import uuid
from datetime import UTC, datetime

from sqlalchemy import Boolean, DateTime, ForeignKey, Index, Integer, String, Text, UniqueConstraint, text
from sqlalchemy.orm import Mapped, mapped_column

from .db import Base


def new_id() -> str:
    return str(uuid.uuid4())


def utcnow() -> datetime:
    return datetime.now(UTC)


class User(Base):
    __tablename__ = "users"
    id: Mapped[str] = mapped_column(String(36), primary_key=True, default=new_id)
    username: Mapped[str] = mapped_column(String(64), unique=True)
    full_name: Mapped[str] = mapped_column(String(160))
    password_hash: Mapped[str] = mapped_column(Text)
    role: Mapped[str] = mapped_column(String(10), default="user")
    active: Mapped[bool] = mapped_column(Boolean, default=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)


class AuthSession(Base):
    __tablename__ = "auth_sessions"
    id: Mapped[str] = mapped_column(String(36), primary_key=True, default=new_id)
    user_id: Mapped[str] = mapped_column(ForeignKey("users.id"), index=True)
    expires_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    revoked: Mapped[bool] = mapped_column(Boolean, default=False)


class RefreshToken(Base):
    __tablename__ = "refresh_tokens"
    digest: Mapped[str] = mapped_column(String(64), primary_key=True)
    session_id: Mapped[str] = mapped_column(ForeignKey("auth_sessions.id"), index=True)
    used: Mapped[bool] = mapped_column(Boolean, default=False)


class LoginBucket(Base):
    __tablename__ = "login_buckets"
    key: Mapped[str] = mapped_column(String(64), primary_key=True)
    started_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    count: Mapped[int] = mapped_column(Integer, default=0)


class Node(Base):
    __tablename__ = "nodes"
    id: Mapped[str] = mapped_column(String(36), primary_key=True, default=new_id)
    parent_id: Mapped[str | None] = mapped_column(ForeignKey("nodes.id"), nullable=True, index=True)
    name: Mapped[str] = mapped_column(String(200))
    kind: Mapped[str] = mapped_column(String(10))
    deleted: Mapped[bool] = mapped_column(Boolean, default=False, index=True)
    deletion_group: Mapped[str | None] = mapped_column(String(36), nullable=True, index=True)
    current_version: Mapped[int] = mapped_column(Integer, default=0)
    lock_user_id: Mapped[str | None] = mapped_column(ForeignKey("users.id"), nullable=True)
    lock_session_id: Mapped[str | None] = mapped_column(ForeignKey("auth_sessions.id"), nullable=True)
    lock_token: Mapped[str | None] = mapped_column(String(64), nullable=True)
    lock_until: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)


Index(
    "active_sibling_name",
    text("coalesce(parent_id, '')"),
    text("lower(name)"),
    unique=True,
    postgresql_where=text("NOT deleted"),
    _table=Node.__table__,
)


class Version(Base):
    __tablename__ = "versions"
    __table_args__ = (UniqueConstraint("node_id", "number"),)
    id: Mapped[str] = mapped_column(String(36), primary_key=True, default=new_id)
    node_id: Mapped[str] = mapped_column(ForeignKey("nodes.id"), index=True)
    number: Mapped[int] = mapped_column(Integer)
    blob: Mapped[str] = mapped_column(String(64))
    sha256: Mapped[str] = mapped_column(String(64))
    size: Mapped[int] = mapped_column(Integer)
    author_id: Mapped[str] = mapped_column(ForeignKey("users.id"))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)
    note: Mapped[str] = mapped_column(String(200), default="Сохранение")


class Audit(Base):
    __tablename__ = "audit"
    id: Mapped[int] = mapped_column(Integer, primary_key=True, autoincrement=True)
    user_id: Mapped[str | None] = mapped_column(ForeignKey("users.id"), nullable=True)
    action: Mapped[str] = mapped_column(String(50))
    node_id: Mapped[str | None] = mapped_column(String(36), nullable=True)
    detail: Mapped[str] = mapped_column(String(500), default="")
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow, index=True)
