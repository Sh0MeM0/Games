import hashlib
import secrets
from datetime import timedelta
from typing import Annotated

import jwt
from argon2 import PasswordHasher
from argon2.exceptions import InvalidHashError, VerificationError
from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from sqlalchemy import select, text
from sqlalchemy.orm import Session

from .config import settings
from .db import get_db
from .models import Audit, AuthSession, LoginBucket, RefreshToken, User, new_id, utcnow
from .schemas import LoginIn, RefreshIn

router = APIRouter(prefix="/api/auth", tags=["Авторизация"])
hasher = PasswordHasher()
dummy_hash = hasher.hash(secrets.token_urlsafe(32))
bearer = HTTPBearer(auto_error=False)
DB = Annotated[Session, Depends(get_db)]


def hash_password(password: str) -> str:
    return hasher.hash(password)


def verify_password(password: str, password_hash: str) -> bool:
    try:
        return hasher.verify(password_hash, password)
    except (VerificationError, InvalidHashError):
        return False


def decode_token(token: str, kind: str) -> dict:
    try:
        data = jwt.decode(
            token,
            settings.jwt_secret,
            algorithms=["HS256"],
            audience="archive",
            issuer="archive-server",
            options={"require": ["exp", "iat", "sub", "sid", "jti", "typ"]},
        )
        if data["typ"] != kind:
            raise ValueError()
        return data
    except (jwt.PyJWTError, ValueError, KeyError):
        raise HTTPException(401, "Сеанс истёк. Войдите снова.") from None


def tokens(db: Session, session: AuthSession) -> dict:
    now = utcnow()
    common = {
        "sub": session.user_id,
        "sid": session.id,
        "iat": now,
        "iss": "archive-server",
        "aud": "archive",
    }
    access = jwt.encode(
        {
            **common,
            "typ": "access",
            "jti": new_id(),
            "exp": min(now + timedelta(minutes=settings.access_minutes), session.expires_at),
        },
        settings.jwt_secret,
        algorithm="HS256",
    )
    refresh = jwt.encode(
        {**common, "typ": "refresh", "jti": secrets.token_urlsafe(32), "exp": session.expires_at},
        settings.jwt_secret,
        algorithm="HS256",
    )
    db.add(RefreshToken(digest=hashlib.sha256(refresh.encode()).hexdigest(), session_id=session.id))
    return {"access_token": access, "refresh_token": refresh, "token_type": "bearer"}


def current_user(
    request: Request, db: DB, credentials: Annotated[HTTPAuthorizationCredentials | None, Depends(bearer)]
) -> User:
    if not credentials:
        raise HTTPException(401, "Войдите в систему.")
    data = decode_token(credentials.credentials, "access")
    session = db.get(AuthSession, data["sid"])
    user = db.get(User, data["sub"])
    if (
        not session
        or session.user_id != data["sub"]
        or session.revoked
        or session.expires_at <= utcnow()
        or not user
        or not user.active
    ):
        raise HTTPException(401, "Сеанс завершён или пользователь заблокирован.")
    request.state.session_id = session.id
    return user


CurrentUser = Annotated[User, Depends(current_user)]


def admin_user(user: CurrentUser) -> User:
    if user.role != "admin":
        raise HTTPException(403, "Доступ разрешён только администратору.")
    return user


Admin = Annotated[User, Depends(admin_user)]


def user_view(user: User) -> dict:
    return {
        "id": user.id,
        "username": user.username,
        "full_name": user.full_name,
        "role": user.role,
        "active": user.active,
    }


@router.post("/login")
def login(body: LoginIn, request: Request, db: DB):
    username = body.username.strip().lower()
    ip = request.client.host if request.client else "unknown"
    # Database buckets work across restarts/workers. Username and IP limits are independent.
    keys = sorted(hashlib.sha256(s.encode()).hexdigest() for s in ["user:" + username, "ip:" + ip])
    for key in keys:
        db.execute(text("SELECT pg_advisory_xact_lock(:key)"), {"key": int(key[:15], 16)})
    buckets = []
    for key in keys:
        bucket = db.get(LoginBucket, key)
        if not bucket:
            bucket = LoginBucket(key=key, started_at=utcnow(), count=0)
            db.add(bucket)
        if bucket.started_at + timedelta(seconds=settings.login_window_seconds) <= utcnow():
            bucket.started_at, bucket.count = utcnow(), 0
        if bucket.count >= settings.login_max_attempts:
            db.commit()
            raise HTTPException(
                429,
                "Слишком много попыток входа. Попробуйте позже.",
                headers={"Retry-After": str(settings.login_window_seconds)},
            )
        buckets.append(bucket)
    user = db.scalar(select(User).where(User.username == username).with_for_update())
    valid = verify_password(body.password, user.password_hash if user else dummy_hash)
    if not valid or not user or not user.active:
        for bucket in buckets:
            bucket.count += 1
        db.add(Audit(action="login_failed", detail=f"Логин: {username[:64]}; IP: {ip[:64]}"))
        db.commit()
        raise HTTPException(401, "Неверный логин или пароль.")
    # Keep IP failures until window expiry; success must not bypass the IP budget.
    for bucket in buckets:
        if bucket.key == hashlib.sha256(("user:" + username).encode()).hexdigest():
            bucket.count = 0
    if hasher.check_needs_rehash(user.password_hash):
        user.password_hash = hash_password(body.password)
    session = AuthSession(user_id=user.id, expires_at=utcnow() + timedelta(days=settings.refresh_days))
    db.add(session)
    db.flush()
    result = tokens(db, session)
    db.add(Audit(user_id=user.id, action="login"))
    db.commit()
    return {**result, "user": user_view(user)}


@router.post("/refresh")
def refresh(body: RefreshIn, db: DB):
    data = decode_token(body.refresh_token, "refresh")
    session = db.scalar(select(AuthSession).where(AuthSession.id == data["sid"]).with_for_update())
    user = db.get(User, data["sub"])
    if (
        not session
        or session.revoked
        or session.user_id != data["sub"]
        or session.expires_at <= utcnow()
        or not user
        or not user.active
    ):
        raise HTTPException(401, "Сеанс завершён. Войдите снова.")
    old = db.get(RefreshToken, hashlib.sha256(body.refresh_token.encode()).hexdigest())
    if not old or old.session_id != session.id or old.used:
        session.revoked = True
        db.add(Audit(user_id=user.id, action="refresh_reuse", detail="Сеанс отозван"))
        db.commit()
        raise HTTPException(401, "Повторное использование refresh-токена. Войдите снова.")
    old.used = True
    result = tokens(db, session)
    db.commit()
    return result


@router.get("/me")
def me(user: CurrentUser):
    return user_view(user)


@router.post("/logout")
def logout(request: Request, user: CurrentUser, db: DB):
    session = db.scalar(
        select(AuthSession).where(AuthSession.id == request.state.session_id).with_for_update()
    )
    session.revoked = True
    db.add(Audit(user_id=user.id, action="logout"))
    db.commit()
    return {"ok": True}
