# AGENTS.md

## Проект

`berg-sync` — Windows/.NET 10 x64 CLI для чтения Microsoft Access MDB через ACE OLE DB и синхронизации с PostgreSQL. MDB никогда не изменяется.

Режимы:

- диагностика MDB;
- перенос одной таблицы;
- полная миграция в локальный PostgreSQL;
- полная публикация на хостинг;
- инкрементальная INSERT/UPDATE-синхронизация локально и на хостинг.

## Ключевые файлы

- `Program.cs` — CLI и выбор режима.
- `FullMigration.cs` — полная миграция.
- `IncrementalSync.cs` — поиск и локальное применение дельты.
- `RemoteDeltaDelivery.cs` — delta-пакеты и удалённое применение.
- `HostingPublisher.cs` — dump, TAR.ZST, SFTP и проверка хостинга.
- `migration-schema.json` — таблицы, колонки и индексы.
- `sql/operations/`, `sql/views/` — расчётные объекты PostgreSQL.
- `scripts/restore-hosting.sh` — актуальное восстановление на сервере.

Подробности: `README.md`, `SYNC_WORKFLOW.md`, `OPERATION_TASKS.md`, `scripts/README.md`.

## Конфигурация

`.env` содержит секреты и игнорируется Git. Шаблон — `.env.sample`.

Основные переменные: `MDB_PATH`, `PG_LOCAL_CONNECTION_STRING`, `PG_HOSTING_CONNECTION_STRING`, `DELTA_PACKAGE_DIR`, `HOSTING_SSH_TARGET`, `HOSTING_TRANSFER_DIR`, `HOSTING_RESTORE_SCRIPT`.

Никогда не выводить и не коммитить строки подключения или пароли.

## Команды

```bash
# Проверка
dotnet build --no-restore
bash -n scripts/restore-hosting.sh
git diff --check

# Полная миграция локально
dotnet run --project . -- --mdb C:/path/db.mdb --migrate --confirm-drop

# Полная миграция и публикация
dotnet run --project . -- --mdb C:/path/db.mdb --migrate --confirm-drop \
  --publish-hosting --confirm-hosting-restore

# Публикация подготовленной базы (требуется delta_seq=0)
dotnet run --project . -- --publish-hosting --confirm-hosting-restore

# Инкрементальная публикация
dotnet run --project . -- --incremental --sync-hosting
```

Все аргументы описаны в `Program.cs: Options.PrintHelp`.

## Важные инварианты

- Полная миграция пересоздаёт `bergauto`, `bergapp`, `berg_sync` и создаёт baseline с `delta_seq=0`.
- Полная публикация заменяет эти схемы на хостинге и сверяет baseline, `COUNT(*)`, `MAX(ID)`.
- Инкрементальный режим применяет только INSERT/UPDATE; удаления ждут полной миграции.
- После общей полной публикации не чередовать `--incremental` и `--incremental --sync-hosting`: последовательности баз разойдутся.
- Pending/failed-пакеты доставляются до создания новой дельты.
- Delta-пакеты неизменяемы (`package.json` + `package.sha256`) и автоматически не удаляются.
- XML-функции удалены и не должны возвращаться.
- `berg_persistent` не входит в dump и не пересоздаётся; после полной и инкрементальной синхронизации вызывается существующая `berg_persistent.archive_invoices()` локально и на хостинге.
- Restore-скрипт удаляет legacy XML-функции перед `pg_restore --clean --single-transaction`.

## Профилирование

Строки `[PROFILE]` выводятся для полной миграции, полной публикации и инкрементального режима. Измеряются COPY/UPSERT, индексы, ANALYZE, operations, каждый materialized view, dump, сжатие, SFTP и restore.

Известное узкое место — `REFRESH bergapp.invoices`. Прямое удалённое применение также чувствительно к сетевому RTT.

## Безопасность

- Не запускать `--migrate --confirm-drop` или `--publish-hosting --confirm-hosting-restore` без явного согласия пользователя.
- Перед разрушительной операцией явно назвать выбранный MDB; для архивов сначала предложить список файлов.
- Проверять `git status` и сохранять чужие незакоммиченные изменения.
- Не делать commit без прямой просьбы.
- После изменения `scripts/restore-hosting.sh` production-копию нужно отдельно обновить на пути `HOSTING_RESTORE_SCRIPT`.
