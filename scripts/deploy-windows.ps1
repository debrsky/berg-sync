# Deploy a framework-dependent win-x64 ZIP on the Windows machine running berg-sync.
# Run from a separate terminal, not from the instance being replaced.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Archive,
    [string]$Destination = 'C:\Tools\berg_sync\app',
    [string]$Sha256
)

$ErrorActionPreference = 'Stop'
$archivePath = (Resolve-Path -LiteralPath $Archive).Path
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
if ([System.IO.Path]::GetFileName($destinationPath.TrimEnd('\', '/')) -ine 'app') {
    throw 'Каталог назначения должен быть app: нельзя разворачивать архив в корень с секретами и рабочими данными.'
}
$parent = [System.IO.Path]::GetDirectoryName($destinationPath.TrimEnd('\', '/'))
if (-not $parent) { throw 'Укажите каталог назначения, а не корень диска.' }
if ($destinationPath.TrimEnd('\', '/') -eq $archivePath.TrimEnd('\', '/')) { throw 'Архив и назначение совпадают.' }

if ($Sha256) {
    $actual = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if ($actual -ine $Sha256) { throw "SHA-256 архива не совпадает: $actual" }
}

# Do not update a running executable (including when invoked from another terminal).
$running = @(Get-Process -Name 'berg-sync' -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and $_.Path.StartsWith($destinationPath.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase) } catch { $false }
})
if ($running.Count -gt 0) { throw 'berg-sync запущен из каталога назначения. Закройте приложение перед деплоем.' }

New-Item -ItemType Directory -Path $parent -Force | Out-Null
$token = (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$staging = Join-Path $parent ".berg-sync-staging-$token"
$backup = Join-Path $parent "berg-sync-backup-$token"
$installed = [System.Collections.Generic.List[string]]::new()
$replaced = [System.Collections.Generic.List[string]]::new()
$deploymentStarted = $false
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $staging)
    if (-not (Test-Path -LiteralPath (Join-Path $staging 'berg-sync.exe') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $staging 'migration-schema.json') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $staging 'sql') -PathType Container)) {
        throw 'Архив не содержит необходимые файлы berg-sync.exe, migration-schema.json и sql/.'
    }
    $files = @(Get-ChildItem -LiteralPath $staging -File -Recurse)
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($staging.TrimEnd('\', '/').Length + 1)
        if ($relative -eq '.env' -or $relative -match '^(logs|delta-packages|ssh|state)([/\\]|$)' -or
            $relative -match '(^|[/\\])\.env$' -or $relative -match '\.(key|pem|pfx)$') {
            throw "Архив содержит конфигурацию или пользовательские данные: $relative"
        }
    }

    # Make a backup of every file to be replaced, without touching .env, logs or packages.
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($staging.TrimEnd('\', '/').Length + 1)
        $existing = Join-Path $destinationPath $relative
        if (Test-Path -LiteralPath $existing -PathType Leaf) {
            $saved = Join-Path $backup $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $saved) -Force | Out-Null
            Copy-Item -LiteralPath $existing -Destination $saved
            $replaced.Add($relative)
        }
    }
    $deploymentStarted = $true
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($staging.TrimEnd('\', '/').Length + 1)
        $target = Join-Path $destinationPath $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        $installed.Add($relative)
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
    Write-Host "Развёрнуто: $destinationPath"
    if (Test-Path -LiteralPath $backup) { Write-Host "Резервная копия заменённых файлов: $backup" }
    Write-Host 'Конфигурация и рабочие каталоги в родительском каталоге app/ не изменялись.'
}
catch {
    $failure = $_
    if ($deploymentStarted) {
        foreach ($relative in $installed) {
            $target = Join-Path $destinationPath $relative
            if (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target -Force }
        }
        foreach ($relative in $replaced) {
            $saved = Join-Path $backup $relative
            $target = Join-Path $destinationPath $relative
            Copy-Item -LiteralPath $saved -Destination $target -Force
        }
        Write-Warning 'Восстановлены заменённые файлы из резервной копии.'
    }
    throw $failure
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
