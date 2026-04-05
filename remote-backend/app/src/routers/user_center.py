import json
import math
import re
from decimal import Decimal, ROUND_FLOOR
from datetime import datetime, timedelta, timezone
from typing import Any, Dict, Optional

import httpx
from fastapi import APIRouter, BackgroundTasks, Depends, HTTPException, status, Request
from pydantic import BaseModel, Field
from sqlalchemy.orm import Session

from core.afdian_service import afdian_service
from core.config import settings
from core.rate_limiter import enforce_user_center_rate_limit
from core.dependencies import (
    get_db,
    get_effective_user_limit_rule,
    hash_password,
    is_user_expired,
    verify_password,
)
from core.logging_config import logger
from db.base import SessionLocal
from db.models import (
    PaymentOrder,
    User,
    normalize_payment_lookup_value,
    payment_lookup_digest,
    set_payment_order_custom_id,
    set_payment_order_remark,
)

router = APIRouter()


DAY_LIMIT_RULE_PATTERN = re.compile(r"^\s*(\d+)\s*/\s*day\s*$", re.IGNORECASE)
NUMBER_PATTERN = re.compile(r"-?\d+(?:\.\d+)?")
ACTION_ALIASES = {
    "renew_1m": "renew",
    "boost_1m": "upgrade",
    "renew": "renew",
    "upgrade": "upgrade",
    "downgrade": "downgrade",
    "addon": "addon",
}
PRICE_EPSILON = 0.0001


def _is_test_user(username: str) -> bool:
    configured = (settings.LIMIT_PUBLIC_TEST_USER or "").strip()
    if configured and username == configured:
        return True
    return username.lower() == "testuser"


class PaymentLinkRequest(BaseModel):
    username: str = Field(..., min_length=3, max_length=50)
    password: str = Field(..., min_length=1, max_length=100)
    action: str = Field("renew")
    months: int = Field(1, ge=1, le=24)
    target_monthly_price: Optional[float] = None
    guide_token: Optional[str] = None
    plan_price: Optional[float] = None
    custom_price: Optional[float] = None


class PaymentGuideRequest(BaseModel):
    username: str = Field(..., min_length=3, max_length=50)
    password: str = Field(..., min_length=1, max_length=100)


def _get_price_step() -> float:
    # Spec: fixed 5 CNY step.
    return 5.0


def _get_resource_increment_per_step() -> int:
    return max(1, int(settings.AFDIAN_BOOST_DAILY_INCREMENT or 1500))


def _is_new_quota_purchase_enabled() -> bool:
    return not settings.LEGACY_CLIENT_COMPAT


def _get_addon_price_step() -> float:
    return max(0.01, float(settings.AFDIAN_ADDON_PRICE_STEP or 5.0))


def _get_addon_resource_increment() -> int:
    return max(1, int(settings.AFDIAN_ADDON_RESOURCE_INCREMENT or 5000))


def _normalize_addon_amount(raw_price: Optional[float]) -> float:
    price = _safe_parse_amount(raw_price)
    if price is None:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="加油包金额无效")

    step = _get_addon_price_step()
    units = float(Decimal(str(price / step)).to_integral_value(rounding=ROUND_FLOOR))
    normalized = round(units * step, 2)
    if normalized + PRICE_EPSILON < step:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"加油包金额最低为 {step:.2f}",
        )
    return normalized


def _calculate_addon_resource_from_amount(amount: float) -> int:
    step = _get_addon_price_step()
    increment = _get_addon_resource_increment()
    units = int((Decimal(str(amount)) / Decimal(str(step))).to_integral_value(rounding=ROUND_FLOOR))
    return max(0, units) * increment


def _build_payment_guide_token(username: str, payload: Dict[str, Any]) -> str:
    return afdian_service.build_custom_id(
        username=username,
        password="guide",
        action="payment_guide",
        extra_payload=payload,
    )


def _parse_payment_guide_token(token: str) -> Dict[str, Any]:
    payload = afdian_service.parse_custom_id(token)
    if str(payload.get("action") or "").strip().lower() != "payment_guide":
        raise ValueError("guide_token invalid")
    return payload


class ActivateRequest(BaseModel):
    remark: Optional[str] = None
    custom_id: Optional[str] = None
    out_trade_no: Optional[str] = None


AFDIAN_WEBHOOK_OK = {"ec": 200, "em": ""}
AFDIAN_WEBHOOK_FORWARD_HEADER = "X-VoiceLink-Afdian-Forwarded"


def _build_forward_response_preview(text: str, limit: int = 200) -> str:
    normalized = (text or "").strip()
    if len(normalized) <= limit:
        return normalized
    return f"{normalized[:limit]}..."


async def _forward_afdian_webhook(payload: Dict[str, Any], already_forwarded: bool = False) -> None:
    if not settings.AFDIAN_WEBHOOK_FORWARD_ENABLED:
        return

    forward_url = (settings.AFDIAN_WEBHOOK_FORWARD_URL or "").strip()
    if not forward_url:
        return

    if already_forwarded:
        logger.info("[AFDIAN-WEBHOOK] Skip forward for already forwarded payload")
        return

    headers = {AFDIAN_WEBHOOK_FORWARD_HEADER: "1"}
    authorization = (settings.AFDIAN_WEBHOOK_FORWARD_AUTHORIZATION or "").strip()
    if authorization:
        headers["Authorization"] = authorization

    timeout = max(1.0, float(settings.AFDIAN_WEBHOOK_FORWARD_TIMEOUT or 10.0))
    try:
        async with httpx.AsyncClient(timeout=timeout) as client:
            response = await client.post(forward_url, json=payload, headers=headers)
            response_preview = _build_forward_response_preview(response.text)
            if response.status_code >= 400:
                logger.warning(
                    "[AFDIAN-WEBHOOK] Forward failed. status=%s url=%s body=%s",
                    response.status_code,
                    forward_url,
                    response_preview,
                )
                return

            logger.info(
                "[AFDIAN-WEBHOOK] Forwarded payload to bot plugin. status=%s url=%s body=%s",
                response.status_code,
                forward_url,
                response_preview,
            )
    except Exception as exc:
        logger.warning("[AFDIAN-WEBHOOK] Forward failed for url=%s error=%s", forward_url, exc)


async def _process_afdian_webhook_payload(
    payload: Dict[str, Any],
    already_forwarded: bool = False,
) -> None:
    db = SessionLocal()
    try:
        await _forward_afdian_webhook(payload, already_forwarded=already_forwarded)

        data = payload.get("data")
        event_type = str(data.get("type") or "").strip().lower() if isinstance(data, dict) else ""
        if event_type and event_type != "order":
            logger.info("[AFDIAN-WEBHOOK] Ignore unsupported event type: %s", event_type)
            return

        webhook_order = afdian_service.extract_webhook_order(payload)
        if webhook_order is None:
            logger.warning("[AFDIAN-WEBHOOK] Missing order info in payload")
            return

        out_trade_no = afdian_service.get_out_trade_no(webhook_order) or None
        direct_custom_id = afdian_service.get_explicit_custom_id(webhook_order) or None

        if direct_custom_id:
            result = await activate_remote_afdian_order(
                db=db,
                remote_order=webhook_order,
                source_payload=payload,
                source="webhook_direct",
                fallback_custom_id=direct_custom_id,
                allow_remark_custom_id=False,
                allow_local_custom_id_fallback=False,
            )
            logger.info(
                "[AFDIAN-WEBHOOK] Direct activation finished. out_trade_no=%s idempotent=%s",
                out_trade_no,
                result.get("idempotent"),
            )
            return

        if not out_trade_no:
            logger.warning("[AFDIAN-WEBHOOK] Missing out_trade_no in payload without direct custom_id")
            return

        query_payload = await afdian_service.query_order(out_trade_no=out_trade_no)
        query_orders = afdian_service.extract_orders(query_payload)
        queried_order = next(
            (item for item in query_orders if afdian_service.get_out_trade_no(item) == out_trade_no),
            query_orders[0] if query_orders else None,
        )
        if queried_order is None:
            logger.warning("[AFDIAN-WEBHOOK] Query confirmation found no order. out_trade_no=%s", out_trade_no)
            return

        queried_custom_id = afdian_service.get_explicit_custom_id(queried_order) or None
        if not queried_custom_id:
            logger.warning("[AFDIAN-WEBHOOK] Query confirmation missing custom_order_id. out_trade_no=%s", out_trade_no)
            return

        result = await activate_remote_afdian_order(
            db=db,
            remote_order=queried_order,
            source_payload=query_payload,
            source="webhook_query_confirmed",
            fallback_custom_id=queried_custom_id,
            allow_remark_custom_id=False,
            allow_local_custom_id_fallback=False,
        )
        logger.info(
            "[AFDIAN-WEBHOOK] Query activation finished. out_trade_no=%s idempotent=%s",
            out_trade_no,
            result.get("idempotent"),
        )
    except HTTPException as exc:
        logger.exception("[AFDIAN-WEBHOOK] Background processing failed. detail=%s", exc.detail)
    except Exception:
        logger.exception("[AFDIAN-WEBHOOK] Unexpected background processing error")
    finally:
        db.close()


@router.post("/payment-guide")
async def payment_guide(
    request: Request,
    payload: PaymentGuideRequest,
    db: Session = Depends(get_db),
    _: None = Depends(enforce_user_center_rate_limit),
):
    username = payload.username.strip()
    if _is_test_user(username):
        return {
            "username": username,
            "is_test_user": True,
            "status": "test_user",
        }

    user = db.query(User).filter(User.username == username).first()
    if user and not verify_password(payload.password, user.password):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="用户名或密码错误")

    # For non-existing users, still allow guide generation based on default rule.
    return _build_payment_guide_response(user, username)


def _utc_now_naive() -> datetime:
    return datetime.now(timezone.utc).replace(tzinfo=None)


def _normalize_naive_utc(value: Optional[datetime]) -> Optional[datetime]:
    if value is None:
        return None
    if value.tzinfo is None:
        return value
    return value.astimezone(timezone.utc).replace(tzinfo=None)


def _to_utc_iso(value: Optional[datetime]) -> Optional[str]:
    if value is None:
        return None
    if value.tzinfo is None:
        value = value.replace(tzinfo=timezone.utc)
    else:
        value = value.astimezone(timezone.utc)
    return value.isoformat().replace("+00:00", "Z")


def _extend_date(current: Optional[datetime], days: int) -> datetime:
    now_utc = _utc_now_naive()
    base = _normalize_naive_utc(current) or now_utc
    if base < now_utc:
        base = now_utc
    return base + timedelta(days=max(1, days))


def _has_active_subscription(user: Optional[User]) -> bool:
    if user is None or not user.is_active:
        return False
    is_expired, _ = is_user_expired(user)
    return not is_expired


def _clear_expired_addon_balance(user: Optional[User]) -> None:
    if user is None:
        return

    now_utc = _utc_now_naive()
    addon_expiration = _normalize_naive_utc(getattr(user, "addon_expiration_date", None))
    subscription_expiration = _normalize_naive_utc(getattr(user, "expiration_date", None))
    if (
        (addon_expiration is not None and addon_expiration <= now_utc)
        or (subscription_expiration is not None and subscription_expiration <= now_utc)
    ):
        user.addon_resource_balance = 0
        user.addon_expiration_date = None


def _sync_addon_expiration_with_subscription(user: Optional[User]) -> None:
    if user is None or not _is_new_quota_purchase_enabled():
        return

    _clear_expired_addon_balance(user)
    if int(getattr(user, "addon_resource_balance", 0) or 0) > 0 and user.expiration_date is not None:
        user.addon_expiration_date = user.expiration_date


def _safe_parse_amount(value: object) -> Optional[float]:
    if value is None:
        return None
    if isinstance(value, (int, float)):
        amount = float(value)
        return amount if amount >= 0 else None
    if not isinstance(value, str):
        return None

    cleaned = value.replace(",", "").strip()
    if not cleaned:
        return None

    try:
        amount = float(cleaned)
        return amount if amount >= 0 else None
    except ValueError:
        match = NUMBER_PATTERN.search(cleaned)
        if not match:
            return None
        amount = float(match.group(0))
        return amount if amount >= 0 else None


def _extract_order_amount(order: dict) -> Optional[float]:
    for key in ("show_amount", "total_amount", "amount", "pay_amount"):
        amount = _safe_parse_amount(order.get(key))
        if amount is not None:
            return amount
    return None


def _extract_daily_limit(limit_rule: Optional[str]) -> Optional[int]:
    if not limit_rule:
        return None
    for part in (segment.strip() for segment in limit_rule.split(";") if segment.strip()):
        matched = DAY_LIMIT_RULE_PATTERN.match(part)
        if matched:
            return int(matched.group(1))
    return None


def _normalize_action(raw_action: Optional[str]) -> str:
    action = (raw_action or "").strip().lower()
    normalized = ACTION_ALIASES.get(action)
    if not normalized:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="不支持的操作类型")
    return normalized


def _get_user_price_and_limit_rule(user: Optional[User]) -> tuple[float, str]:
    if user is None:
        rule = (settings.AFDIAN_DEFAULT_LIMIT_RULE or "").strip()
        return _infer_plan_price_from_rule(rule), rule

    effective_rule = get_effective_user_limit_rule(user)
    price = _infer_plan_price_from_rule(effective_rule)
    return price, effective_rule


def _is_downgrade_allowed(user: Optional[User], is_expired: bool, current_price: float) -> bool:
    # Spec: allow downgrade when account expired.
    if user is None:
        return False
    if not is_expired:
        return False
    return current_price > _get_price_step() + PRICE_EPSILON


def _is_first_purchase_like(user: Optional[User], is_expired: bool) -> bool:
    return user is None or is_expired


def _build_payment_guide_response(user: Optional[User], username: str) -> Dict[str, Any]:
    price_step = int(round(_get_price_step()))
    resource_step = _get_resource_increment_per_step()
    addon_step = round(_get_addon_price_step(), 2)
    addon_resource_step = _get_addon_resource_increment()
    current_price, effective_rule = _get_user_price_and_limit_rule(user)

    is_registered = user is not None

    if not is_registered:
        current_price = float(price_step)

    is_expired = False
    if user is not None:
        is_expired, _ = is_user_expired(user)

    first_purchase_like = _is_first_purchase_like(user, is_expired)
    allowed_actions = ["renew"] if first_purchase_like else ["renew", "upgrade"]
    if is_registered and _is_downgrade_allowed(user, is_expired, current_price):
        allowed_actions.append("downgrade")
    addon_allowed = _is_new_quota_purchase_enabled() and _has_active_subscription(user)
    if addon_allowed:
        allowed_actions.append("addon")

    guide_payload = {
        "v": 1,
        "ts": int(datetime.now(timezone.utc).timestamp()),
        "current_price": round(float(current_price), 2),
        "price_step": price_step,
        "resource_step": resource_step,
        "effective_rule": effective_rule,
        "is_expired": is_expired,
    }
    guide_token = _build_payment_guide_token(username, guide_payload)

    renew_option = {
        "allowed": True,
        "readonly": True,
        "default_monthly_price": round(float(current_price), 2),
        "min_monthly_price": round(float(current_price), 2),
        "max_monthly_price": round(float(current_price), 2),
        "step": price_step,
        "target_resource_quota_preview": _calculate_daily_limit_from_price(current_price),
    }

    if first_purchase_like:
        # Unregistered or expired accounts can choose any first-purchase plan.
        renew_option = {
            "allowed": True,
            "readonly": False,
            "default_monthly_price": round(float(current_price if is_registered else price_step), 2),
            "min_monthly_price": float(price_step),
            "max_monthly_price": None,
            "step": price_step,
            "target_resource_quota_preview": _calculate_daily_limit_from_price(
                float(current_price if is_registered else price_step)
            ),
        }

    upgrade_option = {
        "allowed": bool(is_registered and not first_purchase_like),
        "readonly": False,
        "default_monthly_price": round(float(max(current_price + price_step, price_step)), 2),
        "min_monthly_price": round(float(max(current_price + price_step, price_step)), 2),
        "max_monthly_price": None,
        "step": price_step,
        "target_resource_quota_preview": _calculate_daily_limit_from_price(float(max(current_price + price_step, price_step))),
    }

    downgrade_allowed = bool(is_registered) and _is_downgrade_allowed(user, is_expired, current_price)
    downgrade_option = {
        "allowed": downgrade_allowed,
        "readonly": False,
        "default_monthly_price": round(float(max(price_step, current_price - price_step)), 2),
        "min_monthly_price": float(price_step),
        "max_monthly_price": round(float(max(price_step, current_price - price_step)), 2),
        "step": price_step,
        "target_resource_quota_preview": _calculate_daily_limit_from_price(float(max(price_step, current_price - price_step))),
    }

    addon_option = {
        "allowed": addon_allowed,
        "readonly": False,
        "default_amount": addon_step,
        "min_amount": addon_step,
        "max_amount": None,
        "step": addon_step,
        "resource_increment": addon_resource_step,
        "default_resource_quota_preview": _calculate_addon_resource_from_amount(addon_step),
        "requires_active_subscription": True,
    }

    display_monthly_price = round(float(current_price), 2)
    display_plan_name = (
        f"￥{display_monthly_price:.2f}/月" if is_registered else "首次开通（自选档位）"
    )
    display_resource_summary = (
        f"{_calculate_daily_limit_from_price(current_price)}/day"
        if is_registered
        else f"起步 {_calculate_daily_limit_from_price(float(price_step))}/day"
    )

    anchor_reason = (
        "unregistered"
        if not is_registered
        else ("expired_downgrade_allowed" if downgrade_allowed else "current")
    )

    return {
        "username": username,
        "is_test_user": _is_test_user(username),
        "is_registered": is_registered,
        "is_expired": is_expired,
        "status": "unregistered" if not is_registered else "ok",
        "price_step": price_step,
        "resource_increment_per_step": resource_step,
        "display_plan_name": display_plan_name,
        "display_monthly_price": display_monthly_price,
        "display_resource_quota": _calculate_daily_limit_from_price(current_price),
        "display_resource_summary": display_resource_summary,
        "anchor_reason": anchor_reason,
        "allowed_actions": allowed_actions,
        "renew_option": renew_option,
        "upgrade_option": upgrade_option,
        "downgrade_option": downgrade_option,
        "addon_option": addon_option,
        "addon_enabled": _is_new_quota_purchase_enabled(),
        "addon_resource_balance": int(getattr(user, "addon_resource_balance", 0) or 0) if user is not None else 0,
        "addon_resource_total": int(getattr(user, "addon_resource_total", 0) or 0) if user is not None else 0,
        "addon_expiration_utc": _to_utc_iso(getattr(user, "addon_expiration_date", None)) if user is not None else None,
        "guide_token": guide_token,
    }


def _normalize_months(raw_months: object) -> int:
    try:
        months = int(raw_months)
    except (TypeError, ValueError):
        months = 1
    return max(1, min(24, months))


def _normalize_plan_price(raw_price: Optional[float]) -> float:
    price = _safe_parse_amount(raw_price)
    if price is None:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="套餐价格无效")

    step = _get_price_step()
    if price + PRICE_EPSILON < step:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"套餐月价最低为 {step:.2f}",
        )

    units = float(Decimal(str(price / step)).to_integral_value(rounding=ROUND_FLOOR))
    normalized = round(units * step, 2)
    if normalized + PRICE_EPSILON < step:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"套餐月价最低为 {step:.2f}",
        )
    return normalized


def _calculate_daily_limit_from_price(plan_price: float) -> int:
    step = _get_price_step()
    increment = _get_resource_increment_per_step()
    units = int((Decimal(str(plan_price)) / Decimal(str(step))).to_integral_value(rounding=ROUND_FLOOR))
    return max(1, units) * increment


def _calculate_price_from_daily_limit(daily_limit: Optional[int]) -> Optional[float]:
    if daily_limit is None or daily_limit <= 0:
        return None
    increment = _get_resource_increment_per_step()
    step = _get_price_step()
    units = max(1, int((Decimal(str(daily_limit)) / Decimal(str(increment))).to_integral_value(rounding=ROUND_FLOOR)))
    return round(float(Decimal(units) * Decimal(str(step))), 2)


def _infer_plan_price_from_rule(limit_rule: Optional[str]) -> float:
    daily_limit = _extract_daily_limit(limit_rule)
    inferred = _calculate_price_from_daily_limit(daily_limit)
    if inferred is not None:
        return inferred
    return round(_get_price_step(), 2)


def _replace_daily_limit_rule(base_rule: Optional[str], daily_limit: int) -> str:
    desired_daily = max(1, int(daily_limit))
    parts = [segment.strip() for segment in (base_rule or "").split(";") if segment.strip()]
    if not parts:
        return f"{desired_daily}/day"

    updated_parts = []
    replaced = False
    for part in parts:
        matched = DAY_LIMIT_RULE_PATTERN.match(part)
        if matched and not replaced:
            updated_parts.append(f"{desired_daily}/day")
            replaced = True
        else:
            updated_parts.append(part)

    if not replaced:
        updated_parts.insert(0, f"{desired_daily}/day")
    return ";".join(updated_parts)


def _build_limit_rule_for_plan(base_rule: Optional[str], plan_price: float) -> str:
    return _replace_daily_limit_rule(base_rule, _calculate_daily_limit_from_price(plan_price))


def _build_new_afdian_user(username: str, password: str) -> User:
    return User(
        username=username,
        password=hash_password(password),
        is_admin=False,
        is_active=True,
        limit_rule=settings.AFDIAN_DEFAULT_LIMIT_RULE,
    )


def _find_local_order(
    db: Session,
    out_trade_no: Optional[str],
    order_no: Optional[str],
    custom_id: Optional[str],
) -> Optional[PaymentOrder]:
    if out_trade_no:
        order = db.query(PaymentOrder).filter(PaymentOrder.out_trade_no == out_trade_no).first()
        if order:
            return order
    if order_no:
        order = db.query(PaymentOrder).filter(PaymentOrder.order_no == order_no).first()
        if order:
            return order
    lookup_value = normalize_payment_lookup_value(custom_id)
    lookup_digest = payment_lookup_digest(lookup_value)
    if lookup_digest:
        order = db.query(PaymentOrder).filter(PaymentOrder.custom_id_digest == lookup_digest).first()
        if order and normalize_payment_lookup_value(order.custom_id) == lookup_value:
            return order

        order = db.query(PaymentOrder).filter(PaymentOrder.remark_digest == lookup_digest).first()
        if order and normalize_payment_lookup_value(order.remark) == lookup_value:
            return order
    return None


def _ensure_order_record(
    db: Session,
    order_no: Optional[str],
    out_trade_no: Optional[str],
    custom_id: Optional[str],
) -> PaymentOrder:
    record = _find_local_order(db, out_trade_no, order_no, custom_id)
    if record is None:
        record = PaymentOrder(
            order_no=order_no or None,
            out_trade_no=out_trade_no or None,
            status="pending",
            created_at=_utc_now_naive(),
            updated_at=_utc_now_naive(),
        )
        set_payment_order_custom_id(record, custom_id)
        set_payment_order_remark(record, custom_id)
        db.add(record)
    return record


def _build_remote_payload_snapshot(source: str, remote_order: dict, source_payload: Optional[dict]) -> str:
    return json_dumps_safe(
        {
            "source": source,
            "remote_order": remote_order,
            "source_payload": source_payload,
        }
    )


def _record_order_activation_failure(
    db: Session,
    order: Optional[PaymentOrder],
    detail: str,
    source: str,
    remote_order: Optional[dict] = None,
    source_payload: Optional[dict] = None,
    extra_payload: Optional[Dict[str, Any]] = None,
) -> None:
    if order is None:
        return

    order.activation_error = detail
    order.activation_error_at = _utc_now_naive()
    order.updated_at = _utc_now_naive()
    order.raw_payload = json_dumps_safe(
        {
            "source": source,
            "activation_error": detail,
            "remote_order": remote_order,
            "source_payload": source_payload,
            "extra_payload": extra_payload,
        }
    )
    db.commit()


def _clear_order_activation_failure(order: PaymentOrder) -> None:
    order.activation_error = None
    order.activation_error_at = None


def _set_order_activation_source(order: Optional[PaymentOrder], source: Optional[str]) -> None:
    if order is None:
        return
    order.activation_source = (source or "").strip() or None


def _sync_local_order_from_remote(
    db: Session,
    remote_order: dict,
    source_payload: Optional[dict],
    source: str,
    fallback_custom_id: Optional[str] = None,
    allow_remark_custom_id: bool = True,
    allow_local_custom_id_fallback: bool = True,
) -> tuple[PaymentOrder, Optional[str], Optional[str], Optional[str], Optional[float]]:
    order_no = afdian_service.get_order_no(remote_order) or None
    out_trade_no = afdian_service.get_out_trade_no(remote_order) or None
    custom_id_getter = afdian_service.get_custom_id if allow_remark_custom_id else afdian_service.get_explicit_custom_id
    custom_id = custom_id_getter(remote_order) or fallback_custom_id or None

    local_order = _find_local_order(db, out_trade_no, order_no, custom_id)
    if allow_local_custom_id_fallback and local_order is not None and not custom_id:
        custom_id = local_order.custom_id or None

    local_order = local_order or _ensure_order_record(db, order_no, out_trade_no, custom_id)

    remote_amount = _extract_order_amount(remote_order)
    if order_no:
        local_order.order_no = order_no
    if out_trade_no:
        local_order.out_trade_no = out_trade_no
    if custom_id:
        set_payment_order_custom_id(local_order, custom_id)
    remark = str(remote_order.get("remark") or "").strip()
    if remark:
        set_payment_order_remark(local_order, remark)
    elif custom_id:
        set_payment_order_remark(local_order, custom_id)
    if remote_amount is not None:
        local_order.amount = remote_amount
    if local_order.status != "activated":
        local_order.status = "pending"
    local_order.updated_at = _utc_now_naive()
    local_order.raw_payload = _build_remote_payload_snapshot(source, remote_order, source_payload)

    return local_order, order_no, out_trade_no, custom_id, remote_amount


async def activate_remote_afdian_order(
    db: Session,
    remote_order: dict,
    source_payload: Optional[dict],
    source: str,
    fallback_custom_id: Optional[str] = None,
    allow_remark_custom_id: bool = True,
    allow_local_custom_id_fallback: bool = True,
) -> Dict[str, Any]:
    local_order, order_no, out_trade_no, custom_id, amount = _sync_local_order_from_remote(
        db=db,
        remote_order=remote_order,
        source_payload=source_payload,
        source=source,
        fallback_custom_id=fallback_custom_id,
        allow_remark_custom_id=allow_remark_custom_id,
        allow_local_custom_id_fallback=allow_local_custom_id_fallback,
    )

    if local_order.status == "activated":
        _clear_order_activation_failure(local_order)
        if not getattr(local_order, "activation_source", None):
            _set_order_activation_source(local_order, source)
        local_order.raw_payload = _build_remote_payload_snapshot(
            local_order.activation_source or source,
            remote_order,
            source_payload,
        )
        db.commit()
        return {
            "message": "订单已激活，无需重复提交",
            "idempotent": True,
            "username": local_order.username,
            "out_trade_no": local_order.out_trade_no,
            "order_no": local_order.order_no,
            "action": local_order.action,
            "activation_source": local_order.activation_source,
        }

    if not afdian_service.is_paid_order(remote_order):
        detail = "订单尚未支付，无法激活"
        _record_order_activation_failure(db, local_order, detail, source, remote_order, source_payload)
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=detail)

    if not custom_id:
        detail = "订单缺少 custom_id/remark，无法激活"
        _record_order_activation_failure(db, local_order, detail, source, remote_order, source_payload)
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=detail)

    try:
        custom_payload = afdian_service.parse_custom_id(custom_id)
    except ValueError as exc:
        detail = str(exc)
        _record_order_activation_failure(db, local_order, detail, source, remote_order, source_payload)
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=detail) from exc

    if afdian_service.is_custom_id_expired(custom_payload):
        detail = "custom_id 已过期，请重新生成发电链接"
        _record_order_activation_failure(
            db,
            local_order,
            detail,
            source,
            remote_order,
            source_payload,
            {"custom_payload": custom_payload},
        )
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=detail) from None

    username = str(custom_payload.get("username") or "").strip()
    password = str(custom_payload.get("password") or "")
    try:
        action = _normalize_action(str(custom_payload.get("action") or "").strip().lower())
        months = _normalize_months(custom_payload.get("months") or 1)
    except HTTPException as exc:
        detail = str(exc.detail)
        _record_order_activation_failure(
            db,
            local_order,
            detail,
            source,
            remote_order,
            source_payload,
            {"custom_payload": custom_payload},
        )
        raise
    if not username or not password:
        detail = "custom_id 信息不完整"
        _record_order_activation_failure(
            db,
            local_order,
            detail,
            source,
            remote_order,
            source_payload,
            {"custom_payload": custom_payload},
        )
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=detail)

    user = db.query(User).filter(User.username == username).first()
    is_new_user = user is None
    if user is not None:
        if not verify_password(password, user.password):
            logger.warning(
                "[USER-CENTER] activate bypassed password re-check for existing user. username=%s out_trade_no=%s",
                username,
                out_trade_no,
            )
        _clear_expired_addon_balance(user)

    if amount is None:
        amount = _safe_parse_amount(getattr(local_order, "amount", None))

    addon_resource_amount = 0
    plan_price_raw = custom_payload.get("plan_price")
    if action == "addon":
        if plan_price_raw is None:
            plan_price_raw = amount
        try:
            plan_price = _normalize_addon_amount(plan_price_raw)
        except HTTPException as exc:
            detail = str(exc.detail)
            _record_order_activation_failure(
                db,
                local_order,
                detail,
                source,
                remote_order,
                source_payload,
                {"custom_payload": custom_payload, "plan_price_raw": plan_price_raw},
            )
            raise
        addon_resource_amount = _calculate_addon_resource_from_amount(plan_price)
    else:
        if plan_price_raw is None and amount is not None:
            plan_price_raw = round(amount / max(1, months), 2)
        if plan_price_raw is None:
            base_rule = user.limit_rule if user is not None else settings.AFDIAN_DEFAULT_LIMIT_RULE
            plan_price_raw = _infer_plan_price_from_rule(base_rule)
        try:
            plan_price = _normalize_plan_price(float(plan_price_raw))
        except HTTPException as exc:
            detail = str(exc.detail)
            _record_order_activation_failure(
                db,
                local_order,
                detail,
                source,
                remote_order,
                source_payload,
                {"custom_payload": custom_payload, "plan_price_raw": plan_price_raw},
            )
            raise

    if amount is None:
        amount = round(plan_price if action == "addon" else plan_price * months, 2)

    requested_amount_raw = custom_payload.get("requested_amount")
    requested_amount = _safe_parse_amount(requested_amount_raw)
    if requested_amount is None:
        requested_amount = round(plan_price if action == "addon" else plan_price * months, 2)
    if amount + PRICE_EPSILON < requested_amount:
        detail = "订单支付金额不足，无法完成激活"
        _record_order_activation_failure(
            db,
            local_order,
            detail,
            source,
            remote_order,
            source_payload,
            {
                "custom_payload": custom_payload,
                "requested_amount": requested_amount,
                "actual_amount": amount,
            },
        )
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=detail)

    current_price, _ = _get_user_price_and_limit_rule(user)
    is_expired = False
    if not is_new_user:
        is_expired, _ = is_user_expired(user)
    first_purchase_like = _is_first_purchase_like(None if is_new_user else user, is_expired)
    converted_remaining_days: Optional[float] = None

    if action == "renew":
        if not first_purchase_like and plan_price > current_price + PRICE_EPSILON:
            detail = "续费不支持升级套餐，请选择升级操作"
            _record_order_activation_failure(
                db,
                local_order,
                detail,
                source,
                remote_order,
                source_payload,
                {"custom_payload": custom_payload},
            )
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=detail)

        if is_new_user:
            user = _build_new_afdian_user(username, password)
            db.add(user)
        final_plan_price = plan_price if first_purchase_like else current_price
        extend_days = int(settings.AFDIAN_RENEW_DAYS) * months
        user.expiration_date = _extend_date(user.expiration_date, extend_days)
        user.limit_rule = _build_limit_rule_for_plan(
            user.limit_rule or settings.AFDIAN_DEFAULT_LIMIT_RULE,
            final_plan_price,
        )
        user.boost_expiration_date = None
        user.boost_limit_rule = None
        user.is_active = True
        _sync_addon_expiration_with_subscription(user)
    elif action == "upgrade":
        if amount is None:
            detail = "订单金额解析失败，无法计算提升档位"
            _record_order_activation_failure(
                db,
                local_order,
                detail,
                source,
                remote_order,
                source_payload,
                {"custom_payload": custom_payload},
            )
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=detail)
        if plan_price <= current_price + PRICE_EPSILON:
            detail = "升级套餐必须高于当前套餐，不支持降级或同级升级"
            _record_order_activation_failure(
                db,
                local_order,
                detail,
                source,
                remote_order,
                source_payload,
                {"custom_payload": custom_payload},
            )
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=detail)

        if is_new_user:
            user = _build_new_afdian_user(username, password)
            db.add(user)
        now_utc = _utc_now_naive()
        current_expiration = _normalize_naive_utc(user.expiration_date) or now_utc
        remaining_seconds = max(0.0, (current_expiration - now_utc).total_seconds())
        converted_seconds = remaining_seconds * (current_price / plan_price)
        converted_remaining_days = round(converted_seconds / 86400.0, 2)

        renew_day_unit = int(settings.AFDIAN_RENEW_DAYS)
        purchased_days = (float(amount) / float(plan_price)) * renew_day_unit

        total_days = converted_remaining_days + purchased_days
        total_days_int = max(1, int(math.ceil(total_days)))
        converted_remaining_days = float(total_days_int)

        user.expiration_date = now_utc + timedelta(days=total_days_int)
        user.limit_rule = _build_limit_rule_for_plan(
            user.limit_rule or settings.AFDIAN_DEFAULT_LIMIT_RULE,
            plan_price,
        )
        user.boost_expiration_date = None
        user.boost_limit_rule = None
        user.is_active = True
        _sync_addon_expiration_with_subscription(user)
    elif action == "downgrade":
        if not _is_downgrade_allowed(user, is_expired, current_price):
            detail = "当前账号不支持降档"
            _record_order_activation_failure(
                db,
                local_order,
                detail,
                source,
                remote_order,
                source_payload,
                {"custom_payload": custom_payload},
            )
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=detail)
        if plan_price >= current_price - PRICE_EPSILON:
            detail = "降档套餐必须低于当前档位"
            _record_order_activation_failure(
                db,
                local_order,
                detail,
                source,
                remote_order,
                source_payload,
                {"custom_payload": custom_payload},
            )
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=detail)

        user.limit_rule = _build_limit_rule_for_plan(
            user.limit_rule or settings.AFDIAN_DEFAULT_LIMIT_RULE,
            plan_price,
        )
        user.boost_expiration_date = None
        user.boost_limit_rule = None
        user.is_active = True
        _sync_addon_expiration_with_subscription(user)

    if action == "addon":
        if not _is_new_quota_purchase_enabled():
            detail = "加油包仅在新资源模式下可激活"
            _record_order_activation_failure(
                db,
                local_order,
                detail,
                source,
                remote_order,
                source_payload,
                {"custom_payload": custom_payload},
            )
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=detail)
        if is_new_user or not _has_active_subscription(user):
            detail = "加油包需要有效订阅才能激活"
            _record_order_activation_failure(
                db,
                local_order,
                detail,
                source,
                remote_order,
                source_payload,
                {"custom_payload": custom_payload},
            )
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=detail)
        if addon_resource_amount <= 0:
            detail = "加油包资源额度解析失败"
            _record_order_activation_failure(
                db,
                local_order,
                detail,
                source,
                remote_order,
                source_payload,
                {"custom_payload": custom_payload, "plan_price": plan_price},
            )
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=detail)

        user.addon_resource_balance = int(user.addon_resource_balance or 0) + addon_resource_amount
        user.addon_resource_total = int(user.addon_resource_total or 0) + addon_resource_amount
        user.addon_expiration_date = user.expiration_date
        user.is_active = True

    local_order.order_no = order_no
    local_order.out_trade_no = out_trade_no
    set_payment_order_custom_id(local_order, custom_id)
    set_payment_order_remark(local_order, custom_id)
    local_order.username = username
    local_order.action = action
    local_order.amount = amount
    local_order.status = "activated"
    _set_order_activation_source(local_order, source)
    _clear_order_activation_failure(local_order)
    local_order.activated_at = _utc_now_naive()
    local_order.updated_at = _utc_now_naive()
    local_order.raw_payload = json_dumps_safe(
        {
            "source": source,
            "order": remote_order,
            "source_payload": source_payload,
            "custom_payload": custom_payload,
            "months": months,
            "plan_price": plan_price,
            "requested_amount": requested_amount,
            "limit_rule": user.limit_rule,
            "converted_remaining_days": converted_remaining_days,
            "addon_resource_amount": addon_resource_amount,
            "addon_resource_balance": int(getattr(user, "addon_resource_balance", 0) or 0),
        }
    )

    db.commit()

    logger.info(
        "[USER-CENTER] order activated. username=%s action=%s out_trade_no=%s source=%s",
        username,
        action,
        out_trade_no,
        source,
    )
    return {
        "message": "激活成功",
        "idempotent": False,
        "out_trade_no": out_trade_no,
        "order_no": order_no,
        "action": action,
        "amount": amount,
        "months": months,
        "plan_price": plan_price,
        "limit_rule": user.limit_rule,
        "converted_remaining_days": converted_remaining_days,
        "activation_source": local_order.activation_source,
        "addon_resource_amount": addon_resource_amount,
        "addon_resource_balance": int(getattr(user, "addon_resource_balance", 0) or 0),
        "addon_expiration_utc": _to_utc_iso(getattr(user, "addon_expiration_date", None)),
        "new_expiration_utc": _to_utc_iso(user.expiration_date),
        "boost_expiration_utc": _to_utc_iso(getattr(user, "boost_expiration_date", None)),
    }


async def activate_afdian_order_by_lookup(
    db: Session,
    out_trade_no: Optional[str],
    remark_or_custom_id: Optional[str],
    source: str,
) -> Dict[str, Any]:
    remote_order, source_payload = await afdian_service.find_order(out_trade_no, remark_or_custom_id)
    if remote_order is None:
        local_order = _find_local_order(db, out_trade_no, None, remark_or_custom_id)
        detail = "未查询到对应订单"
        _record_order_activation_failure(
            db,
            local_order,
            detail,
            source,
            extra_payload={
                "out_trade_no": out_trade_no,
                "remark_or_custom_id": remark_or_custom_id,
            },
        )
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail=detail)

    return await activate_remote_afdian_order(
        db=db,
        remote_order=remote_order,
        source_payload=source_payload,
        source=source,
        fallback_custom_id=remark_or_custom_id,
    )


@router.post("/payment-link")
async def create_payment_link(
    request: Request,
    payload: PaymentLinkRequest,
    db: Session = Depends(get_db),
    _: None = Depends(enforce_user_center_rate_limit),
):
    action = _normalize_action(payload.action)
    months = _normalize_months(payload.months)

    username = payload.username.strip()
    password = payload.password
    if _is_test_user(username):
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="测试账户不允许直接发电，请填写正式账号")

    user = db.query(User).filter(User.username == username).first()
    if user and not verify_password(password, user.password):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="用户名或密码错误")

    guide_price: Optional[float] = None
    if payload.guide_token:
        try:
            guide_payload = _parse_payment_guide_token(payload.guide_token)
            if afdian_service.is_custom_id_expired(guide_payload):
                raise ValueError("guide_token expired")
            if str(guide_payload.get("username") or "").strip() != username:
                raise ValueError("username mismatch")
            guide_price = _safe_parse_amount(guide_payload.get("current_price"))
        except Exception as exc:
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=f"支付引导令牌无效: {exc}") from exc

    current_price, _ = _get_user_price_and_limit_rule(user)
    is_expired = False
    if user is not None:
        is_expired, _ = is_user_expired(user)
    first_purchase_like = _is_first_purchase_like(user, is_expired)
    addon_resource_amount = 0

    if action == "upgrade" and first_purchase_like:
        action = "renew"

    if action == "addon":
        if not _is_new_quota_purchase_enabled():
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="加油包仅在新资源模式下可购买")
        if not _has_active_subscription(user):
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="加油包需要在有效订阅期间购买")

        if payload.custom_price is not None:
            plan_price = _normalize_addon_amount(payload.custom_price)
        elif payload.plan_price is not None:
            plan_price = _normalize_addon_amount(payload.plan_price)
        elif payload.target_monthly_price is not None:
            plan_price = _normalize_addon_amount(payload.target_monthly_price)
        else:
            plan_price = _normalize_addon_amount(_get_addon_price_step() * months)
        addon_resource_amount = _calculate_addon_resource_from_amount(plan_price)
    else:
        if payload.target_monthly_price is not None:
            plan_price = _normalize_plan_price(payload.target_monthly_price)
        elif payload.plan_price is not None:
            plan_price = _normalize_plan_price(payload.plan_price)
        elif payload.custom_price is not None and (action in ("upgrade", "downgrade") or first_purchase_like):
            plan_price = _normalize_plan_price(payload.custom_price)
        else:
            plan_price = _normalize_plan_price(guide_price if guide_price is not None else current_price)

    if action == "renew" and user and not first_purchase_like and plan_price > current_price + PRICE_EPSILON:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="续费不支持升档，请选择升级")
    if action == "upgrade" and user and plan_price <= current_price + PRICE_EPSILON:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="升级档位必须高于当前档位")
    if action == "downgrade":
        if not _is_downgrade_allowed(user, is_expired, current_price):
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="当前账号不支持降档")
        if user and plan_price >= current_price - PRICE_EPSILON:
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="降档档位必须低于当前档位")

    # 计算订单金额：
    # - renew / 首次购买：按目标月价 * 月数 收费；
    # - upgrade：只收新旧计划差价 * 月数，避免重复支付已购买部分。
    if action == "addon":
        donation_amount = round(plan_price, 2)
    elif action == "upgrade" and user is not None:
        diff_price = max(0.0, float(plan_price) - float(current_price))
        donation_amount = round(diff_price * months, 2)
    else:
        donation_amount = round(plan_price * months, 2)

    if donation_amount <= 0:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="订单金额无效")

    custom_id = afdian_service.build_custom_id(
        username,
        password,
        action,
        extra_payload={
            "months": months,
            "plan_price": plan_price,
            "requested_amount": donation_amount,
            "guide_token": payload.guide_token,
            "target_monthly_price": plan_price,
            "addon_resource_amount": addon_resource_amount,
        },
    )
    donation_url = afdian_service.build_donation_url(custom_id, donation_amount)

    pending = _ensure_order_record(db, order_no=None, out_trade_no=None, custom_id=custom_id)
    set_payment_order_remark(pending, custom_id)
    pending.username = username
    pending.action = action
    pending.amount = donation_amount
    pending.status = "pending"
    pending.updated_at = _utc_now_naive()
    db.commit()

    logger.info("[USER-CENTER] payment-link generated. username=%s action=%s", username, action)
    return {
        "donation_url": donation_url,
        "custom_id": custom_id,
        "remark": custom_id,
        "action": action,
        "months": months,
        "plan_price": plan_price,
        "custom_price": donation_amount,
        "addon_resource_amount": addon_resource_amount,
    }


@router.post("/activate_legacy")
async def activate_order(
    request: Request,
    payload: ActivateRequest,
    db: Session = Depends(get_db),
    _: None = Depends(enforce_user_center_rate_limit),
):
    out_trade_no = (payload.out_trade_no or "").strip() or None
    remark_or_custom_id = (payload.custom_id or payload.remark or "").strip() or None
    if not out_trade_no and not remark_or_custom_id:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="缺少订单备注/custom_id 或商家订单号")

    order, raw_query_payload = await afdian_service.find_order(out_trade_no, remark_or_custom_id)
    if order is None:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="未查询到对应订单")

    order_no = afdian_service.get_order_no(order) or None
    out_trade_no = afdian_service.get_out_trade_no(order) or out_trade_no
    custom_id = afdian_service.get_custom_id(order) or remark_or_custom_id
    if not custom_id:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="订单缺少custom_id/remark，无法激活")

    local_order = _ensure_order_record(db, order_no, out_trade_no, custom_id)
    if local_order.status == "activated":
        return {
            "message": "订单已激活，无需重复提交",
            "idempotent": True,
            "username": local_order.username,
            "out_trade_no": local_order.out_trade_no,
            "order_no": local_order.order_no,
            "action": local_order.action,
            "activation_source": local_order.activation_source,
        }

    try:
        custom_payload = afdian_service.parse_custom_id(custom_id)
    except ValueError as exc:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=str(exc)) from exc

    if afdian_service.is_custom_id_expired(custom_payload):
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="custom_id 已过期，请重新生成发电链接")

    username = str(custom_payload.get("username") or "").strip()
    password = str(custom_payload.get("password") or "")
    action = _normalize_action(str(custom_payload.get("action") or "").strip().lower())
    months = _normalize_months(custom_payload.get("months") or 1)
    if not username or not password:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="custom_id 信息不完整")

    user = db.query(User).filter(User.username == username).first()
    is_new_user = user is None
    if user is not None:
        # Existing accounts were already authenticated when the payment link was
        # issued. Re-checking the current password here breaks activation for
        # valid orders if the user changed their password before paying.
        if not verify_password(password, user.password):
            logger.warning(
                "[USER-CENTER] activate bypassed password re-check for existing user. username=%s out_trade_no=%s",
                username,
                out_trade_no,
            )
        _clear_expired_addon_balance(user)

    amount = _extract_order_amount(order)
    if amount is None:
        amount = _safe_parse_amount(getattr(local_order, "amount", None))

    addon_resource_amount = 0
    plan_price_raw = custom_payload.get("plan_price")
    if action == "addon":
        if plan_price_raw is None:
            plan_price_raw = amount
        plan_price = _normalize_addon_amount(plan_price_raw)
        addon_resource_amount = _calculate_addon_resource_from_amount(plan_price)
    else:
        if plan_price_raw is None and amount is not None:
            plan_price_raw = round(amount / max(1, months), 2)
        if plan_price_raw is None:
            base_rule = user.limit_rule if user is not None else settings.AFDIAN_DEFAULT_LIMIT_RULE
            plan_price_raw = _infer_plan_price_from_rule(base_rule)
        plan_price = _normalize_plan_price(float(plan_price_raw))

    if amount is None:
        amount = round(plan_price if action == "addon" else plan_price * months, 2)

    requested_amount_raw = custom_payload.get("requested_amount")
    requested_amount = _safe_parse_amount(requested_amount_raw)
    if requested_amount is None:
        requested_amount = round(plan_price if action == "addon" else plan_price * months, 2)
    if amount + PRICE_EPSILON < requested_amount:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="订单支付金额不足，无法完成激活")

    current_price, _ = _get_user_price_and_limit_rule(user)
    is_expired = False
    if not is_new_user:
        is_expired, _ = is_user_expired(user)
    first_purchase_like = _is_first_purchase_like(None if is_new_user else user, is_expired)
    converted_remaining_days: Optional[float] = None

    if action == "renew":
        if not first_purchase_like and plan_price > current_price + PRICE_EPSILON:
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="续费不支持升级套餐，请选择升级操作")

        if is_new_user:
            user = _build_new_afdian_user(username, password)
            db.add(user)
        final_plan_price = plan_price if first_purchase_like else current_price
        extend_days = int(settings.AFDIAN_RENEW_DAYS) * months
        user.expiration_date = _extend_date(user.expiration_date, extend_days)
        user.limit_rule = _build_limit_rule_for_plan(user.limit_rule or settings.AFDIAN_DEFAULT_LIMIT_RULE, final_plan_price)
        user.boost_expiration_date = None
        user.boost_limit_rule = None
        user.is_active = True
        _sync_addon_expiration_with_subscription(user)
    elif action == "upgrade":
        if amount is None:
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="订单金额解析失败，无法计算提额档位")
        if plan_price <= current_price + PRICE_EPSILON:
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="升级套餐必须高于当前套餐，不支持降级或同级升级")

        if is_new_user:
            user = _build_new_afdian_user(username, password)
            db.add(user)
        now_utc = _utc_now_naive()
        current_expiration = _normalize_naive_utc(user.expiration_date) or now_utc
        remaining_seconds = max(0.0, (current_expiration - now_utc).total_seconds())

        # 先将旧套餐剩余时间按价格比例折算到新套餐：
        # remaining_days_new = remaining_days_old * (current_price / plan_price)
        converted_seconds = remaining_seconds * (current_price / plan_price)
        converted_remaining_days = round(converted_seconds / 86400.0, 2)

        # 再根据本次实际支付金额在新套餐下可购买的天数进行折算：
        # purchased_days = (amount / plan_price) * AFDIAN_RENEW_DAYS
        renew_day_unit = int(settings.AFDIAN_RENEW_DAYS)
        purchased_days = (float(amount) / float(plan_price)) * renew_day_unit

        total_days = converted_remaining_days + purchased_days
        total_days_int = max(1, int(math.ceil(total_days)))
        converted_remaining_days = float(total_days_int)

        user.expiration_date = now_utc + timedelta(days=total_days_int)
        user.limit_rule = _build_limit_rule_for_plan(user.limit_rule or settings.AFDIAN_DEFAULT_LIMIT_RULE, plan_price)
        user.boost_expiration_date = None
        user.boost_limit_rule = None
        user.is_active = True
        _sync_addon_expiration_with_subscription(user)

    elif action == "downgrade":
        if not _is_downgrade_allowed(user, is_expired, current_price):
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="当前账号不支持降档")
        if plan_price >= current_price - PRICE_EPSILON:
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="降档档位必须低于当前档位")

        # Downgrade only changes limit rule; expiration remains as-is.
        if is_new_user:
            user = _build_new_afdian_user(username, password)
            db.add(user)
        user.limit_rule = _build_limit_rule_for_plan(user.limit_rule or settings.AFDIAN_DEFAULT_LIMIT_RULE, plan_price)
        user.boost_expiration_date = None
        user.boost_limit_rule = None
        user.is_active = True
        _sync_addon_expiration_with_subscription(user)

    if action == "addon":
        if not _is_new_quota_purchase_enabled():
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="加油包仅在新资源模式下可激活")
        if is_new_user or not _has_active_subscription(user):
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="加油包需要有效订阅才能激活")
        if addon_resource_amount <= 0:
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="加油包资源额度解析失败")

        user.addon_resource_balance = int(user.addon_resource_balance or 0) + addon_resource_amount
        user.addon_resource_total = int(user.addon_resource_total or 0) + addon_resource_amount
        user.addon_expiration_date = user.expiration_date
        user.is_active = True

    local_order.order_no = order_no
    local_order.out_trade_no = out_trade_no
    set_payment_order_custom_id(local_order, custom_id)
    set_payment_order_remark(local_order, custom_id)
    local_order.username = username
    local_order.action = action
    local_order.amount = amount
    local_order.status = "activated"
    _set_order_activation_source(local_order, "legacy_manual_activate")
    local_order.activated_at = _utc_now_naive()
    local_order.updated_at = _utc_now_naive()
    local_order.raw_payload = json_dumps_safe(
        {
            "order": order,
            "query_payload": raw_query_payload,
            "custom_payload": custom_payload,
            "months": months,
            "plan_price": plan_price,
            "requested_amount": requested_amount,
            "limit_rule": user.limit_rule,
            "converted_remaining_days": converted_remaining_days,
            "addon_resource_amount": addon_resource_amount,
            "addon_resource_balance": int(getattr(user, "addon_resource_balance", 0) or 0),
        }
    )

    db.commit()

    logger.info(
        "[USER-CENTER] order activated. username=%s action=%s out_trade_no=%s",
        username,
        action,
        out_trade_no,
    )
    return {
        "message": "激活成功",
        "idempotent": False,
        "out_trade_no": out_trade_no,
        "order_no": order_no,
        "action": action,
        "amount": amount,
        "months": months,
        "plan_price": plan_price,
        "limit_rule": user.limit_rule,
        "converted_remaining_days": converted_remaining_days,
        "activation_source": local_order.activation_source,
        "addon_resource_amount": addon_resource_amount,
        "addon_resource_balance": int(getattr(user, "addon_resource_balance", 0) or 0),
        "addon_expiration_utc": _to_utc_iso(getattr(user, "addon_expiration_date", None)),
        "new_expiration_utc": _to_utc_iso(user.expiration_date),
        "boost_expiration_utc": _to_utc_iso(getattr(user, "boost_expiration_date", None)),
    }


@router.post("/activate")
async def activate_order_v2(
    request: Request,
    payload: ActivateRequest,
    db: Session = Depends(get_db),
    _: None = Depends(enforce_user_center_rate_limit),
):
    out_trade_no = (payload.out_trade_no or "").strip() or None
    remark_or_custom_id = (payload.custom_id or payload.remark or "").strip() or None
    if not out_trade_no and not remark_or_custom_id:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="缺少订单备注/custom_id 或商户订单号")

    return await activate_afdian_order_by_lookup(
        db=db,
        out_trade_no=out_trade_no,
        remark_or_custom_id=remark_or_custom_id,
        source="manual_activate",
    )


@router.post("/afdian/webhook")
async def afdian_webhook(request: Request, background_tasks: BackgroundTasks):
    try:
        payload = await request.json()
    except Exception as exc:
        logger.warning("[AFDIAN-WEBHOOK] Invalid JSON payload: %s", exc)
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Webhook 请求体不是合法 JSON") from exc

    if not isinstance(payload, dict):
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Webhook 请求体格式无效")

    background_tasks.add_task(
        _process_afdian_webhook_payload,
        payload,
        request.headers.get(AFDIAN_WEBHOOK_FORWARD_HEADER) == "1",
    )
    return AFDIAN_WEBHOOK_OK

    data = payload.get("data")
    event_type = str(data.get("type") or "").strip().lower() if isinstance(data, dict) else ""
    if event_type and event_type != "order":
        logger.info("[AFDIAN-WEBHOOK] Ignore unsupported event type: %s", event_type)
        return AFDIAN_WEBHOOK_OK

    webhook_order = afdian_service.extract_webhook_order(payload)
    if webhook_order is None:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Webhook 缺少订单信息")

    out_trade_no = afdian_service.get_out_trade_no(webhook_order) or None
    direct_custom_id = afdian_service.get_explicit_custom_id(webhook_order) or None

    if direct_custom_id:
        try:
            result = await activate_remote_afdian_order(
                db=db,
                remote_order=webhook_order,
                source_payload=payload,
                source="webhook_direct",
                fallback_custom_id=direct_custom_id,
                allow_remark_custom_id=False,
                allow_local_custom_id_fallback=False,
            )
            logger.info(
                "[AFDIAN-WEBHOOK] Direct activation finished. out_trade_no=%s idempotent=%s",
                out_trade_no,
                result.get("idempotent"),
            )
            return AFDIAN_WEBHOOK_OK
        except HTTPException:
            logger.exception("[AFDIAN-WEBHOOK] Direct activation failed for out_trade_no=%s", out_trade_no)
            raise

    if not out_trade_no:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Webhook 订单缺少 out_trade_no")

    try:
        query_payload = await afdian_service.query_order(out_trade_no=out_trade_no)
        query_orders = afdian_service.extract_orders(query_payload)
        queried_order = next(
            (item for item in query_orders if afdian_service.get_out_trade_no(item) == out_trade_no),
            query_orders[0] if query_orders else None,
        )
        if queried_order is None:
            raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Webhook 二次查询未找到对应订单")

        queried_custom_id = afdian_service.get_explicit_custom_id(queried_order) or None
        if not queried_custom_id:
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="查询订单缺少 custom_order_id")

        result = await activate_remote_afdian_order(
            db=db,
            remote_order=queried_order,
            source_payload=query_payload,
            source="webhook_query_confirmed",
            fallback_custom_id=queried_custom_id,
            allow_remark_custom_id=False,
            allow_local_custom_id_fallback=False,
        )
        logger.info(
            "[AFDIAN-WEBHOOK] Query activation finished. out_trade_no=%s idempotent=%s",
            out_trade_no,
            result.get("idempotent"),
        )
        return AFDIAN_WEBHOOK_OK
    except HTTPException:
        logger.exception("[AFDIAN-WEBHOOK] Auto activation failed for out_trade_no=%s", out_trade_no)
        raise
    except Exception:
        logger.exception("[AFDIAN-WEBHOOK] Unexpected error for out_trade_no=%s", out_trade_no)
        raise HTTPException(status_code=status.HTTP_500_INTERNAL_SERVER_ERROR, detail="Webhook 自动激活失败") from None


def json_dumps_safe(payload: object) -> str:
    try:
        return json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    except Exception:
        return "{}"
