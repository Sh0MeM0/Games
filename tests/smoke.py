"""Functional HTTPS check. Run only against a disposable local/staging database."""

import argparse
import os
import ssl
import sys
import uuid
from pathlib import Path

import httpx

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "server" / "tests"))
from office import document


def main():
    parser = argparse.ArgumentParser(description="HTTPS smoke-test (только тестовая БД)")
    parser.add_argument("--url", required=True)
    parser.add_argument("--ca", required=True)
    parser.add_argument("--username", default="admin")
    parser.add_argument("--password-env", default="ARCHIVE_SMOKE_PASSWORD")
    args = parser.parse_args()
    password = os.environ[args.password_env]
    with httpx.Client(
        base_url=args.url, verify=ssl.create_default_context(cafile=args.ca), trust_env=False, timeout=20
    ) as client:
        health = client.get("/health")
        health.raise_for_status()
        assert health.json() == {"status": "ok"}
        login = client.post("/api/auth/login", json={"username": args.username, "password": password})
        login.raise_for_status()
        client.headers["Authorization"] = "Bearer " + login.json()["access_token"]
        tag = uuid.uuid4().hex[:8]
        csv_data = "username,full_name,password,role\n" + "".join(
            f"smoke{tag}{i:02},Сотрудник {i:02},Smoke-password-{tag}-{i:02},user\n" for i in range(1, 31)
        )
        imported = client.post("/api/admin/users/import", files={"file": ("users.csv", csv_data.encode())})
        assert imported.status_code == 201 and imported.json()["created"] == 30, imported.text
        folder = client.post("/api/folders", json={"name": "Проверка " + tag})
        assert folder.status_code == 201, folder.text
        upload = client.post(
            "/api/files",
            data={"parent_id": folder.json()["id"]},
            files={"file": ("Учёт.docx", document("one"))},
        )
        assert upload.status_code == 201, upload.text
        file_id = upload.json()["id"]
        lock = client.post(f"/api/files/{file_id}/checkout").json()
        assert client.post(f"/api/files/{file_id}/checkout").status_code == 423
        saved = client.post(
            f"/api/files/{file_id}/versions",
            data={"lock_token": lock["lock_token"], "base_version": 1},
            files={"file": ("Учёт.docx", document("two"))},
        )
        assert saved.status_code == 201 and saved.json()["version"] == 2, saved.text
        assert client.get(f"/api/files/{file_id}/download").content == document("two")
        reverted = client.post(
            f"/api/files/{file_id}/rollback",
            json={"lock_token": lock["lock_token"], "base_version": 2, "version": 1},
        )
        assert reverted.status_code == 201 and reverted.json()["version"] == 3, reverted.text
        assert client.get(f"/api/files/{file_id}/download").content == document("one")
        assert (
            client.post(f"/api/files/{file_id}/release", json={"lock_token": lock["lock_token"]}).status_code
            == 200
        )
        assert client.delete(f"/api/nodes/{folder.json()['id']}").status_code == 200
        assert client.post(f"/api/nodes/{folder.json()['id']}/restore").status_code == 200
        assert len(client.get(f"/api/files/{file_id}/versions").json()) == 3
        assert client.get("/api/admin/audit").status_code == 200
        assert client.post("/api/auth/logout").status_code == 200
        assert client.get("/api/nodes").status_code == 401
    print("HTTPS: вход, импорт 30 пользователей, файл, блокировка, сохранение, откат, корзина и выход — OK")


if __name__ == "__main__":
    main()
