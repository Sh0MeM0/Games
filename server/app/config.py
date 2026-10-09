from pathlib import Path

from pydantic import Field, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", extra="ignore", hide_input_in_errors=True)
    database_url: str
    jwt_secret: str = Field(min_length=48)
    data_dir: Path = Path("/data/files")
    access_minutes: int = Field(default=15, ge=1, le=60)
    refresh_days: int = Field(default=7, ge=1, le=30)
    lock_timeout_seconds: int = Field(default=180, ge=90, le=3600)
    max_upload_mb: int = Field(default=50, ge=1, le=500)
    login_max_attempts: int = Field(default=8, ge=2, le=100)
    login_window_seconds: int = Field(default=900, ge=30, le=86400)
    require_https: bool = True

    @field_validator("database_url")
    @classmethod
    def postgres_only(cls, value: str) -> str:
        if not value.startswith("postgresql+psycopg://"):
            raise ValueError("Используйте PostgreSQL: postgresql+psycopg://...")
        return value


settings = Settings()
