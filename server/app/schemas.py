import re
import unicodedata
from typing import Literal

from pydantic import BaseModel, Field, field_validator


def normalize_username(value: str) -> str:
    value = value.strip().lower()
    if not re.fullmatch(r"[a-z0-9_.-]{3,64}", value):
        raise ValueError("Логин: 3–64 латинских буквы, цифры, точка, дефис или подчёркивание")
    return value


def validate_name(value: str) -> str:
    value = unicodedata.normalize("NFC", value).strip()
    if (
        not value
        or len(value) > 180
        or value in {".", ".."}
        or any(c in value for c in '/\\<>:"|?*')
        or value.endswith((".", " "))
        or any(ord(c) < 32 for c in value)
        or value.split(".")[0].upper()
        in {
            "CON",
            "PRN",
            "AUX",
            "NUL",
            *(f"COM{i}" for i in range(1, 10)),
            *(f"LPT{i}" for i in range(1, 10)),
        }
    ):
        raise ValueError("Недопустимое имя файла или папки")
    return value


class LoginIn(BaseModel):
    username: str = Field(min_length=1, max_length=64)
    password: str = Field(min_length=1, max_length=256)


class RefreshIn(BaseModel):
    refresh_token: str = Field(max_length=2048)


class UserCreate(BaseModel):
    username: str
    full_name: str = Field(min_length=1, max_length=160)
    password: str = Field(min_length=12, max_length=256)
    role: Literal["user", "admin"] = "user"
    _username = field_validator("username")(normalize_username)


class PasswordIn(BaseModel):
    password: str = Field(min_length=12, max_length=256)


class ActiveIn(BaseModel):
    active: bool


class NameIn(BaseModel):
    name: str
    _name = field_validator("name")(validate_name)


class FolderIn(NameIn):
    parent_id: str | None = None


class LockIn(BaseModel):
    lock_token: str = Field(min_length=16, max_length=64)


class RollbackIn(LockIn):
    base_version: int = Field(ge=1)
    version: int = Field(ge=1)
