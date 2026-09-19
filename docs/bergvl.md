# Устройство `bergvl`

Документ фиксирует фактическую конфигурацию узла на 20 сентября 2026 года.
Секреты и строки подключения здесь намеренно не приводятся.

## 1. Назначение узла

`bergvl` — имя узла в WinRM-инвентаре. Фактическое имя Windows-компьютера —
`BA` (`192.168.179.10`). На нём находятся:

- рабочая Microsoft Access база `bergauto.mdb`;
- локальный PostgreSQL;
- опубликованная Windows x64 сборка `berg-sync`;
- конфигурация синхронизации;
- TUI для ручного запуска;
- задача Планировщика для ежедневной полной миграции и публикации на хостинг.

Общий поток данных:

```text
C:\Berg\DB\bergauto.mdb
    │ только чтение
    ▼
berg-sync на BA
    ├──► локальный PostgreSQL 17.4, localhost:5432
    └──► TAR.ZST + SHA-256 ──SFTP/SSH──► хостинг
                                         │
                                         └── restore-hosting.sh
                                             заменяет bergauto, bergapp,
                                             berg_sync в PostgreSQL хостинга
```

MDB программа не изменяет.

## 2. Система и PostgreSQL

- ОС: Windows Server 2019 Datacenter x64.
- Часовой пояс: `Vladivostok Standard Time` (UTC+10).
- WinRM слушает TCP 5985.
- PostgreSQL 17.4 установлен в `C:\BERGAPP\pgsql`.
- PostgreSQL слушает только loopback: `127.0.0.1:5432` и `[::1]:5432`.
- Сервер запускает задача Планировщика `postgres` при загрузке Windows:

```text
C:\BERGAPP\pgsql\bin\pg_ctl.exe -D ../data start
```

Она работает от пользователя `postgres`. PostgreSQL оформлен именно как задача
Планировщика, а не как обычная служба Windows.

## 3. Размещение Berg Sync

### Исполняемые файлы

Self-contained Windows x64 публикация находится в:

```text
C:\Tools\berg-sync
```

Основные файлы:

```text
berg-sync.exe
berg-sync.dll
berg-sync.runtimeconfig.json
migration-schema.json
sql\
```

В каталоге также находятся runtime .NET и сторонние библиотеки, поэтому
устанавливать отдельный .NET Runtime для запуска не требуется.

### Конфигурация и изменяемые данные

```text
C:\ProgramData\BergSync\
├── .env                 конфигурация и секреты
├── berg-sync.cmd        единая точка запуска
├── delta-packages\      неизменяемые инкрементальные пакеты
└── logs\                журналы автоматических запусков
```

`berg-sync.cmd`:

1. задаёт `BERG_SYNC_ENV_FILE` на общий `.env`;
2. добавляет необходимые PostgreSQL и служебные программы в `PATH`;
3. переходит в `C:\Tools\berg-sync`;
4. запускает `berg-sync.exe` с переданными аргументами.

Программу вручную и из Планировщика следует запускать через этот CMD, а не
напрямую через EXE.

## 4. Конфигурация

Файл конфигурации:

```text
C:\ProgramData\BergSync\.env
```

В нём определены:

```text
MDB_PATH
PG_LOCAL_CONNECTION_STRING
PG_HOSTING_CONNECTION_STRING
DELTA_PACKAGE_DIR
HOSTING_SSH_TARGET
HOSTING_TRANSFER_DIR
HOSTING_RESTORE_SCRIPT
```

Текущие несекретные параметры:

```text
MDB_PATH                 C:\Berg\DB\bergauto.mdb
локальный PostgreSQL     localhost:5432, база postgres, SSL отключён
PostgreSQL хостинга      pg4.sweb.ru:5433, SSL Mode=VerifyFull
DELTA_PACKAGE_DIR        C:\ProgramData\BergSync\delta-packages
HOSTING_SSH_TARGET       bergvl
HOSTING_TRANSFER_DIR     /home/d/debrskyru/berg-transfer
HOSTING_RESTORE_SCRIPT   /home/d/debrskyru/berg-transfer/restore-hosting.sh
```

Пароли находятся только в `.env` и не должны попадать в документацию, журналы,
Git или аргументы процессов.

## 5. Пользователи и доступ

### `Администратор`

- запускает автоматическую полную миграцию;
- имеет доступ к приложению, общему `.env`, MDB, журналам и пакетам;
- имеет собственные SSH config, private key и `known_hosts`;
- `.pgpass` отсутствует: `berg-sync` использует строки подключения из `.env`.

### `zhn`

- может запускать TUI;
- читает общий `.env`;
- читает MDB;
- имеет права изменения `logs` и `delta-packages`;
- имеет собственные SSH config, private key и `known_hosts`;
- `.pgpass` отсутствует и для `berg-sync` не требуется.

### `postgres`

- запускает локальный PostgreSQL и использовался старым процессом переноса;
- имеет `%APPDATA%\postgresql\pgpass.conf`;
- имеет отдельную SSH-конфигурацию и ключ.

Закрытые SSH-ключи не следует копировать между профилями или открывать другим
пользователям.

## 6. Ручной запуск через TUI

Ярлык называется `Berg Sync TUI.lnk` и находится только в личных профилях
разрешённых пользователей:

```text
C:\Users\Администратор\Desktop\Berg Sync TUI.lnk
C:\Users\zhn\Desktop\Berg Sync TUI.lnk
```

Общего ярлыка в `C:\Users\Public\Desktop` нет. ACL обоих файлов разрешает
полный доступ `Администратор`, чтение и запуск `zhn`, а также служебный доступ
`SYSTEM`; остальные пользователи доступа не имеют.

Он запускает:

```text
C:\ProgramData\BergSync\berg-sync.cmd --tui
```

Рабочий каталог — `C:\Tools\berg-sync`.

TUI показывает выбранный MDB и адреса баз без паролей, после чего предлагает:

- инкрементальную синхронизацию локально и на хостинг;
- полную миграцию локально и полную замену схем на хостинге.

Перед фактической операцией TUI запрашивает подтверждение. MDB всегда открывается
только для чтения.

## 7. Задачи Планировщика

Все задачи находятся в корне `Библиотеки планировщика заданий`.

### `postgres`

| Параметр     | Значение                     |
| ------------ | ---------------------------- |
| Состояние    | включена                     |
| Пользователь | `postgres`                   |
| Триггер      | при загрузке Windows         |
| Назначение   | запуск локального PostgreSQL |

### `berg-sync-full`

| Параметр                | Значение                                  |
| ----------------------- | ----------------------------------------- |
| Состояние               | **включена**                              |
| Пользователь            | `Администратор`                           |
| Тип входа               | S4U, пароль в задаче не хранится          |
| Уровень                 | Limited                                   |
| Расписание              | ежедневно в 06:00 по времени Владивостока |
| Пропущенный запуск      | выполнить при первой возможности          |
| Параллельные экземпляры | новый экземпляр игнорируется              |
| Максимальное время      | 72 часа                                   |

Команда:

```text
C:\ProgramData\BergSync\berg-sync.cmd
  --migrate
  --confirm-drop
  --publish-hosting
  --confirm-hosting-restore
```

Рабочий каталог:

```text
C:\Tools\berg-sync
```

Журнал текущего/последнего запуска:

```text
C:\ProgramData\BergSync\logs\berg-sync-full.log
```

Журнал открывается через `>`, поэтому каждый запуск заменяет предыдущий файл.
Если историю нужно сохранять, журнал необходимо архивировать до следующего
запуска или изменить схему логирования.


> Задача разрушительная по назначению: на каждом запуске она пересоздаёт
> локальные схемы `bergauto`, `bergapp`, `berg_sync`, а затем заменяет те же
> схемы на хостинге.

## 8. Что делает ежедневная полная задача

1. Загружает общий `.env`.
2. Открывает `C:\Berg\DB\bergauto.mdb` через ACE OLE DB только для чтения.
3. Удаляет и создаёт заново локальные схемы:
   `bergauto`, `bergapp`, `berg_sync`.
4. Переносит таблицы через PostgreSQL Binary COPY.
5. Создаёт индексы, выполняет `ANALYZE`, рассчитывает operations и materialized
   views.
6. Создаёт новый baseline с `delta_seq = 0`.
7. Делает directory-format dump трёх схем.
8. Создаёт manifest, TAR.ZST и SHA-256.
9. Загружает пакет на хостинг по SFTP с возможностью докачки.
10. Проверяет SHA-256 и запускает по SSH `restore-hosting.sh`.
11. Restore на хостинге выполняет `pg_restore --clean --single-transaction`.
12. Сравнивает локальную и удалённую базы по baseline, `delta_seq`, `COUNT(*)`
    и `MAX(ID)`.
13. Удаляет временные файлы только после успешной итоговой проверки.

При ошибке локальные диагностические файлы сохраняются, а удалённый restore в
одной транзакции не должен оставлять частично заменённые схемы.

## 9. Инкрементальная синхронизация

Инкрементальный запуск выполняется через TUI либо командой:

```text
C:\ProgramData\BergSync\berg-sync.cmd --incremental --sync-hosting
```

Он применяет только INSERT/UPDATE, создаёт неизменяемый пакет в
`delta-packages`, применяет его локально, затем на хостинге. Удаления и `xSaldo`
исправляются следующей полной миграцией.

После общей полной публикации нельзя чередовать:

```text
--incremental
--incremental --sync-hosting
```

Иначе последовательности локальной базы и хостинга разойдутся. Для штатной
работы с актуальным хостингом следует всегда использовать `--sync-hosting`.

Полная и инкрементальная операции не должны выполняться одновременно.

## 10. Проверка состояния

### Планировщик

```powershell
Get-ScheduledTask -TaskName 'berg-sync-full'
Get-ScheduledTaskInfo -TaskName 'berg-sync-full'
```

Успех Планировщика сам по себе недостаточен: дополнительно нужно проверить конец
`berg-sync-full.log`:

```text
Полная публикация и проверка завершены успешно.
Baseline: <baseline-id>
```

### Локальный PostgreSQL

```sql
SELECT * FROM berg_sync.state;

SELECT patch_id, target, status, attempts, last_error, applied_at
FROM berg_sync.patch_delivery
ORDER BY patch_id, target;
```

### Основные признаки исправной системы

- `bergauto.mdb` существует и читается;
- PostgreSQL слушает localhost:5432;
- задача `postgres` успешно стартовала;
- `berg-sync-full` включена и имеет следующий запуск;
- в конце журнала нет `Ошибка`/`ERROR`;
- локальный и удалённый baseline совпадают;
- локальный и удалённый `delta_seq` совпадают;
- нет старых пакетов со статусом `pending` или `failed`.

## 11. Обновление приложения

При обновлении нужно заменить self-contained публикацию в
`C:\Tools\berg-sync`, сохранив отдельно:

- `C:\ProgramData\BergSync\.env`;
- `C:\ProgramData\BergSync\delta-packages`;
- `C:\ProgramData\BergSync\logs`;
- SSH-конфигурацию пользовательских профилей.

После обновления проверить:

```text
C:\ProgramData\BergSync\berg-sync.cmd --help
```

и убедиться, что доступны `migration-schema.json`, каталог `sql`, PostgreSQL
client tools, Zstandard, OpenSSH и ACE OLE DB.

Если изменён `scripts/restore-hosting.sh`, Windows-публикации недостаточно:
production-копию на пути `HOSTING_RESTORE_SCRIPT` нужно обновить отдельно.

## 12. Важные ограничения и меры безопасности

- Не хранить секреты в этом документе, ярлыках или аргументах Планировщика.
- Не запускать полную задачу вручную без проверки выбранного MDB.
- Не запускать одновременно полную и инкрементальную синхронизацию.
- Не удалять `delta-packages` без проверки статусов доставки.
- Не изменять MDB средствами `berg-sync` или обслуживающими скриптами.
- Доступ к `.env`, SSH-ключам и журналам следует сохранять минимально
  необходимым.
