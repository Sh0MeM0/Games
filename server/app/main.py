from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from sqlalchemy import text
from sqlalchemy.exc import IntegrityError, SQLAlchemyError

from . import admin, auth, files
from .config import settings
from .db import Base, SessionLocal, engine


@asynccontextmanager
async def lifespan(app: FastAPI):
    settings.data_dir.mkdir(parents=True, exist_ok=True)
    # Initial schema for v1. Back up first; future schema changes require migrations.
    Base.metadata.create_all(engine)
    yield


app = FastAPI(title="Общий файловый архив", version="1.0.0", lifespan=lifespan, docs_url=None, redoc_url=None)
app.include_router(auth.router)
app.include_router(files.router)
app.include_router(admin.router)


@app.middleware("http")
async def security_headers(request: Request, call_next):
    if settings.require_https and request.url.scheme != "https" and request.url.path != "/health":
        return JSONResponse({"detail": "Разрешено только HTTPS."}, status_code=400)
    # Reject oversized requests before multipart parsing. Caddy also limits request bodies.
    length = request.headers.get("content-length")
    try:
        if length and int(length) > (settings.max_upload_mb + 1) * 1024 * 1024:
            return JSONResponse({"detail": "Размер запроса превышает лимит."}, status_code=413)
    except ValueError:
        return JSONResponse({"detail": "Некорректная длина запроса."}, status_code=400)
    response = await call_next(request)
    response.headers["X-Content-Type-Options"] = "nosniff"
    response.headers["Cache-Control"] = "no-store"
    response.headers["Referrer-Policy"] = "no-referrer"
    return response


@app.exception_handler(RequestValidationError)
async def validation_error(request, exc):
    return JSONResponse(
        {"detail": "Проверьте поля запроса, формат идентификаторов и длину пароля (от 12 символов)."}, 422
    )


@app.exception_handler(IntegrityError)
async def integrity_error(request, exc):
    return JSONResponse({"detail": "Имя или логин уже занят. Обновите список."}, 409)


@app.exception_handler(SQLAlchemyError)
async def database_error(request, exc):
    return JSONResponse({"detail": "База данных временно недоступна. Повторите запрос позже."}, 503)


@app.get("/health")
def health():
    try:
        with SessionLocal() as db:
            db.execute(text("SELECT 1"))
        if not settings.data_dir.is_dir():
            raise OSError()
    except (SQLAlchemyError, OSError):
        return JSONResponse({"status": "unavailable"}, status_code=503)
    return {"status": "ok"}
