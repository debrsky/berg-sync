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
  --mdb-date-offset-hours 0 \
  --confirm-drop
```

`--confirm-drop` обязателен: миграция удаляет `bergauto`, `bergapp` и состояние
предыдущего baseline в `berg_sync` с `CASCADE`. Access хранит локальное время без
часового пояса, поэтому по умолчанию даты переносятся без коррекции
(`--mdb-date-offset-hours 0`). Ненулевое значение следует задавать только для
источника, которому действительно требуется фиксированный сдвиг.

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
6. запускает `HOSTING_RESTORE_SCRIPT`, который после восстановления вызывает
   `berg_persistent.archive_invoices()`;
7. сравнивает baseline, `COUNT(*)` и `MAX(ID)` локальной и удалённой баз.

Полная локальная миграция вызывает `berg_persistent.archive_invoices()` после
создания materialized views. Полная публикация повторяет вызов локально после
успешной проверки хостинга. Схема и процедура должны быть заранее установлены в
локальной базе и на хостинге.

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
  --mdb-date-offset-hours 0
```

Для применения одной и той же дельты локально и на хостинге добавьте:

```powershell
--sync-hosting
```

Для штатной инкрементальной публикации доступен интерактивный интерфейс:

```powershell
dotnet run --project . -- --tui
```

Для удалённого деплоя Windows CLI запустите **на рабочей станции**
`scripts/deploy-remote-windows.ps1`. Адрес, порт, транспорт и имя пользователя
берутся из пользовательского inventory `winrm-mcp` (по умолчанию хост `bergvl`).
Пароля в inventory нет: скрипт читает локальную учётную запись `berg-winrm`
из Windows Credential Manager (или принимает явно переданный `-Credential`),
не выводя пароль.
Целевая машина должна быть Windows с включённым PowerShell Remoting (WinRM),
доступной учётной записью с правом записи в каталог назначения, установленными
.NET 10 и ACE OLE DB x64.
На рабочей станции требуется .NET 10 SDK. Сначала закройте `berg-sync.exe`
на целевой машине, затем из каталога проекта запустите:

```powershell
.\scripts\deploy-remote-windows.ps1
```

По умолчанию цель — `bergvl`, каталог — `C:\Tools\berg-sync`; для другого
каталога используйте `-Destination <путь>`.

По умолчанию скрипт выполняет `dotnet publish` для win-x64, создаёт новый ZIP
в `artifacts/` и публикует **именно этот** архив. Для сборки без деплоя есть
`-BuildOnly`. Чтобы повторно развернуть готовый ZIP без пересборки, укажите
`-Archive <путь>` (необязательно `-Sha256 <хеш>`).
Скрипт передаёт ZIP и установщик по WinRM, проверяет SHA-256 на обеих машинах,
а затем запускает `scripts/deploy-windows.ps1` **на целевой машине**. Там
создаётся резервная копия заменённых файлов рядом с каталогом назначения.
`.env`, `logs/` и `delta-packages/` не перезаписываются; старые файлы не удаляются.
Ярлыки «Berg Sync TUI» на `bergvl` запускают
`C:\ProgramData\BergSync\berg-sync.cmd` с рабочим каталогом `C:\Tools\berg-sync`;
исполняемый файл находится именно в `C:\Tools\berg-sync`. Конфигурация
`C:\ProgramData\BergSync\.env` не затрагивается.
Если пароль получен другим безопасным способом, можно передать `-Credential`.
При подключении по HTTP к IP-адресу PowerShell Remoting может потребовать
запись этого адреса в `TrustedHosts` на рабочей станции; скрипт не меняет
`TrustedHosts` автоматически. Скрипт обновляет только Windows CLI, не
PostgreSQL на хостинге.

TUI всегда использует `MDB_PATH` и предлагает два режима: инкрементальную
публикацию (выбрана по умолчанию) или полную миграцию с публикацией. Перед
запуском он показывает MDB и адреса обеих баз без учётных данных, запрашивает
подтверждение, выводит текущий этап и журнал `[PROFILE]`, а после завершения
показывает baseline, `delta_seq` и время. Для полной миграции отдельно выводится
предупреждение о замене схем локально и на хостинге. Параметр `--tui` не
комбинируется с режимами `--incremental`, `--migrate`, `--copy` и
`--publish-hosting`. При ошибке в любом режиме журнал выполнения сохраняется
в отдельном файле `logs/berg-sync-error-*.log` рядом с текущим рабочим каталогом;
путь к файлу выводится в терминал. При успешном завершении файл не создаётся.
`logs/` исключён из Git. Перед передачей журнала третьим лицам проверьте его:
он может содержать пути к MDB и служебные сведения о базах.

Режим `--incremental` (алиас `--delta`):

- сравнивает новые ID и документы за последние 90 дней;
- использует перекрытия ID: 10 000, для платежей 50 000, для позиций 20 000;
- достраивает связи счёт → заявка/платежи/позиции → клиенты;
- полностью сравнивает малые справочники;
- повторно читает фактически изменившиеся строки и откладывает нестабильные;
- применяет только `INSERT`/`UPDATE`, удаления остаются до полной миграции;
- пересчитывает затронутые финансовые пары и обновляет materialized views;
- после обновления представлений вызывает `berg_persistent.archive_invoices()`
  локально и, с `--sync-hosting`, на хостинге;
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
