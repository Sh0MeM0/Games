import os
import secrets

import pytest
from fastapi.testclient import TestClient
from office import document
from sqlalchemy.engine import make_url

test_database = os.environ.get("TEST_DATABASE_URL")
if not test_database or make_url(test_database).database != "archive_tests":
    raise RuntimeError("Тесты удаляют таблицы. Укажите TEST_DATABASE_URL с отдельной БД archive_tests.")
os.environ["DATABASE_URL"] = test_database
os.environ["JWT_SECRET"] = secrets.token_urlsafe(64)
os.environ["DATA_DIR"] = "/tmp/archive-server-tests"

from app.auth import hash_password
from app.config import settings
from app.db import Base, SessionLocal, engine
from app.main import app
from app.models import User

PASSWORD = "Test-password-12345"


@pytest.fixture(autouse=True)
def database(tmp_path):
    settings.data_dir = tmp_path / "files"
    Base.metadata.drop_all(engine)
    Base.metadata.create_all(engine)
    with SessionLocal() as db:
        for username, role in [("admin", "admin"), ("alice", "user"), ("boris", "user")]:
            db.add(
                User(
                    username=username,
                    full_name=username.capitalize(),
                    role=role,
                    password_hash=hash_password(PASSWORD),
                )
            )
        db.commit()
    yield


@pytest.fixture
def client(database):
    with TestClient(app, base_url="https://testserver") as value:
        yield value


def auth(client, username="alice"):
    response = client.post("/api/auth/login", json={"username": username, "password": PASSWORD})
    assert response.status_code == 200, response.text
    return {"Authorization": "Bearer " + response.json()["access_token"]}


@pytest.fixture
def alice(client):
    return auth(client)


@pytest.fixture
def boris(client):
    return auth(client, "boris")


@pytest.fixture
def admin(client):
    return auth(client, "admin")


@pytest.fixture
def uploaded(client, alice):
    response = client.post(
        "/api/files", headers=alice, files={"file": ("Учёт.docx", document(), "application/octet-stream")}
    )
    assert response.status_code == 201, response.text
    return response.json()
