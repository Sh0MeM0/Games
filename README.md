# Общий файловый архив

FastAPI/Python 3.12 + PostgreSQL + файлы на диске; Windows-клиент Electron с установщиком NSIS. До 30 сотрудников работают с общими Word/Excel-документами по HTTPS. Все активные сотрудники видят общий архив; администратор дополнительно управляет пользователями и журналом.

Вход, папки, загрузка/скачивание, поиск, переименование, корзина, check-out, автоотправка сохранённых файлов, история и откат реализованы. После потери блокировки клиент сохраняет локальную копию и прекращает автозагрузку.

**Для надёжного завершения редактирования сохраните и закройте документ в Word/Excel, затем нажмите «Завершить».** Автоматическое определение закрытия Office — необязательная эвристика, выключенная по умолчанию. Для гарантированного автоматического закрытия во всех версиях Office нужна отдельная интеграция, например ONLYOFFICE/Collabora. Подробнее: [архитектура и ограничения](docs/ARCHITECTURE.md).

## Скачать проект и установщик

Исходники проекта находятся в ветке [`shared-file-archive`](https://github.com/Sh0MeM0/Games/tree/shared-file-archive): нажмите **Code → Download ZIP**. Готовые установщики публикуются в [GitHub Releases](https://github.com/Sh0MeM0/Games/releases); скачайте `SharedArchive-Setup-1.0.0.exe` в разделе **Assets**. Первая сборка появляется после успешного завершения [Windows installer в GitHub Actions](https://github.com/Sh0MeM0/Games/actions/workflows/windows-installer.yml).

Тег `shared-archive-v*` запускает клиентские тесты, сборку Windows x64 и публикацию предварительного релиза с установщиком и SHA-256. Подпись издателя не добавляется; проверка установки и работы Office на настоящей Windows остаётся отдельным шагом.

## Структура

```text
server/
  app/                 # API, модели, авторизация, файловые операции, администрирование, CLI
  tests/               # Критические тесты на реальном PostgreSQL
  pyproject.toml
  uv.lock              # Зафиксированные Python-зависимости
  Dockerfile
client/
  src/main/            # HTTPS, DPAPI-хранилище токенов, синхронизация Office
  src/preload/         # Ограниченный IPC-интерфейс
  src/renderer/        # Русский интерфейс React
  src/shared/
  tests/               # Тесты сети, refresh и локальных рабочих копий
  package.json
  package-lock.json
docker-compose.yml
Caddyfile
.env.example
scripts/backup.sh
scripts/restore.sh
tests/smoke.py          # Функциональная проверка через HTTPS; только тестовая БД
docs/WINDOWS-CHECKLIST.md
docs/ARCHITECTURE.md
```

## 1. Развёртывание на VPS Ubuntu 24.04

Понадобятся домен, DNS A-запись на IP VPS, минимум 2 CPU / 2 ГБ RAM, диск с запасом под все версии и бэкапы. Удалите ошибочную AAAA-запись, если IPv6 на VPS не настроен. Порты 80 и 443 должны быть доступны извне; 5432 и 8000 публиковать не нужно.

Установите Docker Engine и Compose из официального репозитория Docker (проверка подписей остаётся включённой):

```bash
sudo apt update
sudo apt install -y ca-certificates curl git python3
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
. /etc/os-release
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${UBUNTU_CODENAME:-$VERSION_CODENAME} stable" | sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
sudo apt update
sudo apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo systemctl enable --now docker
```

Далее команды выполняются из корня репозитория. Пользователь запуска Docker должен иметь доступ к daemon; приведённые команды на VPS используют `sudo`.

Клонируйте ветку `shared-file-archive`, содержащую проект. Если используете скачанный ZIP, распакуйте его в `/opt/shared-archive` и выполняйте команды из этой папки.

```bash
sudo git clone --branch shared-file-archive https://github.com/Sh0MeM0/Games.git /opt/shared-archive
cd /opt/shared-archive
sudo cp .env.example .env
sudo chmod 600 .env
sudo nano .env
```

Укажите настоящий `DOMAIN`, `ACME_EMAIL`, два разных случайных секрета. Получить их можно локально командами:

```bash
python3 -c "import secrets; print(secrets.token_hex(32))"  # POSTGRES_PASSWORD
python3 -c "import secrets; print(secrets.token_hex(48))"  # JWT_SECRET
```

Не отправляйте секреты в чат и не коммитьте `.env`. Для `POSTGRES_PASSWORD` используйте hex, как в примере, чтобы не требовалось URL-кодирование. Если поднимаете лимит файла, увеличьте и `MAX_UPLOAD_MB`, и `MAX_REQUEST_SIZE` (последний — на 1 МБ больше). `LOCK_TIMEOUT_SECONDS` допускает 90–3600 секунд; значение по умолчанию — 180.

```bash
sudo docker compose config --quiet
sudo docker compose up -d --build --wait
sudo docker compose ps
curl --fail https://ВАШ-ДОМЕН/health
```

Ожидается `{"status":"ok"}`. Caddy автоматически получает и продлевает сертификат Let's Encrypt. Если DNS ещё не распространился или 80/443 закрыты, сертификат не будет получен: проверьте `sudo docker compose logs --tail=100 proxy`. Не отключайте проверку TLS в клиенте.

HTTP на порту 80 используется для ACME и перенаправления на HTTPS; API требует HTTPS. База и API находятся только во внутренней Docker-сети. Не добавляйте публичный `ports` для `db`/`server`: это нарушит модель доверия к proxy-заголовкам.

Обновление: сначала бэкап, затем получить проверенную новую версию кода и выполнить `sudo docker compose up -d --build --wait`. Схема v1 создаётся при первом старте; будущие изменения схемы должны поставляться отдельными миграциями. Не меняйте PostgreSQL major-version без предусмотренной процедуры миграции.

Изменение `POSTGRES_PASSWORD` в `.env` не меняет пароль уже инициализированной PostgreSQL: его нужно отдельно обновить в БД. Смена `JWT_SECRET` завершает действующие JWT-сеансы.

## 2. Первый администратор и 30 пользователей

Создайте администратора интерактивно (пароль вводится скрыто и не попадает в командную строку):

```bash
sudo docker compose exec server python -m app.cli create-admin \
  --username admin --full-name "Администратор организации"
```

Пароль — не менее 12 символов. Не существует встроенного стандартного пароля. Первый администратор создаётся только через доступ к серверу; публичного bootstrap-endpoint нет.

Установите клиент, введите `https://ВАШ-ДОМЕН`, логин и пароль. В разделе «Пользователи» можно создавать сотрудников, блокировать/разблокировать их и сбрасывать пароли. Блокировка/сброс отзывают существующие сеансы. Собственную запись нельзя заблокировать; сброс собственного пароля в интерфейсе отключён — воспользуйтесь другим администратором.

Массовый импорт: создайте `users.csv` в UTF-8 (допускается BOM), разделитель — запятая, первая строка:

```csv
username,full_name,password,role
ivanov,Иванов Иван,Индивидуальный-длинный-пароль-1,user
petrova,Петрова Анна,Другой-индивидуальный-пароль-2,user
```

Логин: 3–64 латинские буквы/цифры/точки/дефисы/подчёркивания; регистр не различается. `role` — `user` или `admin`, столбец необязателен. Пароли должны быть индивидуальными. Сделайте 30 строк сотрудников, затем нажмите «Импорт CSV». За один раз допускается до 100 записей; при ошибке ни одна запись не создаётся. Повторный импорт существующих логинов отклоняется.

Можно импортировать через CLI:

```bash
chmod 600 users.csv
sudo docker compose exec -T server sh -c 'umask 077; cat > /tmp/users.csv' < users.csv
sudo docker compose exec server python -m app.cli import-users /tmp/users.csv
sudo docker compose exec server rm /tmp/users.csv
rm users.csv
```

CSV временно содержит открытые пароли: передайте их сотрудникам защищённым каналом, удалите CSV и не добавляйте его в Git. Пароли в БД хранятся только как Argon2id-хэши.

## 3. Сборка и установка Windows-клиента

На Windows 10/11 x64 установите Node.js 24 LTS и Git. Используйте PowerShell:

```powershell
git clone https://github.com/Sh0MeM0/Games.git
cd Games\client
npm ci
npm test
npm run dist:win
```

Результат: `client/release/SharedArchive-Setup-1.0.0.exe`. Мастер NSIS на русском позволяет выбрать каталог, создаёт ярлыки рабочего стола и меню «Пуск», регистрирует деинсталлятор. Word/Excel устанавливаются отдельно. На первом входе сотрудник вводит HTTPS-адрес сервера; адрес не зашит в приложение.

Установщик без сертификата издателя не подписан: Windows SmartScreen может показывать предупреждение. Для распространения в организации подпишите выпуск своим Authenticode-сертификатом через поддерживаемые electron-builder `CSC_LINK` / `CSC_KEY_PASSWORD` или корпоративный signing-service; задавайте их защищёнными переменными окружения. Не храните сертификат/пароль в репозитории.

Linux также может собрать NSIS. Пользовательский сценарий `client/build/installer.nsi` создаёт деинсталлятор во время установки на Windows, поэтому Wine не требуется. В облачной среде кэши должны быть доступны для записи; инструкции среды сохраняют соответствующие пути. При сборке на Windows отдельные настройки кэша не нужны.

В подготовленной Linux-среде можно запустить `bash scripts/build-windows-linux.sh` из корня. Скрипт сохраняет кэши вне checkout и создаёт неподписанный NSIS-установщик. В этой сборке пропускается изменение PE-метаданных исполняемого файла Electron; название приложения в интерфейсе, ярлыках и списке программ остаётся русским. Для выпуска с Authenticode используйте Windows-сборку; создаваемый при установке деинсталлятор этим способом не подписывается. `bash scripts/cloud-setup.sh` устанавливает зафиксированные зависимости, `bash scripts/cloud-start.sh` запускает локальные сервисы разработки с тестовым HTTPS. Для VPS используйте основной Compose без тестового override.

После сборки пройдите [проверку на Windows](docs/WINDOWS-CHECKLIST.md). Тесты Node и Linux-сборка не заменяют проверку установленного Office, ярлыков и деинсталляции на Windows 10/11.

## 4. Работа сотрудника

1. Войти и выбрать документ. Список обновляется каждые 10 секунд; кнопка «Обновить» запрашивает его сразу.
2. «Для чтения» открывает отдельную read-only копию. «Открыть для редактирования» получает блокировку и открывает рабочую копию в установленном Word/Excel.
3. Сохранить документ в Office. Через несколько секунд клиент загрузит стабильный сохранённый файл как новую версию. Изменения, которые ещё находятся в памяти Office и не сохранены на диск, клиент прочитать не может.
4. Сохранить и закрыть документ в Office, нажать «Завершить», дождаться подтверждения. Затем выйти.
5. При отсутствии сети не удалять рабочую копию. Если блокировка истекла или возник конфликт, нажать «Сохранить копию», прекратить обработку, открыть свежую версию и вручную перенести нужные изменения. Безусловная перезапись по старой блокировке запрещена.

Кнопка «Войти снова» позволяет восстановить авторизацию той же учётной записи. После повторного входа рабочие копии переводятся в режим ручного восстановления, поскольку новая сессия не владеет прежними блокировками. При закрытии клиента копии остаются в AppData, а блокировки истекают по тайм-ауту.

## 5. Ежедневный бэкап и восстановление

Бэкап включает согласованный dump PostgreSQL, все версии документов и SHA-256. Конфигурацию `.env` и сертификаты/ключи Caddy храните отдельно в защищённом хранилище: они не включены в ежедневный архив. Файлы остаются в корзине/истории, поэтому диск растёт с каждым сохранением.

```bash
cd /opt/shared-archive
sudo bash scripts/backup.sh
sudo crontab -e
```

Если VPS использует UTC, добавьте строку для ежедневного запуска в 02:00 по Москве (23:00 UTC). Для другого часового пояса VPS пересчитайте время cron:

```cron
0 23 * * * /bin/bash /opt/shared-archive/scripts/backup.sh >> /var/log/shared-archive-backup.log 2>&1
```

Бэкапы сохраняются в `backups/YYYYMMDDTHHMMSSZ`, хранятся 30 дней. Можно задать `BACKUP_DIR` переменной окружения. Копируйте их на отдельное защищённое хранилище и регулярно проверяйте восстановление; копия на том же VPS не защищает от потери сервера.

Восстановление заменяет данные. Сначала сделайте новый бэкап текущей БД и файлов, сохраните `.env`. Восстанавливайте только доверенный архив, созданный вашим скриптом:

```bash
sudo bash scripts/restore.sh /полный/путь/к/бэкапу --confirm-overwrite
sudo docker compose ps
curl --fail https://ВАШ-ДОМЕН/health
```

Восстановление проверяет контрольные суммы, останавливает API/proxy, восстанавливает БД и файлы, затем запускает сервисы. При ошибке сервисы остаются остановленными. После успеха проверьте вход, историю и скачивание документа; сотрудники должны войти повторно, если поменялся JWT-ключ.

## 6. Разработка и автотесты

Используйте существующий checkout. Каждая облачная задача уже изолирована — создавать дополнительный Git worktree не требуется.

Для Python используйте `uv` и Python 3.12; lockfile не обновляется во время установки:

```bash
cd server
uv sync --frozen
docker run -d --name archive-test-db \
  -e POSTGRES_USER=archive -e POSTGRES_PASSWORD=archive-local-test-only \
  -e POSTGRES_DB=archive_tests -p 127.0.0.1:55432:5432 postgres:17-alpine
# Дождитесь: docker exec archive-test-db pg_isready -U archive -d archive_tests
TEST_DATABASE_URL=postgresql+psycopg://archive:archive-local-test-only@127.0.0.1:55432/archive_tests uv run --frozen pytest
uv run --frozen ruff check app tests
cd ../client
npm ci
npm test
npm run build
```

**Серверные тесты удаляют таблицы.** Runner разрешает только отдельную БД с именем `archive_tests`; никогда не указывайте рабочую БД. Пароль в этом примере предназначен только для одноразовой локальной тестовой БД с портом на loopback. В production используйте случайные секреты из `.env`.

API можно запускать `uv run --frozen uvicorn app.main:app` с `DATABASE_URL`, `JWT_SECRET`, `DATA_DIR` из локального env-файла, однако для полноценного клиента нужен HTTPS reverse proxy. Для тестового Compose предусмотрен `docker-compose.test.yml` с внутренним сертификатом Caddy и привязкой только к loopback. Compose должен быть не ниже 2.24.4 (`!override`). Не используйте этот override на публичном VPS и не отключайте проверку сертификата: локальные HTTP-клиенты должны доверять конкретному корневому CA Caddy.

`tests/smoke.py` создаёт 30 тестовых пользователей и документы. Запускайте только на одноразовой БД; пароль тестового администратора передаётся через `ARCHIVE_SMOKE_PASSWORD`, CA — параметром `--ca`. Сценарий проверяет настоящий HTTPS-вход, импорт, checkout, сохранение, скачивание, откат, корзину и отзыв сеанса.

API: `/api/auth/{login,refresh,me,logout}`, `/api/nodes`, `/api/folders`, `/api/files`, `/api/files/{id}/{checkout,heartbeat,release,download,versions,rollback}`, `/api/trash`, `/api/admin/{users,audit}`. Для диагностики структура доступна в `/openapi.json` через HTTPS. Swagger UI отключён, внешние CDN не используются.
