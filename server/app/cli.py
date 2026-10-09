import argparse
import getpass
from pathlib import Path

from .admin import create_user, parse_csv
from .db import Base, SessionLocal, engine, metadata_guard
from .models import Audit
from .schemas import UserCreate


def main():
    parser = argparse.ArgumentParser(description="Управление общим файловым архивом")
    sub = parser.add_subparsers(dest="command", required=True)
    admin = sub.add_parser("create-admin", help="Создать администратора (пароль вводится скрыто)")
    admin.add_argument("--username", required=True)
    admin.add_argument("--full-name", required=True)
    imported = sub.add_parser("import-users", help="Атомарный импорт CSV, до 100 записей")
    imported.add_argument("path", type=Path)
    args = parser.parse_args()
    Base.metadata.create_all(engine)
    with SessionLocal() as db:
        metadata_guard(db)
        if args.command == "create-admin":
            password = getpass.getpass("Пароль (не менее 12 символов): ")
            if password != getpass.getpass("Повторите пароль: "):
                parser.error("Пароли не совпадают")
            try:
                data = UserCreate(
                    username=args.username, full_name=args.full_name, password=password, role="admin"
                )
            except ValueError:
                parser.error("Проверьте логин, ФИО и длину пароля; пароль не будет напечатан")
            create_user(db, data)
            db.add(Audit(action="create_admin_cli", detail=data.username))
            count = 1
        else:
            if args.path.stat().st_size > 1024 * 1024:
                parser.error("CSV больше 1 МБ")
            records = parse_csv(args.path.read_bytes())
            for record in records:
                create_user(db, record)
            count = len(records)
            db.add(Audit(action="import_users_cli", detail=f"Создано: {count}"))
        db.commit()
        print(f"Создано пользователей: {count}")


if __name__ == "__main__":
    main()
