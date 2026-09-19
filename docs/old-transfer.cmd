@echo off
setlocal EnableExtensions
chcp 65001 >nul

REM ============================================================
REM Local settings
REM ============================================================

set "APP_DIR=C:\BERGAPP\berg-app-master"
set "WORK_DIR=%~dp0"

set "DUMP_DIR=%WORK_DIR%bergdb-dump"
set "PERSISTENT_SQL=%WORK_DIR%berg_persistent.sql"
set "TRANSFER_TAR=%WORK_DIR%berg-transfer.tar"
set "TRANSFER_ARCHIVE=%WORK_DIR%berg-transfer.tar.zst"

set "LOCAL_USER=postgres"
set "LOCAL_DB=bergdb"

set "SSH_TARGET=bergvl"
set "REMOTE_DIR=berg-transfer"
set "REMOTE_SCRIPT=%REMOTE_DIR%/restore-berg.sh"

set "FTP_HOST=bergvl.ru"
set "FTP_USER=debrskyru_bergpg"

REM Set BERG_FTP_PASSWORD in the environment before starting this script.
REM Never store the FTP password in this file.
set "FTP_MAX_ATTEMPTS=20"
set "FTP_RETRY_DELAY=3"
set "FTP_ARCHIVE=berg-transfer.tar.zst"
set "FTP_TEMP_FILE=berg-transfer-%RANDOM%-%RANDOM%.tar.zst.uploading"

REM Maximum Zstandard compression. Level 22 can be slow and memory-intensive.
set "ZSTD_LEVEL=17"

set "PGCLIENTENCODING=UTF8"
set "PGCONNECT_TIMEOUT=15"

echo [%date% %time%] Starting data transfer

REM ============================================================
REM Check configuration and required programs
REM ============================================================

if not defined BERG_FTP_PASSWORD (
    echo ERROR: Environment variable BERG_FTP_PASSWORD is not set
    echo Example: set "BERG_FTP_PASSWORD=your_password"
    goto :failed
)

where node.exe >nul 2>&1
if errorlevel 1 (
    echo ERROR: node.exe was not found
    goto :failed
)

where pg_dump.exe >nul 2>&1
if errorlevel 1 (
    echo ERROR: pg_dump.exe was not found
    goto :failed
)

where ssh.exe >nul 2>&1
if errorlevel 1 (
    echo ERROR: ssh.exe was not found
    goto :failed
)

where curl.exe >nul 2>&1
if errorlevel 1 (
    echo ERROR: curl.exe was not found
    goto :failed
)

where tar.exe >nul 2>&1
if errorlevel 1 (
    echo ERROR: tar.exe was not found
    goto :failed
)

where zstd.exe >nul 2>&1
if errorlevel 1 (
    echo ERROR: zstd.exe was not found
    goto :failed
)

REM PostgreSQL 17 supports --compress=none. Fail early for an older client.
pg_dump.exe --help | findstr /C:"--compress" >nul
if errorlevel 1 (
    echo ERROR: This pg_dump version does not support --compress=none
    goto :failed
)

REM ============================================================
REM Check FTP and SSH before creating the dump
REM ============================================================

echo [%date% %time%] Checking FTP connection to %FTP_HOST%

curl.exe ^
  --fail ^
  --silent ^
  --show-error ^
  --ipv4 ^
  --ftp-pasv ^
  --disable-epsv ^
  --connect-timeout 15 ^
  --max-time 30 ^
  --user "%FTP_USER%:%BERG_FTP_PASSWORD%" ^
  --list-only ^
  "ftp://%FTP_HOST%/" ^
  >nul

if errorlevel 1 (
    echo ERROR: FTP connection check failed
    echo ERROR: Check FTP server, username, password and network connection
    goto :failed
)

echo [%date% %time%] Checking SSH connection and remote requirements

ssh.exe ^
  -o BatchMode=yes ^
  -o ConnectTimeout=15 ^
  %SSH_TARGET% ^
  "test -x ~/%REMOTE_SCRIPT% ^&^& command -v zstd ^>/dev/null ^&^& command -v tar ^>/dev/null"

if errorlevel 1 (
    echo ERROR: Cannot connect to %SSH_TARGET%
    echo ERROR: Or the remote script, zstd or tar is unavailable
    goto :failed
)

REM ============================================================
REM Run local migrations
REM ============================================================

echo [%date% %time%] Running local migrations

pushd "%APP_DIR%"
if errorlevel 1 (
    echo ERROR: Cannot open directory "%APP_DIR%"
    goto :failed
)

node.exe --env-file=.env migrate.js
if errorlevel 1 (
    popd
    echo ERROR: migrate.js failed
    goto :failed
)

popd

REM ============================================================
REM Remove files left by an earlier local run
REM ============================================================

if exist "%DUMP_DIR%\" rd /s /q "%DUMP_DIR%"
if exist "%DUMP_DIR%\" (
    echo ERROR: Cannot remove old dump directory "%DUMP_DIR%"
    goto :failed
)

if exist "%PERSISTENT_SQL%" del /q "%PERSISTENT_SQL%"
if exist "%PERSISTENT_SQL%" (
    echo ERROR: Cannot remove old file "%PERSISTENT_SQL%"
    goto :failed
)

if exist "%TRANSFER_TAR%" del /q "%TRANSFER_TAR%"
if exist "%TRANSFER_TAR%" (
    echo ERROR: Cannot remove old archive "%TRANSFER_TAR%"
    goto :failed
)

if exist "%TRANSFER_ARCHIVE%" del /q "%TRANSFER_ARCHIVE%"
if exist "%TRANSFER_ARCHIVE%" (
    echo ERROR: Cannot remove old archive "%TRANSFER_ARCHIVE%"
    goto :failed
)

REM ============================================================
REM Dump bergapp and bergauto without internal compression
REM ============================================================

echo [%date% %time%] Creating uncompressed directory-format dump

pg_dump.exe ^
  -U "%LOCAL_USER%" ^
  -d "%LOCAL_DB%" ^
  --format=directory ^
  --compress=none ^
  --schema=bergauto ^
  --schema=bergapp ^
  --no-owner ^
  --no-acl ^
  --jobs=4 ^
  --verbose ^
  --file="%DUMP_DIR%"

if errorlevel 1 (
    echo ERROR: Main pg_dump failed
    goto :failed
)

REM ============================================================
REM Dump only the structure of berg_persistent
REM ============================================================

echo [%date% %time%] Creating berg_persistent schema dump

pg_dump.exe ^
  -U "%LOCAL_USER%" ^
  -d "%LOCAL_DB%" ^
  --schema-only ^
  --schema=berg_persistent ^
  --no-owner ^
  --no-acl ^
  --format=plain ^
  --verbose ^
  --file="%PERSISTENT_SQL%"

if errorlevel 1 (
    echo ERROR: berg_persistent pg_dump failed
    goto :failed
)

REM ============================================================
REM Create TAR and compress it with maximum Zstandard compression
REM ============================================================

echo [%date% %time%] Creating uncompressed TAR archive

pushd "%WORK_DIR%"
if errorlevel 1 (
    echo ERROR: Cannot open directory "%WORK_DIR%"
    goto :failed
)

tar.exe -cf "%TRANSFER_TAR%" bergdb-dump berg_persistent.sql
if errorlevel 1 (
    popd
    echo ERROR: Cannot create TAR archive
    goto :failed
)

popd

echo [%date% %time%] Compressing archive with Zstandard level %ZSTD_LEVEL%

zstd.exe ^
  --ultra ^
  -%ZSTD_LEVEL% ^
  -T0 ^
  --force ^
  "%TRANSFER_TAR%" ^
  -o "%TRANSFER_ARCHIVE%"

if errorlevel 1 (
    echo ERROR: Zstandard compression failed
    goto :failed
)

echo [%date% %time%] Verifying local Zstandard archive

zstd.exe --test "%TRANSFER_ARCHIVE%"
if errorlevel 1 (
    echo ERROR: Zstandard archive verification failed
    goto :failed
)

del /q "%TRANSFER_TAR%"
if exist "%TRANSFER_TAR%" (
    echo ERROR: Cannot remove temporary TAR archive
    goto :failed
)

REM ============================================================
REM Upload via FTP, resuming the same temporary remote file
REM ============================================================

echo [%date% %time%] Uploading archive to %FTP_HOST% via FTP

set /a FTP_ATTEMPT=0

:ftp_upload_retry
set /a FTP_ATTEMPT+=1

echo [%date% %time%] FTP upload attempt %FTP_ATTEMPT% of %FTP_MAX_ATTEMPTS%

curl.exe ^
  --fail ^
  --show-error ^
  --ipv4 ^
  --ftp-pasv ^
  --disable-epsv ^
  --connect-timeout 20 ^
  --speed-limit 1024 ^
  --speed-time 60 ^
  --continue-at - ^
  --limit-rate 100M ^
  --user "%FTP_USER%:%BERG_FTP_PASSWORD%" ^
  --upload-file "%TRANSFER_ARCHIVE%" ^
  "ftp://%FTP_HOST%/%FTP_TEMP_FILE%"

if not errorlevel 1 goto :ftp_upload_completed

if %FTP_ATTEMPT% GEQ %FTP_MAX_ATTEMPTS% goto :ftp_upload_failed

echo [%date% %time%] FTP connection failed; retrying in %FTP_RETRY_DELAY% seconds
timeout /t %FTP_RETRY_DELAY% /nobreak >nul
goto :ftp_upload_retry

:ftp_upload_failed
echo ERROR: FTP upload failed after %FTP_MAX_ATTEMPTS% attempts
echo Partial remote file: %FTP_TEMP_FILE%
goto :failed

:ftp_upload_completed
echo [%date% %time%] FTP upload completed

REM ============================================================
REM Atomically activate the uploaded file on the FTP server
REM ============================================================

echo [%date% %time%] Activating uploaded archive

curl.exe ^
  --fail ^
  --silent ^
  --show-error ^
  --ipv4 ^
  --ftp-pasv ^
  --disable-epsv ^
  --connect-timeout 20 ^
  --user "%FTP_USER%:%BERG_FTP_PASSWORD%" ^
  --quote "*DELE %FTP_ARCHIVE%" ^
  --quote "RNFR %FTP_TEMP_FILE%" ^
  --quote "RNTO %FTP_ARCHIVE%" ^
  --list-only ^
  "ftp://%FTP_HOST%/" ^
  >nul

if errorlevel 1 (
    echo ERROR: Archive was uploaded, but could not be renamed
    echo Temporary remote file: %FTP_TEMP_FILE%
    goto :failed
)

echo [%date% %time%] Archive activated successfully

REM ============================================================
REM Run restore on bergvl
REM ============================================================

echo [%date% %time%] Starting remote restore

ssh.exe ^
  -o BatchMode=yes ^
  -o ConnectTimeout=15 ^
  %SSH_TARGET% ^
  "~/%REMOTE_SCRIPT%"

if errorlevel 1 (
    echo ERROR: Remote restore failed
    echo Local dump files were preserved for diagnostics
    goto :failed
)

REM ============================================================
REM Local cleanup after complete success
REM ============================================================

rd /s /q "%DUMP_DIR%"
if exist "%DUMP_DIR%\" echo WARNING: Cannot remove local dump directory

del /q "%PERSISTENT_SQL%" 2>nul
del /q "%TRANSFER_TAR%" 2>nul
del /q "%TRANSFER_ARCHIVE%" 2>nul

echo [%date% %time%] Data transfer completed successfully
exit /b 0

:failed
echo [%date% %time%] Data transfer failed
echo Local dump and archive files were preserved for diagnostics
exit /b 1
