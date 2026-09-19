#!/usr/bin/env bash
set -Eeuo pipefail

TRANSFER_DIR="$HOME/berg-transfer"
ARCHIVE="$TRANSFER_DIR/berg-transfer.tar.zst"
WORK_DIR="$TRANSFER_DIR/work"
LOCK_FILE="$TRANSFER_DIR/restore.lock"

PG_RESTORE="/usr/lib64/postgresql-17/bin/pg_restore"
PSQL="/usr/lib64/postgresql-17/bin/psql"

REMOTE_HOST="pg4.sweb.ru"
REMOTE_PORT="5433"
REMOTE_USER="debrskyru"
REMOTE_DB="debrskyru"

export PGCLIENTENCODING=UTF8
export PGCONNECT_TIMEOUT=15

log() {
    printf '[%s] %s\n' "$(date '+%F %T')" "$*"
}

die() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

command -v flock >/dev/null 2>&1 || die "flock was not found"
command -v zstd >/dev/null 2>&1 || die "zstd was not found"
command -v tar >/dev/null 2>&1 || die "tar was not found"

[[ -x "$PG_RESTORE" ]] || \
    die "pg_restore not found or not executable: $PG_RESTORE"

[[ -x "$PSQL" ]] || \
    die "psql not found or not executable: $PSQL"

mkdir -p "$TRANSFER_DIR"

exec 9>"$LOCK_FILE"

if ! flock -n 9; then
    die "Another restore process is already running"
fi

cleanup_work_dir() {
    rm -rf -- "$WORK_DIR"
}

trap cleanup_work_dir EXIT

[[ -f "$ARCHIVE" ]] || die "Archive not found: $ARCHIVE"

log "Verifying Zstandard archive"

if ! zstd --quiet --test -- "$ARCHIVE"; then
    die "Zstandard archive is damaged: $ARCHIVE"
fi

rm -rf -- "$WORK_DIR"
mkdir -p "$WORK_DIR"

log "Extracting archive"

if ! zstd --quiet --decompress --stdout -- "$ARCHIVE" | \
    tar -xf - -C "$WORK_DIR"; then
    die "Cannot extract archive: $ARCHIVE"
fi

DUMP_DIR="$WORK_DIR/bergdb-dump"
PERSISTENT_SQL="$WORK_DIR/berg_persistent.sql"

[[ -f "$DUMP_DIR/toc.dat" ]] || \
    die "Invalid directory-format dump: $DUMP_DIR"

[[ -f "$PERSISTENT_SQL" ]] || \
    die "File not found: $PERSISTENT_SQL"

log "Restoring bergapp and bergauto"

"$PG_RESTORE" \
    -h "$REMOTE_HOST" \
    -p "$REMOTE_PORT" \
    -U "$REMOTE_USER" \
    -d "$REMOTE_DB" \
    --clean \
    --if-exists \
    --single-transaction \
    --no-owner \
    --no-acl \
    --verbose \
    "$DUMP_DIR"

log "Checking berg_persistent schema"

PERSISTENT_EXISTS="$(
    "$PSQL" \
        -X \
        -h "$REMOTE_HOST" \
        -p "$REMOTE_PORT" \
        -U "$REMOTE_USER" \
        -d "$REMOTE_DB" \
        -qAt \
        -v ON_ERROR_STOP=1 \
        -c "SELECT 1 FROM pg_namespace WHERE nspname = 'berg_persistent';"
)"

if [[ "$PERSISTENT_EXISTS" != "1" ]]; then
    log "berg_persistent does not exist; creating it"

    "$PSQL" \
        -X \
        -h "$REMOTE_HOST" \
        -p "$REMOTE_PORT" \
        -U "$REMOTE_USER" \
        -d "$REMOTE_DB" \
        -v ON_ERROR_STOP=1 \
        -f "$PERSISTENT_SQL"
else
    log "berg_persistent already exists; its structure will not be changed"
fi

log "Archiving invoices"

"$PSQL" \
    -X \
    -h "$REMOTE_HOST" \
    -p "$REMOTE_PORT" \
    -U "$REMOTE_USER" \
    -d "$REMOTE_DB" \
    -v ON_ERROR_STOP=1 \
    -c "CALL berg_persistent.archive_invoices();"

rm -f -- "$ARCHIVE"

log "Data restore completed successfully"
