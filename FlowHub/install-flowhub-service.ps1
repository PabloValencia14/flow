param(
    [string]$StageRoot = 'C:\FlowHub\stage',
    [string]$AppRoot = 'C:\FlowHub',
    [switch]$ConfigureServe
)

$ErrorActionPreference = 'Stop'
$serviceName = 'FlowHub'
$exe = Join-Path $AppRoot 'FlowHub.exe'
$config = Join-Path $AppRoot 'appsettings.json'
$backupRoot = Join-Path $AppRoot 'backups'

if (-not (Test-Path -LiteralPath (Join-Path $StageRoot 'FlowHub.exe'))) {
    throw "No se encuentra FlowHub.exe en $StageRoot"
}

New-Item -ItemType Directory -Force -Path $AppRoot, (Join-Path $AppRoot 'data'), $backupRoot | Out-Null
if (Test-Path -LiteralPath $exe) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backup = Join-Path $backupRoot $stamp
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    Copy-Item -LiteralPath $exe -Destination $backup -Force
    if (Test-Path -LiteralPath $config) { Copy-Item -LiteralPath $config -Destination $backup -Force }
}
$token = [Environment]::GetEnvironmentVariable('FLOW_HUB_APP_TOKEN', 'Machine')
if ([string]::IsNullOrWhiteSpace($token)) {
    $bytes = [byte[]]::new(32)
    $rng = New-Object Security.Cryptography.RNGCryptoServiceProvider
    $rng.GetBytes($bytes)
    $rng.Dispose()
    [Environment]::SetEnvironmentVariable('FLOW_HUB_APP_TOKEN', [Convert]::ToBase64String($bytes), 'Machine')
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $existing) {
    New-Service -Name $serviceName -BinaryPathName ('"{0}"' -f $exe) -DisplayName 'FlowHub' -Description 'Local-first Flow synchronization hub' -StartupType Automatic | Out-Null
}
else {
    if ($existing.Status -eq 'Running') {
        Stop-Service -Name $serviceName -Force
        $deadline = (Get-Date).AddSeconds(20)
        do {
            Start-Sleep -Milliseconds 250
            $existing = Get-Service -Name $serviceName
        } while ($existing.Status -ne 'Stopped' -and (Get-Date) -lt $deadline)
        if ($existing.Status -ne 'Stopped') { throw 'FlowHub no se detuvo completamente antes de actualizarse' }
    }
}

# A self-contained .NET service can keep runtime DLL handles briefly after
# SCM reports Stopped. Release only the process belonging to this executable
# before replacing the published files.
Get-Process -Name 'FlowHub' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Copy-Item -Path (Join-Path $StageRoot '*') -Destination $AppRoot -Recurse -Force

Start-Service -Name $serviceName
Start-Sleep -Seconds 2
$health = Invoke-RestMethod 'http://127.0.0.1:8790/healthz'
if ($health.status -ne 'ok') { throw 'FlowHub no devolvió healthz=ok' }

if ($ConfigureServe) {
    $tailscale = @('C:\Program Files\Tailscale\tailscale.exe', 'C:\Program Files (x86)\Tailscale\tailscale.exe') |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $tailscale) { throw 'No se encontró tailscale.exe' }
    & $tailscale serve --https=443 http://127.0.0.1:8790
}

[pscustomobject]@{ service = $serviceName; health = $health.status; serveConfigured = [bool]$ConfigureServe } | ConvertTo-Json
