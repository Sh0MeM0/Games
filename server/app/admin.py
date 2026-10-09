import csv
import io
from uuid import UUID

from fastapi import APIRouter, HTTPException, Query, UploadFile
from pydantic import ValidationError
from sqlalchemy import select, update

from .auth import DB, Admin, hash_password, user_view
from .db import metadata_guard
from .models import Audit, AuthSession, Node, User
from .schemas import ActiveIn, PasswordIn, UserCreate

router = APIRouter(prefix="/api/admin", tags=["Администрирование"])


def create_user(db, data: UserCreate):
    if db.scalar(select(User).where(User.username == data.username)):
        raise HTTPException(409, f"Логин {data.username} уже занят.")
    user = User(
        username=data.username,
        full_name=data.full_name,
        password_hash=hash_password(data.password),
        role=data.role,
    )
    db.add(user)
    db.flush()
    return user


def revoke_user(db, user_id):
    db.execute(update(AuthSession).where(AuthSession.user_id == user_id).values(revoked=True))
    db.execute(
        update(Node)
        .where(Node.lock_user_id == user_id)
        .values(lock_user_id=None, lock_session_id=None, lock_token=None, lock_until=None)
    )


@router.get("/users")
def users(admin: Admin, db: DB):
    return [user_view(u) for u in db.scalars(select(User).order_by(User.username)).all()]


@router.post("/users", status_code=201)
def add_user(body: UserCreate, admin: Admin, db: DB):
    metadata_guard(db)
    user = create_user(db, body)
    db.add(Audit(user_id=admin.id, action="create_user", detail=body.username))
    db.commit()
    return user_view(user)


@router.patch("/users/{user_id}")
def set_active(user_id: UUID, body: ActiveIn, admin: Admin, db: DB):
    metadata_guard(db)
    user = db.scalar(select(User).where(User.id == str(user_id)).with_for_update())
    if not user:
        raise HTTPException(404, "Пользователь не найден.")
    if user.id == admin.id and not body.active:
        raise HTTPException(409, "Нельзя заблокировать собственную учётную запись.")
    user.active = body.active
    if not body.active:
        revoke_user(db, user.id)
    db.add(
        Audit(user_id=admin.id, action="activate_user" if body.active else "block_user", detail=user.username)
    )
    db.commit()
    return user_view(user)


@router.post("/users/{user_id}/password")
def reset_password(user_id: UUID, body: PasswordIn, admin: Admin, db: DB):
    metadata_guard(db)
    user = db.scalar(select(User).where(User.id == str(user_id)).with_for_update())
    if not user:
        raise HTTPException(404, "Пользователь не найден.")
    user.password_hash = hash_password(body.password)
    revoke_user(db, user.id)
    db.add(Audit(user_id=admin.id, action="reset_password", detail=user.username))
    db.commit()
    return {"ok": True}


def parse_csv(raw: bytes) -> list[UserCreate]:
    try:
        reader = csv.DictReader(io.StringIO(raw.decode("utf-8-sig")))
        if not {"username", "full_name", "password"} <= set(reader.fieldnames or []):
            raise ValueError("Нужны столбцы username,full_name,password; role необязателен.")
        records = []
        for line, record in enumerate(reader, 2):
            if len(records) >= 100:
                raise ValueError("За один импорт разрешено не более 100 пользователей.")
            try:
                records.append(
                    UserCreate(
                        username=record.get("username", ""),
                        full_name=record.get("full_name", ""),
                        password=record.get("password", ""),
                        role=record.get("role") or "user",
                    )
                )
            except ValidationError:
                # Do not return Pydantic errors: they can contain plaintext passwords.
                raise ValueError(
                    f"Строка {line}: проверьте логин, ФИО, роль и пароль (минимум 12 символов)."
                ) from None
        if not records or len({r.username for r in records}) != len(records):
            raise ValueError("CSV пуст или содержит повторяющиеся логины.")
        return records
    except (UnicodeError, ValueError, csv.Error) as exc:
        raise HTTPException(422, str(exc)) from None


@router.post("/users/import", status_code=201)
async def import_users(file: UploadFile, admin: Admin, db: DB):
    try:
        raw = await file.read(1024 * 1024 + 1)
    finally:
        await file.close()
    if len(raw) > 1024 * 1024:
        raise HTTPException(413, "CSV должен быть меньше 1 МБ.")
    records = parse_csv(raw)
    metadata_guard(db)
    created = [create_user(db, data) for data in records]
    db.add(Audit(user_id=admin.id, action="import_users", detail=f"Создано: {len(created)}"))
    db.commit()
    return {"created": len(created)}


@router.get("/audit")
def audit(admin: Admin, db: DB, limit: int = Query(default=100, ge=1, le=500), before: int | None = None):
    query = select(Audit, User.full_name).outerjoin(User, User.id == Audit.user_id)
    if before:
        query = query.where(Audit.id < before)
    rows = db.execute(query.order_by(Audit.id.desc()).limit(limit)).all()
    return [
        {
            "id": a.id,
            "user": name or "Неавторизованный",
            "action": a.action,
            "node_id": a.node_id,
            "detail": a.detail,
            "created_at": a.created_at,
        }
        for a, name in rows
    ]
