import secrets
from datetime import timedelta
from typing import Annotated
from uuid import UUID

from fastapi import APIRouter, Form, HTTPException, Query, Request, UploadFile
from fastapi.responses import FileResponse
from sqlalchemy import func, select
from sqlalchemy.exc import IntegrityError

from .auth import DB, Admin, CurrentUser
from .config import settings
from .db import metadata_guard
from .models import Audit, Node, User, Version, utcnow
from .schemas import FolderIn, LockIn, NameIn, RollbackIn, validate_name
from .storage import blob_path, store_upload, suffix

router = APIRouter(prefix="/api", tags=["Файлы"])


def get_node(db, node_id, *, deleted=False) -> Node:
    node = db.get(Node, str(node_id))
    if not node or node.deleted != deleted:
        raise HTTPException(404, "Файл или папка не найдены.")
    return node


def require_parent(db, parent_id):
    if parent_id:
        try:
            parent_id = str(UUID(parent_id))
        except ValueError:
            raise HTTPException(422, "Некорректная папка.") from None
        if get_node(db, parent_id).kind != "folder":
            raise HTTPException(422, "Родитель должен быть папкой.")
    return parent_id


def available_name(db, parent_id, name, exclude=None):
    existing = db.scalar(
        select(Node).where(
            Node.parent_id == parent_id,
            Node.deleted.is_(False),
            func.lower(Node.name) == name.lower(),
            Node.id != (exclude or ""),
        )
    )
    if existing:
        raise HTTPException(409, "В этой папке уже есть файл или папка с таким именем.")


def active_lock(node: Node) -> bool:
    return bool(node.lock_token and node.lock_until and node.lock_until > utcnow())


def clear_lock(node: Node):
    node.lock_user_id = node.lock_session_id = node.lock_token = node.lock_until = None


def require_lock(node: Node, user: User, request: Request, token: str):
    if (
        not active_lock(node)
        or node.lock_user_id != user.id
        or node.lock_session_id != request.state.session_id
        or not secrets.compare_digest(node.lock_token, token)
    ):
        raise HTTPException(
            409, "Блокировка утрачена. Локальная копия сохранена; откройте актуальную версию заново."
        )


def node_view(db, node: Node) -> dict:
    owner = db.get(User, node.lock_user_id) if active_lock(node) else None
    version = db.scalar(
        select(Version).where(Version.node_id == node.id, Version.number == node.current_version)
    )
    return {
        "id": node.id,
        "parent_id": node.parent_id,
        "name": node.name,
        "kind": node.kind,
        "deleted": node.deleted,
        "version": node.current_version,
        "size": version.size if version else 0,
        "updated_at": node.updated_at,
        "lock": {"user_id": owner.id, "full_name": owner.full_name, "expires_at": node.lock_until}
        if owner
        else None,
    }


def append_version(db, node, user, blob, digest, size, note="Сохранение"):
    node.current_version += 1
    node.updated_at = utcnow()
    db.add(
        Version(
            node_id=node.id,
            number=node.current_version,
            blob=blob,
            sha256=digest,
            size=size,
            author_id=user.id,
            note=note,
        )
    )
    db.add(
        Audit(
            user_id=user.id,
            action="upload" if node.current_version == 1 else "edit",
            node_id=node.id,
            detail=f"{node.name}; версия {node.current_version}; {note}",
        )
    )


@router.get("/nodes")
def list_nodes(
    user: CurrentUser,
    db: DB,
    parent_id: UUID | None = None,
    search: Annotated[str | None, Query(max_length=180)] = None,
):
    query = select(Node).where(Node.deleted.is_(False))
    if search:
        query = query.where(Node.name.ilike("%" + search + "%"))
    else:
        require_parent(db, str(parent_id) if parent_id else None)
        query = query.where(Node.parent_id == (str(parent_id) if parent_id else None))
    nodes = db.scalars(query.order_by(Node.kind.desc(), func.lower(Node.name)).limit(501)).all()
    return {"items": [node_view(db, n) for n in nodes[:500]], "truncated": len(nodes) > 500}


@router.get("/nodes/{node_id}")
def details(node_id: UUID, user: CurrentUser, db: DB):
    node = get_node(db, node_id)
    ancestors = []
    parent_id = node.parent_id
    while parent_id:
        parent = get_node(db, parent_id)
        ancestors.insert(0, {"id": parent.id, "name": parent.name})
        parent_id = parent.parent_id
    return {**node_view(db, node), "ancestors": ancestors}


@router.post("/folders", status_code=201)
def create_folder(body: FolderIn, user: CurrentUser, db: DB):
    metadata_guard(db)
    parent_id = require_parent(db, body.parent_id)
    available_name(db, parent_id, body.name)
    node = Node(parent_id=parent_id, name=body.name, kind="folder")
    db.add(node)
    db.flush()
    db.add(Audit(user_id=user.id, action="create_folder", node_id=node.id, detail=node.name))
    db.commit()
    return node_view(db, node)


@router.post("/files", status_code=201)
async def upload_file(
    file: UploadFile, user: CurrentUser, db: DB, parent_id: Annotated[str | None, Form()] = None
):
    try:
        name = validate_name(file.filename or "")
    except ValueError as exc:
        raise HTTPException(422, str(exc)) from None
    blob, digest, size = await store_upload(file, name)
    try:
        metadata_guard(db)
        parent_id = require_parent(db, parent_id)
        available_name(db, parent_id, name)
        node = Node(parent_id=parent_id, name=name, kind="file")
        db.add(node)
        db.flush()
        append_version(db, node, user, blob, digest, size, "Загрузка")
        db.commit()
    except Exception:
        db.rollback()
        blob_path(blob).unlink(missing_ok=True)
        raise
    return node_view(db, node)


@router.get("/files/{node_id}/download")
def download(node_id: UUID, user: CurrentUser, db: DB, version: int | None = Query(default=None, ge=1)):
    node = get_node(db, node_id)
    row = db.scalar(
        select(Version).where(Version.node_id == node.id, Version.number == (version or node.current_version))
    )
    if not row:
        raise HTTPException(404, "Версия не найдена.")
    path = blob_path(row.blob)
    if not path.is_file():
        raise HTTPException(503, "Файл отсутствует в хранилище. Обратитесь к администратору.")
    db.add(
        Audit(user_id=user.id, action="download", node_id=node.id, detail=f"{node.name}; версия {row.number}")
    )
    db.commit()
    return FileResponse(
        path,
        media_type="application/octet-stream",
        filename=node.name,
        headers={
            "X-File-Version": str(row.number),
            "X-File-SHA256": row.sha256,
            "Cache-Control": "no-store",
            "X-Content-Type-Options": "nosniff",
        },
    )


@router.patch("/nodes/{node_id}")
def rename(node_id: UUID, body: NameIn, user: CurrentUser, db: DB):
    metadata_guard(db)
    node = get_node(db, node_id)
    if active_lock(node):
        raise HTTPException(409, "Сначала завершите редактирование файла.")
    if node.kind == "file" and suffix(body.name) != suffix(node.name):
        raise HTTPException(422, "Переименование не должно изменять формат документа.")
    available_name(db, node.parent_id, body.name, node.id)
    old = node.name
    node.name, node.updated_at = body.name, utcnow()
    db.add(Audit(user_id=user.id, action="rename", node_id=node.id, detail=f"{old} → {node.name}"))
    db.commit()
    return node_view(db, node)


def subtree(db, node):
    result, pending = [node], [node.id]
    while pending:
        children = db.scalars(select(Node).where(Node.parent_id.in_(pending), Node.deleted.is_(False))).all()
        result.extend(children)
        pending = [c.id for c in children]
    return result


@router.delete("/nodes/{node_id}")
def delete(node_id: UUID, user: CurrentUser, db: DB):
    metadata_guard(db)
    node = get_node(db, node_id)
    nodes = subtree(db, node)
    if any(active_lock(n) for n in nodes):
        raise HTTPException(409, "Файл или содержимое папки сейчас редактируется.")
    for item in nodes:
        item.deleted, item.deletion_group = True, node.id
        item.updated_at = utcnow()
        clear_lock(item)
    db.add(Audit(user_id=user.id, action="delete", node_id=node.id, detail=node.name))
    db.commit()
    return {"ok": True}


@router.get("/trash")
def trash(user: CurrentUser, db: DB):
    rows = db.scalars(
        select(Node)
        .where(Node.deleted.is_(True), Node.deletion_group == Node.id)
        .order_by(Node.updated_at.desc())
    ).all()
    return [node_view(db, node) for node in rows]


@router.post("/nodes/{node_id}/restore")
def restore(node_id: UUID, user: CurrentUser, db: DB):
    metadata_guard(db)
    node = get_node(db, node_id, deleted=True)
    if node.deletion_group != node.id:
        raise HTTPException(409, "Восстановите сначала родительскую папку из корзины.")
    require_parent(db, node.parent_id)
    available_name(db, node.parent_id, node.name)
    for item in db.scalars(select(Node).where(Node.deletion_group == node.id)).all():
        item.deleted, item.deletion_group = False, None
        item.updated_at = utcnow()
    db.add(Audit(user_id=user.id, action="restore", node_id=node.id, detail=node.name))
    try:
        db.commit()
    except IntegrityError:
        db.rollback()
        raise HTTPException(409, "Имя уже занято. Переименуйте новый файл перед восстановлением.") from None
    return node_view(db, node)


@router.post("/files/{node_id}/checkout")
def checkout(node_id: UUID, request: Request, user: CurrentUser, db: DB):
    metadata_guard(db)
    node = get_node(db, node_id)
    if node.kind != "file":
        raise HTTPException(422, "Можно редактировать только файл.")
    if active_lock(node):
        owner = db.get(User, node.lock_user_id)
        raise HTTPException(423, f"Редактирует: {owner.full_name}. Доступно открытие для чтения.")
    node.lock_user_id, node.lock_session_id = user.id, request.state.session_id
    node.lock_token = secrets.token_urlsafe(32)
    node.lock_until = utcnow() + timedelta(seconds=settings.lock_timeout_seconds)
    db.add(Audit(user_id=user.id, action="checkout", node_id=node.id, detail=node.name))
    db.commit()
    return {
        "lock_token": node.lock_token,
        "version": node.current_version,
        "expires_at": node.lock_until,
        "heartbeat_seconds": min(20, settings.lock_timeout_seconds // 3),
        "node": node_view(db, node),
    }


@router.post("/files/{node_id}/heartbeat")
def heartbeat(node_id: UUID, body: LockIn, request: Request, user: CurrentUser, db: DB):
    metadata_guard(db)
    node = get_node(db, node_id)
    require_lock(node, user, request, body.lock_token)
    node.lock_until = utcnow() + timedelta(seconds=settings.lock_timeout_seconds)
    db.commit()
    return {"expires_at": node.lock_until}


@router.post("/files/{node_id}/release")
def release(node_id: UUID, body: LockIn, request: Request, user: CurrentUser, db: DB):
    metadata_guard(db)
    node = get_node(db, node_id)
    if node.lock_token:
        if (
            node.lock_token != body.lock_token
            or node.lock_user_id != user.id
            or node.lock_session_id != request.state.session_id
        ):
            raise HTTPException(409, "Блокировка уже принадлежит другому сеансу.")
        clear_lock(node)
        db.add(Audit(user_id=user.id, action="release", node_id=node.id, detail=node.name))
    db.commit()
    return {"ok": True}


@router.post("/files/{node_id}/force-unlock")
def force_unlock(node_id: UUID, user: Admin, db: DB):
    metadata_guard(db)
    node = get_node(db, node_id)
    clear_lock(node)
    db.add(Audit(user_id=user.id, action="force_unlock", node_id=node.id, detail=node.name))
    db.commit()
    return {"ok": True}


@router.post("/files/{node_id}/versions", status_code=201)
async def save(
    node_id: UUID,
    file: UploadFile,
    request: Request,
    user: CurrentUser,
    db: DB,
    lock_token: Annotated[str, Form(min_length=16, max_length=64)],
    base_version: Annotated[int, Form(ge=1)],
):
    node = get_node(db, node_id)
    require_lock(node, user, request, lock_token)
    blob, digest, size = await store_upload(file, node.name)
    try:
        metadata_guard(db)
        db.refresh(node)  # Download/validation happened before the critical section.
        if node.deleted:
            raise HTTPException(404, "Файл удалён.")
        require_lock(node, user, request, lock_token)
        if base_version != node.current_version:
            raise HTTPException(
                409, "Конфликт версий. Сохраните локальную копию и откройте актуальную версию."
            )
        append_version(db, node, user, blob, digest, size)
        node.lock_until = utcnow() + timedelta(seconds=settings.lock_timeout_seconds)
        db.commit()
    except Exception:
        db.rollback()
        blob_path(blob).unlink(missing_ok=True)
        raise
    return node_view(db, node)


@router.get("/files/{node_id}/versions")
def history(node_id: UUID, user: CurrentUser, db: DB):
    get_node(db, node_id)
    rows = db.execute(
        select(Version, User.full_name)
        .join(User, User.id == Version.author_id)
        .where(Version.node_id == str(node_id))
        .order_by(Version.number.desc())
    ).all()
    return [
        {
            "number": v.number,
            "author": name,
            "created_at": v.created_at,
            "size": v.size,
            "sha256": v.sha256,
            "note": v.note,
        }
        for v, name in rows
    ]


@router.post("/files/{node_id}/rollback", status_code=201)
def rollback(node_id: UUID, body: RollbackIn, request: Request, user: CurrentUser, db: DB):
    metadata_guard(db)
    node = get_node(db, node_id)
    require_lock(node, user, request, body.lock_token)
    if node.current_version != body.base_version:
        raise HTTPException(409, "Конфликт версий. Обновите историю.")
    original = db.scalar(select(Version).where(Version.node_id == node.id, Version.number == body.version))
    if not original or not blob_path(original.blob).is_file():
        raise HTTPException(404, "Версия не найдена в хранилище.")
    append_version(
        db, node, user, original.blob, original.sha256, original.size, f"Откат к версии {body.version}"
    )
    db.add(Audit(user_id=user.id, action="rollback", node_id=node.id, detail=f"Версия {body.version}"))
    db.commit()
    return node_view(db, node)
