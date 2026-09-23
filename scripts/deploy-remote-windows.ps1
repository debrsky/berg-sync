# Run on the workstation. Build a fresh ZIP and deploy it to a Windows target via WinRM.
# Uses the endpoint from the winrm-mcp inventory and the berg-winrm credential from Windows Credential Manager.
[CmdletBinding()]
param(
    [string]$SavedHost = 'bergvl',
    [string]$InventoryPath = (Join-Path $env:APPDATA 'winrm-mcp\inventory.json'),
    [string]$Destination = 'C:\Tools\berg-sync',
    [string]$Archive,
    [string]$Sha256,
    [switch]$BuildOnly,
    [pscredential]$Credential
)

$ErrorActionPreference = 'Stop'
$inventory = Get-Content -LiteralPath $InventoryPath -Raw | ConvertFrom-Json
$hostSettings = $inventory.hosts.PSObject.Properties[$SavedHost].Value
if (-not $hostSettings) { throw "Хост '$SavedHost' отсутствует в inventory: $InventoryPath" }
if ($hostSettings.auth -ne 'ntlm') { throw 'Скрипт поддерживает только NTLM из winrm-mcp inventory.' }
$scheme = if ($hostSettings.ssl) { 'https' } else { 'http' }
$uri = '{0}://{1}:{2}/{3}' -f $scheme, $hostSettings.host, $hostSettings.port, $hostSettings.path
$installer = Join-Path $PSScriptRoot 'deploy-windows.ps1'
if (-not $BuildOnly -and -not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw "Не найден скрипт установки: $installer"
}
if ($BuildOnly -and $Archive) { throw 'Параметры -BuildOnly и -Archive несовместимы.' }
if ($Sha256 -and -not $Archive) { throw 'Параметр -Sha256 используется только вместе с -Archive.' }

if ($Archive) {
    $archivePath = (Resolve-Path -LiteralPath $Archive).Path
} else {
    $project = Join-Path (Split-Path -Parent $PSScriptRoot) 'berg-sync.csproj'
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw "Не найден проект: $project"
    }
    $publishDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('berg-sync-publish-' + [guid]::NewGuid().ToString('N'))
    $artifactsDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts'
    New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null
    $archivePath = Join-Path $artifactsDirectory (
        'berg-sync-win-x64-framework-dependent-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.zip')
    try {
        Write-Host 'Сборка свежего win-x64 артефакта...'
        & dotnet publish $project -c Release -r win-x64 --self-contained false -o $publishDirectory
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }
        foreach ($required in @('berg-sync.exe', 'berg-sync.dll', 'migration-schema.json', 'sql')) {
            if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $required))) {
                throw "В публикации отсутствует $required"
            }
        }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($publishDirectory, $archivePath)
        Write-Host "Артефакт собран: $archivePath"
    } catch {
        if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
        throw
    } finally {
        if (Test-Path -LiteralPath $publishDirectory) {
            Remove-Item -LiteralPath $publishDirectory -Recurse -Force
        }
    }
}
$actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
if ($Sha256 -and $Sha256 -ine $actualHash) {
    throw "SHA-256 локального архива не совпадает: $actualHash"
}
Write-Host "SHA-256: $actualHash"
if ($BuildOnly) { return }

if (-not $Credential) {
    # winrm-mcp inventory contains no password. Read the local user's Generic
    # Windows Credential Manager entry without printing or passing the password.
    if (-not ('BergCredentialManager' -as [type])) { Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class BergCredentialManager {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential {
        public uint Flags, Type;
        public IntPtr TargetName, Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes, TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
    public static System.Management.Automation.PSCredential Read(string target) {
        IntPtr pointer;
        if (!CredRead(target, 1, 0, out pointer)) return null; // Generic credential
        try {
            var entry = (NativeCredential)Marshal.PtrToStructure(pointer, typeof(NativeCredential));
            var user = Marshal.PtrToStringUni(entry.UserName);
            var password = Marshal.PtrToStringUni(entry.CredentialBlob, (int)entry.CredentialBlobSize / 2);
            if (String.IsNullOrEmpty(user) || password == null) return null;
            var secure = new System.Security.SecureString();
            foreach (char c in password) secure.AppendChar(c);
            secure.MakeReadOnly();
            return new System.Management.Automation.PSCredential(user, secure);
        } finally { CredFree(pointer); }
    }
}
'@ }
    $Credential = [BergCredentialManager]::Read('berg-winrm')
    if (-not $Credential) { throw 'Не найдена учётная запись berg-winrm в Windows Credential Manager.' }
}
if ($Credential.UserName -ne $hostSettings.username) {
    throw 'Пользователь сохранённой учётной записи не совпадает с winrm-mcp inventory.'
}

$session = $null
$remoteDirectory = $null
try {
    $session = New-PSSession -ConnectionUri $uri -Authentication Negotiate `
        -Credential $Credential -ErrorAction Stop
    $remoteDirectory = Invoke-Command -Session $session -ScriptBlock {
        $directory = Join-Path ([System.IO.Path]::GetTempPath()) (
            'berg-sync-deploy-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $directory -ErrorAction Stop | Out-Null
        $directory
    } -ErrorAction Stop
    $remoteArchive = Join-Path $remoteDirectory 'berg-sync.zip'
    $remoteInstaller = Join-Path $remoteDirectory 'deploy-windows.ps1'
    Write-Host "Передача артефакта на $SavedHost ($($hostSettings.host)) ..."
    Copy-Item -LiteralPath $archivePath -Destination $remoteArchive -ToSession $session -ErrorAction Stop
    Copy-Item -LiteralPath $installer -Destination $remoteInstaller -ToSession $session -ErrorAction Stop

    Invoke-Command -Session $session -ArgumentList $remoteArchive, $remoteInstaller, $Destination, $actualHash -ScriptBlock {
        param($archive, $installerPath, $target, $expectedHash)
        & $installerPath -Archive $archive -Destination $target -Sha256 $expectedHash
    } -ErrorAction Stop
}
finally {
    if ($session) {
        if ($remoteDirectory) {
            try {
                Invoke-Command -Session $session -ArgumentList $remoteDirectory -ScriptBlock {
                    param($directory)
                    Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction Stop
                } -ErrorAction Stop
            } catch { Write-Warning 'Не удалось очистить временные файлы на целевой машине.' }
        }
        Remove-PSSession $session
    }
}
