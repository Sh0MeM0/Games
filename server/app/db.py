from collections.abc import Generator

from sqlalchemy import create_engine, text
from sqlalchemy.orm import DeclarativeBase, Session, sessionmaker

from .config import settings


class Base(DeclarativeBase):
    pass


engine = create_engine(settings.database_url, pool_pre_ping=True, pool_size=10, max_overflow=10)
SessionLocal = sessionmaker(engine, expire_on_commit=False)


def get_db() -> Generator[Session, None, None]:
    with SessionLocal() as session:
        yield session


def metadata_guard(db: Session) -> None:
    # A small organisation: serialize tree/lock mutations, including ancestor checks.
    # PostgreSQL releases this transaction-scoped lock on commit or rollback.
    db.execute(text("SELECT pg_advisory_xact_lock(7303021)"))
