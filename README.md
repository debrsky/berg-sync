# Berg Sync

Новая .NET-версия полной миграции Microsoft Access MDB в PostgreSQL и
инкрементальной синхронизации. Проект никогда не изменяет MDB. PostgreSQL
изменяется только при явном указании `--copy`, `--migrate` или `--incremental`.

Документация:

- [OPERATION_TASKS.md](OPERATION_TASKS.md) — подробное описание выполняемых задач,
  команд, восстановления после ошибок и рекомендуемого расписания;
- [SYNC_WORKFLOW.md](SYNC_WORKFLOW.md) — алгоритмы, транзакционные границы,
  состояние синхронизации и ограничения.

## Требования

- Windows;
- .NET SDK 10;
- Microsoft Access Database Engine (ACE) той же разрядности, что и приложение;
- доступный PostgreSQL для проверки переноса.

По умолчанию проект собирается как `x64` и пробует провайдеры в таком порядке:

1. `Microsoft.ACE.OLEDB.16.0`;
2. `Microsoft.ACE.OLEDB.12.0`.

Если установлен только 32-битный ACE, замените в `berg-sync.csproj`:

```xml
<PlatformTarget>x64</PlatformTarget>
```

на `x86`.

## Настройка окружения

Скопируйте `.env.sample` в `.env` и заполните строки подключения:

```bash
cp .env.sample .env
```

При запуске `berg-sync` автоматически загружает `.env` из текущего каталога,
каталога приложения или корня проекта. Другой файл можно указать через
`BERG_SYNC_ENV_FILE`. Приоритет настроек:

1. аргумент `--pg`;
2. переменная процесса `PG_LOCAL_CONNECTION_STRING`;
3. значение `PG_LOCAL_CONNECTION_STRING` из `.env`.

Путь к исходной базе задаётся через `MDB_PATH`. Аргумент `--mdb` имеет над ним
приоритет. Для полной доставки на хостинг используются `HOSTING_SSH_TARGET`,
`HOSTING_TRANSFER_DIR`, `HOSTING_RESTORE_SCRIPT` и
`PG_HOSTING_CONNECTION_STRING`. Файл `.env` игнорируется Git.

## Запуск из `C:\WORK\berg_sync`

Показать таблицы:

```bash
dotnet run --project . --
```

В примере используется `MDB_PATH` из `.env`. Другой файл можно указать явно:

```bash
dotnet run --project . -- \
  --mdb C:/WORK/BERG/Berg/DB/bergauto.mdb
```

Проверить структуру и три строки таблицы:

```bash
dotnet run --project . -- \
  --mdb C:/WORK/BERG/Berg/DB/bergauto.mdb \
  --table XInvoices \
  --limit 3
```

Дополнительно посчитать строки:

```bash
dotnet run --project . -- \
  --mdb C:/WORK/BERG/Berg/DB/bergauto.mdb \
  --table XInvoices \
  --limit 1 \
  --count
```

Для явного выбора провайдера:

```bash
dotnet run --project . -- \
  --mdb C:/WORK/BERG/Berg/DB/bergauto.mdb \
  --provider Microsoft.ACE.OLEDB.12.0
```

## Тестовый перенос в PostgreSQL

Строку подключения безопаснее передать через окружение:

```bash
export PG_LOCAL_CONNECTION_STRING='Host=localhost;Port=5432;Database=bergdb;Username=postgres;Password=secret'
```

Перенос первых 1000 строк с пересозданием тестовой таблицы:

```bash
dotnet run --project . -- \
  --mdb C:/WORK/BERG/Berg/DB/bergauto.mdb \
  --table XInvoices \
  --limit 0 \
  --copy \
  --pg-schema mdb_poc \
  --pg-table XInvoices_test \
  --copy-limit 1000 \
  --replace
```

`--copy-limit 0` переносит всю таблицу. Без `--replace` существующая целевая
таблица не удаляется, а выполнение завершится ошибкой. Создание таблицы, бинарный
`COPY` и проверка количества строк выполняются в одной транзакции.

## Полная миграция

Режим `--migrate` повторяет основной процесс `migrate.js`:

1. создаёт целевую базу при необходимости и устанавливает `Asia/Vladivostok`;
2. пересоздаёт схемы `bergauto` и `bergapp`;
3. создаёт 10 таблиц из `migration-schema.json`;
4. переносит данные бинарным `COPY`, каждая таблица — в отдельной транзакции;
5. создаёт 41 индекс и выполняет `ANALYZE`;
6. рассчитывает `bergapp.operations`;
7. создаёт materialized views.

Запуск из корня проекта:

```bash
export PG_LOCAL_CONNECTION_STRING='Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=secret'

dotnet run --project . -- \
  --mdb C:/WORK/BERG/Berg/DB/bergauto.mdb \
  --migrate \
  --pg-database bergdb \
  --pg-admin-database postgres \
  --migration-limit 0 \
  --mdb-date-offset-hours 10 \
  --confirm-drop
```

`--confirm-drop` обязателен: миграция удаляет `bergauto`, `bergapp` и состояние
предыдущего baseline в `berg_sync` с `CASCADE`. Значение `--mdb-date-offset-hours 10`
повторяет преобразование дат из `migrate.js`; для переноса локального Access-времени
без коррекции укажите `0`.

В отличие от текущего `migrate.js`, .NET-версия прекращает выполнение при первой
ошибке.

Расчёт операций использует отдельный исправленный файл
`sql/get-operations.sql`. В нём:

- события с одинаковым timestamp дополнительно сортируются по уникальному ID
  (`XInvoices.ID` или `XInvoicePays.ID`);
- FIFO-очередь фильтруется через `unnest ... WITH ORDINALITY`, поэтому её порядок
  не теряется при очистке погашенных счетов.

После изменения `schema.config.mjs` обновите снимок схемы для .NET:

```bash
node generate-migration-schema.mjs
```

## Полная публикация на хостинг

После полной миграции локальную базу можно опубликовать на хостинг:

```bash
dotnet run --project . -- \
  --migrate \
  --confirm-drop \
  --publish-hosting \
  --confirm-hosting-restore
```

Также можно опубликовать уже подготовленную локальную базу с `delta_seq = 0`:

```bash
dotnet run --project . -- \
  --publish-hosting \
  --confirm-hosting-restore
```

Публикация:

1. проверяет локальный baseline и подключение к хостингу;
2. создаёт directory-format dump схем `bergauto`, `bergapp`, `berg_sync`;
3. формирует manifest, TAR.ZST и SHA-256;
4. загружает архив через SFTP с докачкой `reput`;
5. проверяет удалённый SHA-256 и атомарно активирует архив;
6. запускает `HOSTING_RESTORE_SCRIPT`;
7. сравнивает baseline, `COUNT(*)` и `MAX(ID)` локальной и удалённой баз.

`--confirm-hosting-restore` обязателен, поскольку на хостинге схемы заменяются.
При ошибке локальные файлы поставки сохраняются для диагностики.

Полная миграция и публикация выводят строки `[PROFILE]`. Для миграции отдельно
измеряются `COPY` каждой таблицы, индексы, `ANALYZE`, расчёт operations и создание
каждого materialized view. Для публикации измеряются контрольные снимки, dump,
TAR и Zstandard, проверка SHA-256, скорость SFTP, удалённое восстановление и
проверка каждой таблицы на хостинге.

## Инкрементальная выгрузка в локальный PostgreSQL

Сначала нужна полная миграция. После неё изменения из более свежего MDB можно
применить без пересоздания базы:

```bash
export PG_LOCAL_CONNECTION_STRING='Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=secret'

dotnet run --project . -- \
  --mdb C:/WORK/BERG/Berg/DB/bergauto.mdb \
  --incremental \
  --pg-database bergdb \
  --mdb-date-offset-hours 10
```

Для применения одной и той же дельты локально и на хостинге добавьте:

```powershell
--sync-hosting
```

Режим `--incremental` (алиас `--delta`):

- сравнивает новые ID и документы за последние 90 дней;
- использует перекрытия ID: 10 000, для платежей 50 000, для позиций 20 000;
- достраивает связи счёт → заявка/платежи/позиции → клиенты;
- полностью сравнивает малые справочники;
- повторно читает фактически изменившиеся строки и откладывает нестабильные;
- применяет только `INSERT`/`UPDATE`, удаления остаются до полной миграции;
- пересчитывает затронутые финансовые пары и обновляет materialized views;
- ведёт baseline, последовательность и журнал запусков в схеме `berg_sync`;
- отклоняет MDB, у которого максимальные ID меньше локальной базы;
- с `--sync-hosting` создаёт неизменяемый пакет с SHA-256, применяет его локально,
  затем к `PG_HOSTING_CONNECTION_STRING`;
- сохраняет состояние доставки в `berg_sync.patch_delivery`;
- при следующем запуске сначала повторяет доставку pending/failed-пакетов.

Полезные параметры:

```text
--period-days 90
--id-overlap 10000
--payment-id-overlap 50000
--invoice-data-id-overlap 20000
--full-customers
--delta-max-changes 100000
--skip-recalculate
--skip-refresh-views
--sync-hosting
--delta-package-dir C:/WORK/berg_sync/delta-packages
```

`--full-customers` предназначен для более редкого полного прохода `Customers`
(например, раз в 4 часа). Параметры `--skip-*` полезны для диагностики; в штатном
запуске пересчёт и refresh лучше не отключать. Коррекция дат обязана совпадать со
значением, использованным при полной миграции.

Инкрементальный режим выводит строки `[PROFILE]` с длительностью основных
локальных этапов и подробной разбивкой публикации: создание и проверка пакета,
подключение и блокировка, `COPY`/`UPSERT` по таблицам, выгрузка и замена готовых
строк `operations`, обновление каждого materialized view, запись метаданных,
`COMMIT` и итоговая проверка на хостинге. Снимок операций хранится в пакете как
сжатый бинарный COPY-файл и защищён SHA-256.

## Возможности диагностического режима

- доступность ACE OLE DB;
- возможность открытия существующего MDB;
- список пользовательских таблиц;
- типы и nullable-статус колонок;
- корректное чтение дат, чисел, boolean, текста, `NULL` и бинарных данных;
- автоматическое базовое сопоставление типов Access → PostgreSQL;
- перенос через `NpgsqlBinaryImporter` (`COPY ... FORMAT BINARY`);
- совпадение числа прочитанных и записанных строк перед фиксацией транзакции.
