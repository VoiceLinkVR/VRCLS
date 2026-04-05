import json
from datetime import datetime, time, timedelta, timezone
from typing import Any, Optional

from sqlalchemy import case, desc, func, or_, text
from sqlalchemy.orm import Session

from core.afdian_service import afdian_service
from core.dependencies import hash_password, normalize_email, utcnow_naive
from core.logging_config import logger
from db.models import PaymentOrder, RequestLog, User, normalize_payment_lookup_value, payment_lookup_digest

LOCAL_TIME_OFFSET_HOURS = 8
MAX_HOUR_OPTIONS = 168
MAX_DETAIL_ROWS = 300
PAYMENT_PENDING_EXPIRE_HOURS = 24
EXCLUDED_ENDPOINTS = [
    "/api/login",
    "/api/latestVersionInfo",
    "/api/updates/check",
    "/api/models/list",
    "/api/translations/profile",
    "/api/translators/runtime",
    "/api/translations/capabilities",
]

AFDIAN_ACTION_LABELS = {
    "renew": "renew",
    "upgrade": "upgrade",
}

AFDIAN_ACTIVATION_SOURCE_LABELS = {
    "manual_activate": "Manual activation",
    "admin_manual_activate": "Admin manual activation",
    "legacy_manual_activate": "Legacy manual activation",
    "webhook_direct": "Webhook direct activation",
    "webhook_query_confirmed": "Webhook query confirmation",
}


def _to_utc_iso(value: Optional[datetime]) -> Optional[str]:
    if value is None:
        return None
    if value.tzinfo is None:
        value = value.replace(tzinfo=timezone.utc)
    else:
        value = value.astimezone(timezone.utc)
    return value.isoformat().replace("+00:00", "Z")


def _display_name(user: User) -> str:
    return (getattr(user, "display_name", None) or user.username or "").strip()


def serialize_admin_user(user: User) -> dict[str, Any]:
    return {
        "id": str(user.id),
        "username": user.username,
        "email": (user.email or "").strip(),
        "display_name": _display_name(user),
        "email_verified": bool(getattr(user, "email_verified", False)),
        "is_admin": bool(user.is_admin),
    }


def _format_money(value: Any) -> Optional[str]:
    try:
        return f"CNY {float(value):.2f}"
    except (TypeError, ValueError):
        return None


def _format_local_timestamp(timestamp_value: Any) -> Optional[str]:
    try:
        ts = int(timestamp_value)
    except (TypeError, ValueError):
        return None

    if ts <= 0:
        return None

    return (datetime.utcfromtimestamp(ts) + timedelta(hours=LOCAL_TIME_OFFSET_HOURS)).strftime("%Y-%m-%d %H:%M:%S")


def _parse_json_text(raw_text: Optional[str]) -> Any:
    if not raw_text:
        return None
    try:
        return json.loads(raw_text)
    except (TypeError, ValueError):
        return {"_raw": raw_text}


def _normalize_payment_status(status: Optional[str]) -> str:
    normalized = str(status or "").strip().lower()
    if normalized in {"pending", "activated", "unpaid"}:
        return normalized
    return normalized or "unknown"


def _get_activation_source_label(source: Optional[str]) -> Optional[str]:
    normalized = str(source or "").strip().lower()
    if not normalized:
        return None
    return AFDIAN_ACTIVATION_SOURCE_LABELS.get(normalized, source)


def _build_afdian_token_result(token: str) -> dict[str, Any]:
    parsed = afdian_service.parse_custom_id(token)
    action = str(parsed.get("action") or "").strip().lower()
    try:
        months = int(parsed.get("months")) if parsed.get("months") is not None else None
    except (TypeError, ValueError):
        months = None

    return {
        "token_length": len(token),
        "expired": afdian_service.is_custom_id_expired(parsed),
        "fields": {
            "version": parsed.get("v"),
            "username": parsed.get("username"),
            "password": parsed.get("password"),
            "action": action or None,
            "action_label": AFDIAN_ACTION_LABELS.get(action, action or None),
            "months": months,
            "plan_price": _format_money(parsed.get("plan_price")),
            "requested_amount": _format_money(parsed.get("requested_amount")),
            "timestamp": parsed.get("ts"),
            "generated_at_local": _format_local_timestamp(parsed.get("ts")),
            "nonce": parsed.get("nonce"),
        },
        "payload": parsed,
    }


def close_expired_pending_orders(db: Session) -> int:
    expire_before = datetime.utcnow() - timedelta(hours=PAYMENT_PENDING_EXPIRE_HOURS)
    expired_orders = (
        db.query(PaymentOrder)
        .filter(
            PaymentOrder.status == "pending",
            PaymentOrder.created_at.isnot(None),
            PaymentOrder.created_at <= expire_before,
        )
        .all()
    )

    if not expired_orders:
        return 0

    now_utc = datetime.utcnow()
    for order in expired_orders:
        order.status = "unpaid"
        order.updated_at = now_utc

    db.commit()
    logger.info("[ADMIN] Auto-closed unpaid afdian orders: %s", len(expired_orders))
    return len(expired_orders)


def _build_user_afdian_summary_map(db: Session) -> dict[str, dict[str, Any]]:
    rows = (
        db.query(
            PaymentOrder.username.label("username"),
            func.count(PaymentOrder.id).label("order_count"),
            func.sum(case((PaymentOrder.status == "activated", 1), else_=0)).label("activated_order_count"),
            func.sum(case((PaymentOrder.status == "pending", 1), else_=0)).label("pending_order_count"),
            func.sum(case((PaymentOrder.status == "unpaid", 1), else_=0)).label("unpaid_order_count"),
            func.coalesce(func.sum(case((PaymentOrder.status == "activated", PaymentOrder.amount), else_=0.0)), 0.0).label(
                "activated_amount_total"
            ),
            func.max(PaymentOrder.created_at).label("latest_order_at"),
        )
        .filter(PaymentOrder.username.isnot(None))
        .group_by(PaymentOrder.username)
        .all()
    )

    summary_map: dict[str, dict[str, Any]] = {}
    for row in rows:
        username = str(row.username or "").strip()
        if not username:
            continue
        summary_map[username] = {
            "order_count": int(row.order_count or 0),
            "activated_order_count": int(row.activated_order_count or 0),
            "pending_order_count": int(row.pending_order_count or 0),
            "unpaid_order_count": int(row.unpaid_order_count or 0),
            "closed_order_count": int(row.unpaid_order_count or 0),
            "activated_amount_total": round(float(row.activated_amount_total or 0.0), 2),
            "latest_order_at": _to_utc_iso(row.latest_order_at),
        }

    return summary_map


def _serialize_user_record(user: User, afdian_summary: Optional[dict[str, Any]] = None) -> dict[str, Any]:
    return {
        "id": str(user.id),
        "username": user.username,
        "email": (user.email or "").strip(),
        "display_name": _display_name(user),
        "email_verified": bool(getattr(user, "email_verified", False)),
        "is_admin": bool(user.is_admin),
        "is_active": bool(user.is_active),
        "limit_rule": user.limit_rule,
        "expiration_date": _to_utc_iso(user.expiration_date),
        "boost_expiration_date": _to_utc_iso(getattr(user, "boost_expiration_date", None)),
        "boost_limit_rule": getattr(user, "boost_limit_rule", None),
        "addon_resource_balance": int(getattr(user, "addon_resource_balance", 0) or 0),
        "addon_resource_total": int(getattr(user, "addon_resource_total", 0) or 0),
        "addon_expiration_date": _to_utc_iso(getattr(user, "addon_expiration_date", None)),
        "created_at": _to_utc_iso(getattr(user, "created_at", None)),
        "last_login_at": _to_utc_iso(getattr(user, "last_login_at", None)),
        "afdian_summary": afdian_summary
        or {
            "order_count": 0,
            "activated_order_count": 0,
            "pending_order_count": 0,
            "unpaid_order_count": 0,
            "closed_order_count": 0,
            "activated_amount_total": 0.0,
            "latest_order_at": None,
        },
    }


def list_users_payload(db: Session) -> dict[str, Any]:
    close_expired_pending_orders(db)
    users = db.query(User).order_by(desc(User.created_at), desc(User.id)).all()
    afdian_summary_map = _build_user_afdian_summary_map(db)

    now = datetime.now()
    soon = now + timedelta(days=7)
    summary = {
        "total_users": db.query(func.count(User.id)).scalar() or 0,
        "admin_users": db.query(func.count(User.id)).filter(User.is_admin == True).scalar() or 0,  # noqa: E712
        "active_users": db.query(func.count(User.id)).filter(User.is_active == True).scalar() or 0,  # noqa: E712
        "non_permanent_users": db.query(func.count(User.id)).filter(User.expiration_date.isnot(None)).scalar() or 0,
        "expiring_in_7_days": (
            db.query(func.count(User.id))
            .filter(
                User.expiration_date.isnot(None),
                User.is_active == True,  # noqa: E712
                User.expiration_date >= now,
                User.expiration_date <= soon,
            )
            .scalar()
            or 0
        ),
    }
    return {
        "items": [_serialize_user_record(user, afdian_summary_map.get(user.username)) for user in users],
        "summary": summary,
    }


def get_user_detail_payload(db: Session, user_id: int) -> dict[str, Any]:
    close_expired_pending_orders(db)
    user = db.query(User).filter(User.id == user_id).first()
    if user is None:
        raise ValueError("User not found")

    orders = (
        db.query(PaymentOrder)
        .filter(PaymentOrder.username == user.username)
        .order_by(desc(PaymentOrder.created_at), desc(PaymentOrder.id))
        .all()
    )
    afdian_summary_map = _build_user_afdian_summary_map(db)
    afdian_summary = afdian_summary_map.get(user.username, _serialize_user_record(user)["afdian_summary"])

    latest_parsed_custom_id = None
    for order in orders:
        if not order.custom_id:
            continue
        try:
            latest_parsed_custom_id = afdian_service.parse_custom_id(order.custom_id)
            break
        except ValueError:
            continue

    return {
        "user": _serialize_user_record(user, afdian_summary),
        "afdian_summary": {
            **afdian_summary,
            "has_orders": bool(orders),
            "latest_parsed_custom_id": latest_parsed_custom_id,
        },
        "orders": [_build_afdian_order_payload(order) for order in orders],
    }


def create_user(db: Session, payload: Any) -> dict[str, Any]:
    username = payload.username.strip()
    email = normalize_email(payload.email) if payload.email else None
    if db.query(User).filter(User.username == username).first() is not None:
        raise ValueError("Username already exists")
    if email and db.query(User).filter(User.email == email).first() is not None:
        raise ValueError("Email already exists")

    user = User(
        username=username,
        password=hash_password(payload.password),
        email=email,
        display_name=(payload.display_name or username).strip(),
        email_verified=False,
        is_admin=bool(payload.is_admin),
        is_active=bool(payload.is_active),
        limit_rule=payload.limit_rule,
        expiration_date=payload.expiration_date,
        created_at=utcnow_naive(),
    )
    db.add(user)
    db.commit()
    db.refresh(user)
    return _serialize_user_record(user)


def update_user(db: Session, user_id: int, payload: Any) -> dict[str, Any]:
    user = db.query(User).filter(User.id == user_id).first()
    if user is None:
        raise ValueError("User not found")

    if payload.username is not None:
        username = payload.username.strip()
        exists = db.query(User).filter(User.username == username, User.id != user_id).first()
        if exists is not None:
            raise ValueError("Username already exists")
        user.username = username

    if payload.email is not None:
        email = normalize_email(payload.email) if payload.email else None
        if email:
            exists = db.query(User).filter(User.email == email, User.id != user_id).first()
            if exists is not None:
                raise ValueError("Email already exists")
        user.email = email

    if payload.display_name is not None:
        user.display_name = payload.display_name.strip() or user.username
    if payload.password:
        user.password = hash_password(payload.password)
    if payload.is_admin is not None:
        user.is_admin = payload.is_admin
    if payload.is_active is not None:
        user.is_active = payload.is_active
    if payload.limit_rule is not None:
        user.limit_rule = payload.limit_rule
    if payload.expiration_date is not None:
        user.expiration_date = payload.expiration_date

    db.commit()
    db.refresh(user)
    return _serialize_user_record(user)


def delete_user(db: Session, user_id: int) -> dict[str, Any]:
    user = db.query(User).filter(User.id == user_id).first()
    if user is None:
        raise ValueError("User not found")
    username = user.username
    db.delete(user)
    db.commit()
    return {"message": f"Deleted user {username}"}


def _build_afdian_order_payload(order: PaymentOrder) -> dict[str, Any]:
    parsed_custom_id = None
    custom_id_parse_error = None
    status = _normalize_payment_status(order.status)
    if order.custom_id:
        try:
            parsed_custom_id = afdian_service.parse_custom_id(order.custom_id)
        except ValueError as exc:
            custom_id_parse_error = str(exc)

    return {
        "id": order.id,
        "order_no": order.order_no,
        "out_trade_no": order.out_trade_no,
        "custom_id": order.custom_id,
        "remark": order.remark,
        "username": order.username,
        "action": order.action,
        "amount": round(float(order.amount), 2) if order.amount is not None else None,
        "status": status,
        "status_label": {
            "activated": "Activated",
            "pending": "Pending",
            "unpaid": "Closed",
        }.get(status, status),
        "default_collapsed": status == "unpaid",
        "activated_at": _to_utc_iso(order.activated_at),
        "activation_source": getattr(order, "activation_source", None),
        "activation_source_label": _get_activation_source_label(getattr(order, "activation_source", None)),
        "activation_error": order.activation_error,
        "activation_error_at": _to_utc_iso(getattr(order, "activation_error_at", None)),
        "created_at": _to_utc_iso(order.created_at),
        "updated_at": _to_utc_iso(order.updated_at),
        "parsed_custom_id": parsed_custom_id,
        "custom_id_parse_error": custom_id_parse_error,
        "raw_payload": _parse_json_text(order.raw_payload),
        "raw_payload_text": order.raw_payload,
    }


def _build_remote_afdian_order_payload(order: dict[str, Any]) -> dict[str, Any]:
    custom_id = afdian_service.get_custom_id(order) or None
    parsed_custom_id = None
    custom_id_parse_error = None
    if custom_id:
        try:
            parsed_custom_id = afdian_service.parse_custom_id(custom_id)
        except ValueError as exc:
            custom_id_parse_error = str(exc)

    status_code = afdian_service.get_order_status(order)
    return {
        "order_no": afdian_service.get_order_no(order) or None,
        "out_trade_no": afdian_service.get_out_trade_no(order) or None,
        "custom_id": custom_id,
        "remark": order.get("remark"),
        "status": status_code,
        "status_label": {
            2: "Paid",
            1: "Pending",
            0: "Unknown",
        }.get(status_code, str(status_code) if status_code is not None else None),
        "amount": _format_money(order.get("show_amount") or order.get("total_amount") or order.get("amount")),
        "create_time_local": _format_local_timestamp(order.get("create_time")),
        "parsed_custom_id": parsed_custom_id,
        "custom_id_parse_error": custom_id_parse_error,
        "raw_payload": order,
    }


def list_orders_payload(db: Session) -> dict[str, Any]:
    close_expired_pending_orders(db)
    orders = db.query(PaymentOrder).order_by(desc(PaymentOrder.created_at), desc(PaymentOrder.id)).all()
    parsed_orders = [_build_afdian_order_payload(order) for order in orders]
    return {
        "summary": {
            "total_orders": len(parsed_orders),
            "activated_orders": sum(1 for order in parsed_orders if order["status"] == "activated"),
            "pending_orders": sum(1 for order in parsed_orders if order["status"] == "pending"),
            "closed_orders": sum(1 for order in parsed_orders if order["status"] == "unpaid"),
            "activated_amount_total": round(
                sum(float(order["amount"] or 0) for order in parsed_orders if order["status"] == "activated"),
                2,
            ),
        },
        "items": parsed_orders,
    }


def get_order_detail_payload(db: Session, order_id: int) -> dict[str, Any]:
    order = db.query(PaymentOrder).filter(PaymentOrder.id == order_id).first()
    if order is None:
        raise ValueError("Order not found")
    return _build_afdian_order_payload(order)


def _find_local_order_by_keyword(db: Session, query_value: str) -> Optional[PaymentOrder]:
    lookup_value = (query_value or "").strip()
    if not lookup_value:
        return None

    lookup_digest = payment_lookup_digest(lookup_value)
    if not lookup_digest:
        return None

    order = (
        db.query(PaymentOrder)
        .filter(
            or_(
                PaymentOrder.out_trade_no == lookup_value,
                PaymentOrder.order_no == lookup_value,
                PaymentOrder.custom_id_digest == lookup_digest,
                PaymentOrder.remark_digest == lookup_digest,
            )
        )
        .order_by(desc(PaymentOrder.id))
        .first()
    )
    if order is None:
        return None

    if order.out_trade_no == lookup_value or order.order_no == lookup_value:
        return order
    if normalize_payment_lookup_value(order.custom_id) == lookup_value:
        return order
    if normalize_payment_lookup_value(order.remark) == lookup_value:
        return order
    return None


async def lookup_afdian_order_payload(db: Session, query_value: str) -> dict[str, Any]:
    lookup_value = (query_value or "").strip()
    local_order = _find_local_order_by_keyword(db, lookup_value)

    resolved_out_trade_no = lookup_value
    if local_order and local_order.out_trade_no:
        resolved_out_trade_no = local_order.out_trade_no

    remote_payload = None
    remote_order = None
    remote_error = None
    if resolved_out_trade_no:
        try:
            remote_payload = await afdian_service.query_order(out_trade_no=resolved_out_trade_no)
            remote_orders = afdian_service.extract_orders(remote_payload)
            if remote_orders:
                remote_order = next(
                    (item for item in remote_orders if afdian_service.get_out_trade_no(item) == resolved_out_trade_no),
                    remote_orders[0],
                )
        except Exception as exc:  # pylint: disable=broad-except
            remote_error = str(exc)

    return {
        "query_value": lookup_value,
        "resolved_out_trade_no": resolved_out_trade_no or None,
        "local_found": local_order is not None,
        "remote_found": remote_order is not None,
        "remote_error": remote_error,
        "local_order": _build_afdian_order_payload(local_order) if local_order else None,
        "remote_order": _build_remote_afdian_order_payload(remote_order) if remote_order else None,
        "remote_payload": remote_payload,
    }


def resolve_activate_lookup(db: Session, query_value: str) -> tuple[Optional[str], Optional[str]]:
    lookup_value = (query_value or "").strip()
    local_order = _find_local_order_by_keyword(db, lookup_value)
    if local_order is None:
        return lookup_value or None, lookup_value or None

    local_out_trade_no = (local_order.out_trade_no or "").strip() or None
    local_custom_id = (local_order.custom_id or "").strip() or None
    local_remark = (local_order.remark or "").strip() or None

    if lookup_value == local_out_trade_no:
        return local_out_trade_no, local_custom_id or local_remark or lookup_value

    if lookup_value == local_custom_id or lookup_value == local_remark:
        return local_out_trade_no, lookup_value

    return local_out_trade_no, local_custom_id or local_remark or lookup_value


def build_dashboard_summary(db: Session) -> dict[str, Any]:
    user_payload = list_users_payload(db)
    order_payload = list_orders_payload(db)
    latest_users = user_payload["items"][:5]
    latest_orders = order_payload["items"][:8]
    return {
        "users": user_payload["summary"],
        "orders": order_payload["summary"],
        "latest_users": latest_users,
        "latest_orders": latest_orders,
    }


def parse_afdian_token_payload(token: str) -> dict[str, Any]:
    token = (token or "").strip()
    if not token:
        raise ValueError("token is required")
    return _build_afdian_token_result(token)


def _get_local_hour_expr(db: Session):
    if db.bind.dialect.name == "sqlite":
        return func.strftime("%Y-%m-%d %H:00", func.datetime(RequestLog.timestamp, f"+{LOCAL_TIME_OFFSET_HOURS} hours"))
    return func.date_format(
        func.date_add(RequestLog.timestamp, text(f"INTERVAL {LOCAL_TIME_OFFSET_HOURS} HOUR")),
        "%Y-%m-%d %H:00",
    )


def _get_local_date_expr(db: Session):
    if db.bind.dialect.name == "sqlite":
        return func.date(func.datetime(RequestLog.timestamp, f"+{LOCAL_TIME_OFFSET_HOURS} hours"))
    return func.date(func.date_add(RequestLog.timestamp, text(f"INTERVAL {LOCAL_TIME_OFFSET_HOURS} HOUR")))


def _get_recent_hours(db: Session, hour_expr) -> list[str]:
    latest_timestamp = db.query(func.max(RequestLog.timestamp)).scalar()
    if not latest_timestamp:
        return []

    recent_start = latest_timestamp - timedelta(days=14)
    rows = (
        db.query(hour_expr.label("hour"))
        .filter(RequestLog.timestamp >= recent_start)
        .group_by(hour_expr)
        .order_by(desc(hour_expr))
        .limit(MAX_HOUR_OPTIONS)
        .all()
    )
    return [row[0] for row in rows if row[0]]


def _normalize_hour(selected_hour: Optional[str], hours: list[str]) -> Optional[str]:
    if selected_hour and selected_hour in hours:
        return selected_hour

    if selected_hour:
        try:
            datetime.strptime(selected_hour, "%Y-%m-%d %H:00")
            return selected_hour
        except ValueError:
            logger.warning("[STATS] Invalid hour format: %s", selected_hour)

    return hours[0] if hours else None


def _get_utc_hour_range(selected_hour: str):
    local_start = datetime.strptime(selected_hour, "%Y-%m-%d %H:00")
    utc_start = local_start - timedelta(hours=LOCAL_TIME_OFFSET_HOURS)
    return utc_start, utc_start + timedelta(hours=1)


def build_stats_payload(db: Session, selected_hour_param: Optional[str]) -> dict[str, Any]:
    hour_expr = _get_local_hour_expr(db)
    date_expr = _get_local_date_expr(db)
    hours = _get_recent_hours(db, hour_expr)
    selected_hour = _normalize_hour(selected_hour_param, hours)

    if selected_hour and selected_hour not in hours:
        hours = [selected_hour, *hours[: MAX_HOUR_OPTIONS - 1]]

    total_success = 0
    total_fail = 0
    total_rate_limited = 0
    total_resource_cost = 0
    total_count = 0
    hourly_stats = []
    duration_stats = []
    top_endpoints = []

    if selected_hour:
        hour_start_utc, hour_end_utc = _get_utc_hour_range(selected_hour)
        base_filters = [
            RequestLog.timestamp >= hour_start_utc,
            RequestLog.timestamp < hour_end_utc,
            RequestLog.endpoint.notin_(EXCLUDED_ENDPOINTS),
        ]

        summary_row = (
            db.query(
                func.sum(case((RequestLog.status == "success", 1), else_=0)).label("total_success"),
                func.sum(case((RequestLog.status == "failed", 1), else_=0)).label("total_fail"),
                func.sum(case((RequestLog.status == "rate_limited", 1), else_=0)).label("total_rate_limited"),
                func.sum(RequestLog.resource_cost).label("total_resource_cost"),
                func.count().label("total_count"),
            )
            .filter(*base_filters)
            .first()
        )
        if summary_row:
            total_success = summary_row.total_success or 0
            total_fail = summary_row.total_fail or 0
            total_rate_limited = summary_row.total_rate_limited or 0
            total_resource_cost = summary_row.total_resource_cost or 0
            total_count = summary_row.total_count or 0

        total_count_col = func.count().label("total_count")
        hourly_stats = (
            db.query(
                RequestLog.username,
                RequestLog.ip,
                RequestLog.endpoint,
                func.sum(case((RequestLog.status == "success", 1), else_=0)).label("success_count"),
                func.sum(case((RequestLog.status == "failed", 1), else_=0)).label("fail_count"),
                func.sum(case((RequestLog.status == "rate_limited", 1), else_=0)).label("rate_limited_count"),
                func.sum(RequestLog.resource_cost).label("resource_cost"),
                total_count_col,
            )
            .filter(*base_filters)
            .group_by(RequestLog.username, RequestLog.ip, RequestLog.endpoint)
            .order_by(desc(total_count_col))
            .limit(MAX_DETAIL_ROWS)
            .all()
        )

        endpoint_total_col = func.count().label("endpoint_total")
        top_endpoints = (
            db.query(
                RequestLog.endpoint,
                func.sum(case((RequestLog.status == "success", 1), else_=0)).label("success_count"),
                func.sum(case((RequestLog.status == "failed", 1), else_=0)).label("fail_count"),
                func.sum(case((RequestLog.status == "rate_limited", 1), else_=0)).label("rate_limited_count"),
                func.sum(RequestLog.resource_cost).label("resource_cost"),
                endpoint_total_col,
            )
            .filter(*base_filters)
            .group_by(RequestLog.endpoint)
            .order_by(desc(endpoint_total_col))
            .limit(8)
            .all()
        )

        duration_range = case(
            (RequestLog.duration < 0.5, "0-0.5s"),
            (RequestLog.duration < 1, "0.5-1s"),
            (RequestLog.duration < 2, "1-2s"),
            (RequestLog.duration < 3, "2-3s"),
            (RequestLog.duration < 4, "3-4s"),
            (RequestLog.duration < 6, "4-6s"),
            (RequestLog.duration < 10, "5-10s"),
            (RequestLog.duration < 20, "10-20s"),
            (RequestLog.duration < 30, "20-30s"),
            (RequestLog.duration < 50, "30-50s"),
            else_="50+s",
        ).label("duration_range")
        bucket_order = case(
            (RequestLog.duration < 0.5, 1),
            (RequestLog.duration < 1, 2),
            (RequestLog.duration < 2, 3),
            (RequestLog.duration < 3, 4),
            (RequestLog.duration < 4, 5),
            (RequestLog.duration < 6, 6),
            (RequestLog.duration < 10, 7),
            (RequestLog.duration < 20, 8),
            (RequestLog.duration < 30, 9),
            (RequestLog.duration < 50, 10),
            else_=11,
        ).label("bucket_order")

        duration_stats = (
            db.query(duration_range, func.count().label("count"), bucket_order)
            .filter(*base_filters)
            .group_by(duration_range, bucket_order)
            .order_by(bucket_order)
            .all()
        )

    if selected_hour:
        local_end_day = datetime.strptime(selected_hour.split()[0], "%Y-%m-%d").date()
    else:
        local_end_day = (datetime.utcnow() + timedelta(hours=LOCAL_TIME_OFFSET_HOURS)).date()
    local_start_day = local_end_day - timedelta(days=6)

    local_start_dt = datetime.combine(local_start_day, time.min)
    local_end_exclusive = datetime.combine(local_end_day + timedelta(days=1), time.min)
    utc_start_dt = local_start_dt - timedelta(hours=LOCAL_TIME_OFFSET_HOURS)
    utc_end_dt = local_end_exclusive - timedelta(hours=LOCAL_TIME_OFFSET_HOURS)

    daily_rows = (
        db.query(
            date_expr.label("day"),
            func.sum(case((RequestLog.status == "success", 1), else_=0)).label("daily_success"),
            func.sum(case((RequestLog.status == "failed", 1), else_=0)).label("daily_fail"),
            func.sum(case((RequestLog.status == "rate_limited", 1), else_=0)).label("daily_rate_limited"),
            func.sum(RequestLog.resource_cost).label("daily_resource_cost"),
            func.count().label("daily_total"),
        )
        .filter(RequestLog.timestamp >= utc_start_dt, RequestLog.timestamp < utc_end_dt)
        .group_by(date_expr)
        .order_by(desc(date_expr))
        .all()
    )

    daily_map: dict[str, dict[str, Any]] = {}
    for row in daily_rows:
        day_key = row.day.strftime("%Y-%m-%d") if hasattr(row.day, "strftime") else str(row.day)
        daily_map[day_key] = {
            "day": day_key,
            "daily_success": row.daily_success or 0,
            "daily_fail": row.daily_fail or 0,
            "daily_rate_limited": row.daily_rate_limited or 0,
            "daily_resource_cost": row.daily_resource_cost or 0,
            "daily_total": row.daily_total or 0,
        }

    daily_stats = []
    for day_offset in range(7):
        day_text = (local_start_day + timedelta(days=day_offset)).strftime("%Y-%m-%d")
        daily_stats.append(
            daily_map.get(
                day_text,
                {
                    "day": day_text,
                    "daily_success": 0,
                    "daily_fail": 0,
                    "daily_rate_limited": 0,
                    "daily_resource_cost": 0,
                    "daily_total": 0,
                },
            )
        )

    return {
        "selected_hour": selected_hour,
        "hours": hours,
        "summary": {
            "total_success": total_success,
            "total_fail": total_fail,
            "total_rate_limited": total_rate_limited,
            "total_resource_cost": total_resource_cost,
            "total_count": total_count,
            "success_rate": round((total_success / total_count) * 100, 2) if total_count else 0,
            "fail_rate": round((total_fail / total_count) * 100, 2) if total_count else 0,
            "rate_limited_rate": round((total_rate_limited / total_count) * 100, 2) if total_count else 0,
            "resource_per_request": round((total_resource_cost / total_count), 2) if total_count else 0,
        },
        "date_filter_info": {
            "start_date": local_start_day.strftime("%Y-%m-%d"),
            "end_date": local_end_day.strftime("%Y-%m-%d"),
        },
        "daily_stats": daily_stats,
        "duration_stats": [{"duration_range": row.duration_range, "count": row.count} for row in duration_stats],
        "top_endpoints": [
            {
                "endpoint": row.endpoint,
                "success_count": row.success_count or 0,
                "fail_count": row.fail_count or 0,
                "rate_limited_count": row.rate_limited_count or 0,
                "resource_cost": row.resource_cost or 0,
                "total_count": row.endpoint_total or 0,
            }
            for row in top_endpoints
        ],
        "hourly_stats": [
            {
                "username": row.username or "anonymous",
                "ip": row.ip,
                "endpoint": row.endpoint,
                "success_count": row.success_count or 0,
                "fail_count": row.fail_count or 0,
                "rate_limited_count": row.rate_limited_count or 0,
                "resource_cost": row.resource_cost or 0,
                "total_count": row.total_count or 0,
            }
            for row in hourly_stats
        ],
        "meta": {
            "generated_at": datetime.utcnow().strftime("%Y-%m-%d %H:%M:%S UTC"),
            "max_detail_rows": MAX_DETAIL_ROWS,
        },
    }
