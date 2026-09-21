# Восстановление полной выгрузки на хостинге

`scripts/restore-hosting.sh` заменяет серверную часть старого процесса из
`docs/restore-berg.sh`. После восстановления основных схем скрипт вызывает
существующую процедуру `berg_persistent.archive_invoices()`, но не создаёт и не
обновляет объекты схемы `berg_persistent`.

## Развёртывание

```bash
mkdir -p ~/berg-transfer
cp restore-hosting.sh ~/berg-transfer/
cp restore-hosting.env.sample ~/berg-transfer/restore.env
chmod 700 ~/berg-transfer/restore-hosting.sh
chmod 600 ~/berg-transfer/restore.env
```

Заполните в `restore.env` параметры PostgreSQL. Пароль предпочтительно хранить в
`~/.pgpass`, а не в `restore.env`.

## Формат входных файлов

В `~/berg-transfer` должны находиться:

```text
berg-transfer-<baseline>.tar.zst
berg-transfer-<baseline>.tar.zst.sha256
```

Архив должен содержать только:

```text
bergdb-dump/
manifest.json        # необязателен для текущей версии restore
```

`bergdb-dump` — directory-format dump PostgreSQL, содержащий схемы:

```text
bergauto
bergapp
berg_sync
```

Перед созданием dump полная миграция должна создать новое состояние
`berg_sync.state` с `delta_seq = 0`.

Checksum-файл создаётся рядом с архивом:

```bash
sha256sum berg-transfer-<baseline>.tar.zst \
  > berg-transfer-<baseline>.tar.zst.sha256
```

Имя файла внутри checksum должно совпадать с именем переданного архива.

## Запуск

```bash
~/berg-transfer/restore-hosting.sh \
  berg-transfer-2026-09-19-full.tar.zst
```

Если имя не указано, используется `berg-transfer.tar.zst`.

## Последовательность

Скрипт:

1. получает неблокирующий `flock`;
2. проверяет SHA-256;
3. проверяет Zstandard;
4. запрещает неожиданные пути внутри TAR;
5. проверяет наличие трёх требуемых схем в dump;
6. проверяет целевую базу, пользователя и `pg_is_in_recovery()`;
7. устанавливает `hstore`, если расширение отсутствует;
8. выполняет `pg_restore --clean --single-transaction`;
9. проверяет ключевые таблицы и `berg_sync.state`;
10. требует `delta_seq = 0`;
11. вызывает `berg_persistent.archive_invoices()`;
12. записывает файл `<baseline>.restored` с результатами;
13. удаляет архив и checksum только после полного успеха.

Схема `berg_persistent` и процедура `archive_invoices()` должны существовать до
запуска восстановления. Ошибка процедуры завершает публикацию ошибкой.

При ошибке транзакция `pg_restore` откатывается, входной архив сохраняется, а
временный распакованный каталог удаляется.

## Загрузка с докачкой

Архив следует загружать под временным именем:

```text
berg-transfer-<baseline>.tar.zst.uploading
```

Для продолжения загрузки OpenSSH SFTP поддерживает `reput`. После загрузки нужно
сравнить удалённый SHA-256, затем переименовать файл и только после этого
запускать restore-скрипт.
