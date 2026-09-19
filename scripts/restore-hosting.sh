#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_NAME="$(basename "$0")"
TRANSFER_DIR="${BERG_TRANSFER_DIR:-$HOME/berg-transfer}"
CONFIG_FILE="${BERG_RESTORE_ENV:-$TRANSFER_DIR/restore.env}"
LOCK_FILE="$TRANSFER_DIR/restore.lock"
WORK_ROOT="$TRANSFER_DIR/work"

log() {
    printf '[%s] %s\n' "$(date '+%F %T')" "$*"
}

die() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

usage() {
    cat <<EOF
Usage: $SCRIPT_NAME [archive-name]

Restores bergauto, bergapp and berg_sync from a Zstandard-compressed TAR
archive. The archive and its <archive-name>.sha256 file must be located in:
  $TRANSFER_DIR

Default archive name:
  berg-transfer.tar.zst

PostgreSQL settings are read from process environment and optionally from:
  $CONFIG_FILE
EOF
}

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    usage
    exit 0
fi

ARCHIVE_NAME="${1:-berg-transfer.tar.zst}"
[[ "$ARCHIVE_NAME" == "$(basename "$ARCHIVE_NAME")" ]] || \
    die "Archive name must not contain a directory: $ARCHIVE_NAME"
[[ "$ARCHIVE_NAME" == *.tar.zst ]] || \
    die "Archive must have the .tar.zst extension: $ARCHIVE_NAME"

ARCHIVE="$TRANSFER_DIR/$ARCHIVE_NAME"
CHECKSUM_FILE="$ARCHIVE.sha256"
WORK_DIR="$WORK_ROOT/${ARCHIVE_NAME%.tar.zst}"
DUMP_DIR="$WORK_DIR/bergdb-dump"
FILE_LIST="$WORK_DIR/archive-files.txt"
TOC_LIST="$WORK_DIR/toc.txt"
RESTORE_MARKER="$TRANSFER_DIR/${ARCHIVE_NAME%.tar.zst}.restored"

mkdir -p "$TRANSFER_DIR"

if [[ -f "$CONFIG_FILE" ]]; then
    # restore.env is a trusted operator-owned file and may contain PGPASSWORD or
    # PGPASSFILE. It must never be included in the transfer archive.
    set -a
    # shellcheck source=/dev/null
    source "$CONFIG_FILE"
    set +a
fi

: "${PGHOST:?Set PGHOST in the environment or restore.env}"
: "${PGDATABASE:?Set PGDATABASE in the environment or restore.env}"
: "${PGUSER:?Set PGUSER in the environment or restore.env}"
PGPORT="${PGPORT:-5432}"
PGCONNECT_TIMEOUT="${PGCONNECT_TIMEOUT:-15}"
PGCLIENTENCODING="${PGCLIENTENCODING:-UTF8}"
export PGHOST PGPORT PGDATABASE PGUSER PGCONNECT_TIMEOUT PGCLIENTENCODING

find_program() {
    local configured="$1"
    local fallback="$2"

    if [[ -n "$configured" ]]; then
        [[ -x "$configured" ]] || die "Program is not executable: $configured"
        printf '%s\n' "$configured"
        return
    fi

    command -v "$fallback" 2>/dev/null || \
        die "$fallback was not found; set its absolute path in restore.env"
}

PG_RESTORE_BIN="$(find_program "${PG_RESTORE:-}" pg_restore)"
PSQL_BIN="$(find_program "${PSQL:-}" psql)"

for program in flock zstd tar sha256sum awk grep; do
    command -v "$program" >/dev/null 2>&1 || die "$program was not found"
done

exec 9>"$LOCK_FILE"
if ! flock -n 9; then
    die "Another restore process is already running"
fi

cleanup() {
    rm -rf -- "$WORK_DIR"
}
trap cleanup EXIT

[[ -f "$ARCHIVE" ]] || die "Archive not found: $ARCHIVE"
[[ -f "$CHECKSUM_FILE" ]] || die "Checksum file not found: $CHECKSUM_FILE"

read -r expected_hash expected_name extra < "$CHECKSUM_FILE" || \
    die "Cannot read checksum file: $CHECKSUM_FILE"
expected_hash="${expected_hash,,}"
expected_name="${expected_name#\*}"
expected_name="${expected_name%$'\r'}"

[[ "$expected_hash" =~ ^[[:xdigit:]]{64}$ ]] || \
    die "Invalid SHA-256 value in $CHECKSUM_FILE"
[[ "$expected_name" == "$ARCHIVE_NAME" ]] || \
    die "Checksum file refers to '$expected_name', expected '$ARCHIVE_NAME'"
[[ -z "${extra:-}" ]] || die "Unexpected data in checksum file: $CHECKSUM_FILE"

log "Calculating SHA-256 for $ARCHIVE_NAME"
actual_hash="$(sha256sum -- "$ARCHIVE" | awk '{print $1}')"
[[ "$actual_hash" == "$expected_hash" ]] || \
    die "SHA-256 mismatch for $ARCHIVE_NAME"

log "Verifying Zstandard archive"
zstd --quiet --test -- "$ARCHIVE" || die "Zstandard archive is damaged"

rm -rf -- "$WORK_DIR"
mkdir -p "$WORK_DIR"

log "Checking archive contents"
if ! zstd --quiet --decompress --stdout -- "$ARCHIVE" | tar -tf - > "$FILE_LIST"; then
    die "Cannot list archive contents"
fi

while IFS= read -r entry; do
    case "$entry" in
        bergdb-dump|bergdb-dump/*|manifest.json) ;;
        *) die "Unexpected path in archive: $entry" ;;
    esac
done < "$FILE_LIST"

log "Extracting archive"
if ! zstd --quiet --decompress --stdout -- "$ARCHIVE" | tar -xf - -C "$WORK_DIR"; then
    die "Cannot extract archive"
fi

[[ -f "$DUMP_DIR/toc.dat" ]] || \
    die "Directory-format PostgreSQL dump is missing: $DUMP_DIR/toc.dat"

"$PG_RESTORE_BIN" --list "$DUMP_DIR" > "$TOC_LIST" || \
    die "Cannot read PostgreSQL dump TOC"

grep -q 'bergauto' "$TOC_LIST" || die "bergauto is absent from the dump"
grep -q 'bergapp' "$TOC_LIST" || die "bergapp is absent from the dump"
grep -q 'berg_sync' "$TOC_LIST" || die "berg_sync is absent from the dump"

log "Checking PostgreSQL target"
target_info="$(
    "$PSQL_BIN" -X -qAt -v ON_ERROR_STOP=1 -F '|' -c \
        "SELECT current_database(), current_user, pg_is_in_recovery();"
)"
IFS='|' read -r actual_database actual_user in_recovery <<< "$target_info"

[[ "$actual_database" == "$PGDATABASE" ]] || \
    die "Connected to unexpected database: $actual_database"
[[ "$actual_user" == "$PGUSER" ]] || \
    die "Connected as unexpected user: $actual_user"
[[ "$in_recovery" == "f" ]] || die "Target PostgreSQL is in recovery mode"

log "Ensuring required PostgreSQL extensions"
"$PSQL_BIN" -X -q -v ON_ERROR_STOP=1 -c \
    "CREATE EXTENSION IF NOT EXISTS hstore;"

log "Restoring into $PGHOST:$PGPORT/$PGDATABASE as $PGUSER"
"$PG_RESTORE_BIN" \
    --clean \
    --if-exists \
    --single-transaction \
    --exit-on-error \
    --no-owner \
    --no-acl \
    --verbose \
    --dbname="$PGDATABASE" \
    "$DUMP_DIR"

log "Running post-restore checks"
post_restore="$(
    "$PSQL_BIN" -X -qAt -v ON_ERROR_STOP=1 -F '|' -c "
        SELECT
            EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'hstore'),
            to_regnamespace('bergauto') IS NOT NULL,
            to_regnamespace('bergapp') IS NOT NULL,
            to_regnamespace('berg_sync') IS NOT NULL,
            to_regclass('bergauto.\"Applications\"') IS NOT NULL,
            to_regclass('bergauto.\"XInvoices\"') IS NOT NULL,
            to_regclass('bergapp.operations') IS NOT NULL,
            to_regclass('berg_sync.state') IS NOT NULL,
            (SELECT count(*) FROM bergauto.\"Applications\"),
            (SELECT count(*) FROM bergauto.\"XInvoices\"),
            (SELECT count(*) FROM bergapp.operations),
            (SELECT baseline_id FROM berg_sync.state WHERE id = 1),
            (SELECT delta_seq FROM berg_sync.state WHERE id = 1);
    "
)"

IFS='|' read -r has_hstore has_bergauto has_bergapp has_sync has_apps has_invoices \
    has_operations has_state applications_count invoices_count operations_count \
    baseline_id delta_seq <<< "$post_restore"

for check in "$has_hstore" "$has_bergauto" "$has_bergapp" "$has_sync" "$has_apps" \
             "$has_invoices" "$has_operations" "$has_state"; do
    [[ "$check" == "t" ]] || die "Post-restore object check failed"
done

[[ -n "$baseline_id" ]] || die "berg_sync.state does not contain baseline_id"
[[ "$delta_seq" == "0" ]] || die "Full restore must set delta_seq to 0"

{
    printf 'restored_at=%s\n' "$(date --iso-8601=seconds)"
    printf 'archive=%s\n' "$ARCHIVE_NAME"
    printf 'sha256=%s\n' "$actual_hash"
    printf 'database=%s\n' "$PGDATABASE"
    printf 'baseline_id=%s\n' "$baseline_id"
    printf 'delta_seq=%s\n' "$delta_seq"
    printf 'applications=%s\n' "$applications_count"
    printf 'invoices=%s\n' "$invoices_count"
    printf 'operations=%s\n' "$operations_count"
} > "$RESTORE_MARKER.tmp"
mv -f -- "$RESTORE_MARKER.tmp" "$RESTORE_MARKER"

rm -f -- "$ARCHIVE" "$CHECKSUM_FILE"

log "Restore completed successfully"
log "Baseline: $baseline_id; Applications: $applications_count; XInvoices: $invoices_count; Operations: $operations_count"
