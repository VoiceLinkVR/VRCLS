import re

from fastapi import APIRouter, Depends, HTTPException, Request, status
from sqlalchemy import or_
from sqlalchemy.orm import Session

from core.dependencies import (
    authenticate_user,
    get_current_user,
    get_db,
    hash_password,
    is_user_expired,
    issue_login_artifacts,
    normalize_email,
    revoke_refresh_token,
    revoke_user_session,
    rotate_refresh_token,
    utcnow_naive,
)
from db.models import User
from schemas.auth import (
    AuthLoginRequest,
    AuthLogoutRequest,
    AuthRefreshRequest,
    AuthRegisterRequest,
    AuthTokenResponse,
    AuthUserResponse,
)

router = APIRouter()


def _build_username(db: Session, email: str) -> str:
    local_part = email.split("@", 1)[0]
    slug = re.sub(r"[^a-zA-Z0-9_.-]+", "_", local_part).strip("._-") or "user"
    slug = slug[:32]
    candidate = slug
    counter = 1

    while db.query(User).filter(User.username == candidate).first() is not None:
        suffix = f"_{counter}"
        candidate = f"{slug[: max(1, 50 - len(suffix))]}{suffix}"
        counter += 1

    return candidate


def _serialize_user(user: User) -> AuthUserResponse:
    display_name = (getattr(user, "display_name", None) or user.username or "").strip()
    return AuthUserResponse(
        id=str(user.id),
        username=user.username,
        email=(user.email or "").strip(),
        display_name=display_name,
        email_verified=bool(getattr(user, "email_verified", False)),
        is_admin=bool(user.is_admin),
    )


@router.post("/auth/register", response_model=AuthTokenResponse)
async def register(
    payload: AuthRegisterRequest,
    request: Request,
    db: Session = Depends(get_db),
):
    email = normalize_email(payload.email)
    display_name = payload.display_name.strip()

    existing_user = db.query(User).filter(or_(User.email == email, User.username == email)).first()
    if existing_user is not None:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Email already registered")

    user = User(
        username=_build_username(db, email),
        password=hash_password(payload.password),
        email=email,
        email_verified=False,
        display_name=display_name,
        is_admin=False,
        is_active=True,
        created_at=utcnow_naive(),
        last_login_at=utcnow_naive(),
    )
    db.add(user)
    db.flush()

    access_token, refresh_token, _ = issue_login_artifacts(db, user, request)
    db.commit()
    db.refresh(user)

    return AuthTokenResponse(
        access_token=access_token,
        refresh_token=refresh_token,
        user=_serialize_user(user),
    )


@router.post("/auth/login", response_model=AuthTokenResponse)
async def login(
    payload: AuthLoginRequest,
    request: Request,
    db: Session = Depends(get_db),
):
    user = authenticate_user(db, payload.login.strip(), payload.password)
    if user is None:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid credentials")

    if not user.is_active:
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="User is inactive")

    expired, expires_at_utc = is_user_expired(user)
    if expired:
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail={
                "code": "account_expired",
                "message": "User account expired",
                "expiration_utc": expires_at_utc.isoformat().replace("+00:00", "Z") if expires_at_utc else None,
            },
        )

    user.last_login_at = utcnow_naive()
    access_token, refresh_token, _ = issue_login_artifacts(db, user, request)
    db.commit()
    db.refresh(user)

    return AuthTokenResponse(
        access_token=access_token,
        refresh_token=refresh_token,
        user=_serialize_user(user),
    )


@router.post("/auth/refresh", response_model=AuthTokenResponse)
async def refresh(payload: AuthRefreshRequest, db: Session = Depends(get_db)):
    user, access_token, refresh_token = rotate_refresh_token(db, payload.refresh_token.strip())

    user.last_login_at = utcnow_naive()
    db.commit()
    db.refresh(user)

    return AuthTokenResponse(
        access_token=access_token,
        refresh_token=refresh_token,
        user=_serialize_user(user),
    )


@router.post("/auth/logout")
async def logout(
    request: Request,
    payload: AuthLogoutRequest | None = None,
    db: Session = Depends(get_db),
):
    if payload and payload.refresh_token:
        revoke_refresh_token(db, payload.refresh_token.strip())
    revoke_user_session(db, request)
    db.commit()
    return {"message": "Logged out"}


@router.get("/auth/me", response_model=AuthUserResponse)
async def me(current_user: User = Depends(get_current_user)):
    return _serialize_user(current_user)
