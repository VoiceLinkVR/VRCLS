from fastapi import APIRouter, Depends, HTTPException, Request, status
from sqlalchemy.orm import Session

from core import admin_service
from core.dependencies import (
    authenticate_user,
    get_current_admin_user,
    get_db,
    hash_password,
    issue_login_artifacts,
    normalize_email,
    resolve_authenticated_user,
    revoke_user_session,
    utcnow_naive,
)
from db.models import User
from routers.user_center import activate_afdian_order_by_lookup
from schemas.admin import (
    AdminAuthBootstrapRequest,
    AdminAuthLoginRequest,
    AdminAuthStateResponse,
    AdminAuthTokenResponse,
    AdminOrderActivateRequest,
    AdminOrderLookupRequest,
    AdminUserCreateRequest,
    AdminUserResponse,
    AdminUserUpdateRequest,
)

router = APIRouter()


def _serialize_admin_user(user: User) -> AdminUserResponse:
    return AdminUserResponse(**admin_service.serialize_admin_user(user))


def _current_admin_if_any(request: Request, db: Session) -> User | None:
    try:
        session_user = resolve_authenticated_user(request, db)
    except HTTPException:
        return None
    if session_user is None or not session_user.is_admin:
        return None
    return session_user


@router.get("/auth/state", response_model=AdminAuthStateResponse)
async def auth_state(request: Request, db: Session = Depends(get_db)):
    current_admin = _current_admin_if_any(request, db)
    return AdminAuthStateResponse(
        has_admin=db.query(User).filter(User.is_admin == True).count() > 0,  # noqa: E712
        user=_serialize_admin_user(current_admin) if current_admin else None,
    )


@router.post("/auth/bootstrap", response_model=AdminAuthTokenResponse)
async def bootstrap_admin(
    payload: AdminAuthBootstrapRequest,
    request: Request,
    db: Session = Depends(get_db),
):
    if db.query(User).filter(User.is_admin == True).count() > 0:  # noqa: E712
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail="Admin already exists")

    username = payload.username.strip()
    email = normalize_email(payload.email) if payload.email else None
    if db.query(User).filter(User.username == username).first() is not None:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Username already exists")
    if email and db.query(User).filter(User.email == email).first() is not None:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Email already exists")

    user = User(
        username=username,
        password=hash_password(payload.password),
        email=email,
        display_name=(payload.display_name or username).strip(),
        email_verified=False,
        is_admin=True,
        is_active=True,
        created_at=utcnow_naive(),
        last_login_at=utcnow_naive(),
    )
    db.add(user)
    db.flush()

    access_token, refresh_token, _ = issue_login_artifacts(db, user, request)
    db.commit()
    db.refresh(user)

    return AdminAuthTokenResponse(
        access_token=access_token,
        refresh_token=refresh_token,
        user=_serialize_admin_user(user),
    )


@router.post("/auth/login", response_model=AdminAuthTokenResponse)
async def login_admin(
    payload: AdminAuthLoginRequest,
    request: Request,
    db: Session = Depends(get_db),
):
    user = authenticate_user(db, payload.login.strip(), payload.password)
    if user is None or not user.is_admin:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid credentials")
    if not user.is_active:
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="User is inactive")

    user.last_login_at = utcnow_naive()
    access_token, refresh_token, _ = issue_login_artifacts(db, user, request)
    db.commit()
    db.refresh(user)

    return AdminAuthTokenResponse(
        access_token=access_token,
        refresh_token=refresh_token,
        user=_serialize_admin_user(user),
    )


@router.post("/auth/logout")
async def logout_admin(
    request: Request,
    db: Session = Depends(get_db),
    _: User = Depends(get_current_admin_user),
):
    revoke_user_session(db, request)
    db.commit()
    return {"message": "Logged out"}


@router.get("/auth/me", response_model=AdminUserResponse)
async def me_admin(current_user: User = Depends(get_current_admin_user)):
    return _serialize_admin_user(current_user)


@router.get("/dashboard/summary")
async def dashboard_summary(_: User = Depends(get_current_admin_user), db: Session = Depends(get_db)):
    return admin_service.build_dashboard_summary(db)


@router.get("/users")
async def list_users(_: User = Depends(get_current_admin_user), db: Session = Depends(get_db)):
    return admin_service.list_users_payload(db)


@router.get("/users/{user_id}")
async def get_user(user_id: int, _: User = Depends(get_current_admin_user), db: Session = Depends(get_db)):
    try:
        return admin_service.get_user_detail_payload(db, user_id)
    except ValueError as exc:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail=str(exc)) from exc


@router.post("/users")
async def create_user(
    payload: AdminUserCreateRequest,
    _: User = Depends(get_current_admin_user),
    db: Session = Depends(get_db),
):
    try:
        return admin_service.create_user(db, payload)
    except ValueError as exc:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=str(exc)) from exc


@router.patch("/users/{user_id}")
async def update_user(
    user_id: int,
    payload: AdminUserUpdateRequest,
    _: User = Depends(get_current_admin_user),
    db: Session = Depends(get_db),
):
    try:
        return admin_service.update_user(db, user_id, payload)
    except ValueError as exc:
        status_code = status.HTTP_404_NOT_FOUND if str(exc) == "User not found" else status.HTTP_400_BAD_REQUEST
        raise HTTPException(status_code=status_code, detail=str(exc)) from exc


@router.delete("/users/{user_id}")
async def delete_user(
    user_id: int,
    current_user: User = Depends(get_current_admin_user),
    db: Session = Depends(get_db),
):
    target = db.query(User).filter(User.id == user_id).first()
    if target is None:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="User not found")
    if target.id == current_user.id:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Cannot delete current admin session user")
    return admin_service.delete_user(db, user_id)


@router.get("/orders")
async def list_orders(_: User = Depends(get_current_admin_user), db: Session = Depends(get_db)):
    return admin_service.list_orders_payload(db)


@router.post("/orders/lookup")
async def lookup_order(
    payload: AdminOrderLookupRequest,
    _: User = Depends(get_current_admin_user),
    db: Session = Depends(get_db),
):
    return await admin_service.lookup_afdian_order_payload(db, payload.keyword)


@router.post("/orders/activate")
async def activate_order(
    payload: AdminOrderActivateRequest,
    _: User = Depends(get_current_admin_user),
    db: Session = Depends(get_db),
):
    out_trade_no, remark_or_custom_id = admin_service.resolve_activate_lookup(db, payload.keyword)
    result = await activate_afdian_order_by_lookup(
        db=db,
        out_trade_no=out_trade_no,
        remark_or_custom_id=remark_or_custom_id,
        source="admin_manual_activate",
    )
    return {
        "lookup_value": payload.keyword.strip(),
        "resolved_out_trade_no": out_trade_no,
        "resolved_custom_id": remark_or_custom_id,
        **result,
    }


@router.get("/orders/{order_id}")
async def get_order(order_id: int, _: User = Depends(get_current_admin_user), db: Session = Depends(get_db)):
    try:
        return admin_service.get_order_detail_payload(db, order_id)
    except ValueError as exc:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail=str(exc)) from exc


@router.get("/stats")
async def stats(hour: str | None = None, _: User = Depends(get_current_admin_user), db: Session = Depends(get_db)):
    return admin_service.build_stats_payload(db, hour)


@router.post("/afdian/parse-token")
async def parse_afdian_token(
    payload: AdminOrderLookupRequest,
    _: User = Depends(get_current_admin_user),
):
    try:
        return admin_service.parse_afdian_token_payload(payload.keyword)
    except ValueError as exc:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=str(exc)) from exc
