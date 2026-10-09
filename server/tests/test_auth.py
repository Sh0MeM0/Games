from conftest import PASSWORD
from sqlalchemy import select

from app.config import settings
from app.db import SessionLocal
from app.models import User


def test_password_hash_and_login(client):
    with SessionLocal() as db:
        user = db.scalar(select(User).where(User.username == "alice"))
        assert user.password_hash.startswith("$argon2id$")
        assert PASSWORD not in user.password_hash
    bad = client.post("/api/auth/login", json={"username": "alice", "password": "wrong"})
    missing = client.post("/api/auth/login", json={"username": "unknown", "password": "wrong"})
    assert bad.status_code == missing.status_code == 401
    assert bad.json() == missing.json()
    assert client.get("/api/nodes").status_code == 401


def test_rate_limit_survives_sessions(client, monkeypatch):
    monkeypatch.setattr(settings, "login_max_attempts", 3)
    for _ in range(3):
        assert (
            client.post("/api/auth/login", json={"username": "alice", "password": "bad"}).status_code == 401
        )
    response = client.post("/api/auth/login", json={"username": "alice", "password": PASSWORD})
    assert response.status_code == 429
    assert "Retry-After" in response.headers


def test_refresh_rotation_replay_revokes_session(client):
    original = client.post("/api/auth/login", json={"username": "alice", "password": PASSWORD}).json()
    rotated = client.post("/api/auth/refresh", json={"refresh_token": original["refresh_token"]})
    assert rotated.status_code == 200
    assert rotated.json()["refresh_token"] != original["refresh_token"]
    assert (
        client.post("/api/auth/refresh", json={"refresh_token": original["refresh_token"]}).status_code == 401
    )
    header = {"Authorization": "Bearer " + rotated.json()["access_token"]}
    assert client.get("/api/auth/me", headers=header).status_code == 401


def test_logout_revokes_access(client, alice):
    assert client.post("/api/auth/logout", headers=alice).status_code == 200
    assert client.get("/api/nodes", headers=alice).status_code == 401


def test_admin_block_and_password_reset(client, alice, admin):
    assert client.get("/api/admin/users", headers=alice).status_code == 403
    users = client.get("/api/admin/users", headers=admin).json()
    user_id = next(u["id"] for u in users if u["username"] == "alice")
    assert (
        client.patch(f"/api/admin/users/{user_id}", headers=admin, json={"active": False}).status_code == 200
    )
    assert client.get("/api/nodes", headers=alice).status_code == 401
    assert client.post("/api/auth/login", json={"username": "alice", "password": PASSWORD}).status_code == 401
    client.patch(f"/api/admin/users/{user_id}", headers=admin, json={"active": True})
    assert (
        client.post(
            f"/api/admin/users/{user_id}/password", headers=admin, json={"password": "New-password-12345"}
        ).status_code
        == 200
    )
    assert client.post("/api/auth/login", json={"username": "alice", "password": PASSWORD}).status_code == 401
    assert (
        client.post(
            "/api/auth/login", json={"username": "alice", "password": "New-password-12345"}
        ).status_code
        == 200
    )


def test_csv_import_atomic_and_no_password_leak(client, admin):
    data = "username,full_name,password,role\nnewuser,Иван,Unique-password-123,user\n"
    result = client.post("/api/admin/users/import", headers=admin, files={"file": ("users.csv", data)})
    assert result.status_code == 201 and result.json()["created"] == 1
    bad = "username,full_name,password\nsecond,Пётр,Another-password-123\nnewuser,Иван,Unique-password-123\n"
    assert (
        client.post("/api/admin/users/import", headers=admin, files={"file": ("users.csv", bad)}).status_code
        == 409
    )
    assert "second" not in [u["username"] for u in client.get("/api/admin/users", headers=admin).json()]
    response = client.post(
        "/api/admin/users",
        headers=admin,
        json={"username": "invalid!", "full_name": "Иван", "password": "Secret-value"},
    )
    assert response.status_code == 422 and "Secret-value" not in response.text


def test_https_only(client):
    assert client.get("http://testserver/api/nodes").status_code == 400
