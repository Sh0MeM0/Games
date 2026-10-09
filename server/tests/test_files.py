from concurrent.futures import ThreadPoolExecutor
from datetime import timedelta

from conftest import document

from app.db import SessionLocal
from app.models import Node, utcnow


def save(client, headers, node_id, lock, base, content):
    return client.post(
        f"/api/files/{node_id}/versions",
        headers=headers,
        data={"lock_token": lock, "base_version": base},
        files={"file": ("Учёт.docx", content)},
    )


def test_checkout_one_winner_and_read_access(client, alice, boris, uploaded):
    file_id = uploaded["id"]
    with ThreadPoolExecutor(max_workers=2) as pool:
        responses = list(
            pool.map(lambda h: client.post(f"/api/files/{file_id}/checkout", headers=h), [alice, boris])
        )
    assert sorted(r.status_code for r in responses) == [200, 423]
    assert client.get(f"/api/files/{file_id}/download", headers=boris).content == document()
    assert client.delete(f"/api/nodes/{file_id}", headers=alice).status_code == 409
    assert (
        client.patch(f"/api/nodes/{file_id}", headers=alice, json={"name": "Новое.docx"}).status_code == 409
    )


def test_expired_lock_fences_old_editor(client, alice, boris, uploaded):
    file_id = uploaded["id"]
    old = client.post(f"/api/files/{file_id}/checkout", headers=alice).json()
    with SessionLocal() as db:
        db.get(Node, file_id).lock_until = utcnow() - timedelta(seconds=1)
        db.commit()
    new = client.post(f"/api/files/{file_id}/checkout", headers=boris).json()
    assert new["lock_token"] != old["lock_token"]
    assert (
        save(client, alice, file_id, old["lock_token"], 1, document("Потерянная версия")).status_code == 409
    )
    assert (
        client.post(
            f"/api/files/{file_id}/release", headers=alice, json={"lock_token": old["lock_token"]}
        ).status_code
        == 409
    )
    assert (
        client.post(
            f"/api/files/{file_id}/heartbeat", headers=boris, json={"lock_token": new["lock_token"]}
        ).status_code
        == 200
    )


def test_versions_conflict_history_and_rollback(client, alice, uploaded):
    file_id = uploaded["id"]
    lock = client.post(f"/api/files/{file_id}/checkout", headers=alice).json()["lock_token"]
    second = document("Вторая версия")
    result = save(client, alice, file_id, lock, 1, second)
    assert result.status_code == 201 and result.json()["version"] == 2
    assert save(client, alice, file_id, lock, 1, document("Конфликт")).status_code == 409
    assert client.get(f"/api/files/{file_id}/download", headers=alice).content == second
    history = client.get(f"/api/files/{file_id}/versions", headers=alice).json()
    assert [v["number"] for v in history] == [2, 1]
    assert history[0]["author"] == "Alice"
    reverted = client.post(
        f"/api/files/{file_id}/rollback",
        headers=alice,
        json={"lock_token": lock, "base_version": 2, "version": 1},
    )
    assert reverted.status_code == 201 and reverted.json()["version"] == 3
    assert client.get(f"/api/files/{file_id}/download", headers=alice).content == document()
    assert len(client.get(f"/api/files/{file_id}/versions", headers=alice).json()) == 3
    assert (
        client.post(f"/api/files/{file_id}/release", headers=alice, json={"lock_token": lock}).status_code
        == 200
    )


def test_simultaneous_saves_cannot_overwrite(client, alice, uploaded):
    file_id = uploaded["id"]
    lock = client.post(f"/api/files/{file_id}/checkout", headers=alice).json()["lock_token"]
    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(
            pool.map(lambda value: save(client, alice, file_id, lock, 1, document(value)), ["A", "B"])
        )
    assert sorted(r.status_code for r in results) == [201, 409]
    assert len(client.get(f"/api/files/{file_id}/versions", headers=alice).json()) == 2


def test_trash_folder_restore_and_search(client, alice):
    folder = client.post("/api/folders", headers=alice, json={"name": "Документы"}).json()
    file = client.post(
        "/api/files",
        headers=alice,
        data={"parent_id": folder["id"]},
        files={"file": ("Баланс.xlsx", document("Баланс", ".xlsx"))},
    ).json()
    assert client.get("/api/nodes?search=Баланс", headers=alice).json()["items"][0]["id"] == file["id"]
    assert client.delete(f"/api/nodes/{folder['id']}", headers=alice).status_code == 200
    assert client.get(f"/api/files/{file['id']}/download", headers=alice).status_code == 404
    assert len(client.get("/api/trash", headers=alice).json()) == 1
    assert client.post(f"/api/nodes/{folder['id']}/restore", headers=alice).status_code == 200
    assert client.get(f"/api/files/{file['id']}/download", headers=alice).status_code == 200


def test_wrong_lock_and_force_unlock(client, alice, boris, admin, uploaded):
    file_id = uploaded["id"]
    lock = client.post(f"/api/files/{file_id}/checkout", headers=alice).json()["lock_token"]
    assert save(client, boris, file_id, lock, 1, document()).status_code == 409
    assert client.post(f"/api/files/{file_id}/force-unlock", headers=boris).status_code == 403
    assert client.post(f"/api/files/{file_id}/force-unlock", headers=admin).status_code == 200
    assert (
        client.post(f"/api/files/{file_id}/heartbeat", headers=alice, json={"lock_token": lock}).status_code
        == 409
    )


def test_upload_validation_size_and_path(client, alice, monkeypatch):
    from app.config import settings

    for name, content in [
        ("../evil.docx", document()),
        ("evil.exe", b"MZ"),
        ("fake.docx", b"MZ"),
        ("fake.xls", document()),
        ("CON.docx", document()),
    ]:
        assert client.post("/api/files", headers=alice, files={"file": (name, content)}).status_code in {
            415,
            422,
        }
    monkeypatch.setattr(settings, "max_upload_mb", 1)
    assert (
        client.post(
            "/api/files", headers=alice, files={"file": ("Large.docx", b"0" * (1024 * 1024 + 1))}
        ).status_code
        == 413
    )
    assert client.get("/api/nodes", headers=alice).json()["items"] == []


def test_name_collision_and_restore_conflict(client, alice, uploaded):
    assert (
        client.post("/api/files", headers=alice, files={"file": ("Учёт.docx", document())}).status_code == 409
    )
    client.delete(f"/api/nodes/{uploaded['id']}", headers=alice)
    assert (
        client.post("/api/files", headers=alice, files={"file": ("Учёт.docx", document())}).status_code == 201
    )
    assert client.post(f"/api/nodes/{uploaded['id']}/restore", headers=alice).status_code == 409
