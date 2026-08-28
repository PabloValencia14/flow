[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Flow'),
    [switch]$EnableTailscale,
    [switch]$SkipGroqKey,
    [switch]$NoAutoStart
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installerRoot = $PSScriptRoot
. (Join-Path $installerRoot 'CredentialTools.ps1')
$script:flowInstallerTemporaryRoot = $null

function Resolve-TailscaleExecutable {
    @(
        (Join-Path ${env:ProgramFiles} 'Tailscale\tailscale.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Tailscale\tailscale.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

function Get-PayloadDirectory {
    # Prefer the reproducible release archive. A locally ignored payload can
    # be left over from an older build and must never silently win here.
    $archive = Join-Path $installerRoot 'release\Flow-Windows-Installer.zip'
    if (Test-Path -LiteralPath $archive) {
        $archiveRoot = Join-Path ([IO.Path]::GetTempPath()) ('FlowInstaller-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $archiveRoot | Out-Null
        try {
            Expand-Archive -LiteralPath $archive -DestinationPath $archiveRoot -Force
            $archivePayload = Join-Path $archiveRoot 'payload'
            if (Test-Path -LiteralPath (Join-Path $archivePayload 'Flow.Windows.exe')) {
                $script:flowInstallerTemporaryRoot = $archiveRoot
                return $archivePayload
            }
            throw 'El ZIP no contiene un payload válido de Flow.Windows.'
        }
        catch {
            Remove-Item -LiteralPath $archiveRoot -Recurse -Force -ErrorAction SilentlyContinue
            throw
        }
    }

    $bundledPayload = Join-Path $installerRoot 'payload'
    if (Test-Path -LiteralPath (Join-Path $bundledPayload 'Flow.Windows.exe')) {
        return $bundledPayload
    }

    $project = Join-Path $installerRoot '..\Flow.Windows\Flow.Windows.csproj'
    if (-not (Test-Path -LiteralPath $project)) {
        throw 'No se encontró el payload autocontenido ni el proyecto Flow.Windows.'
    }

    $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        throw 'Este paquete no contiene payload autocontenido y el equipo no tiene .NET SDK. Ejecuta Build-FlowInstaller.ps1 en el equipo de desarrollo y distribuye el ZIP generado.'
    }

    $temporaryPayload = Join-Path ([IO.Path]::GetTempPath()) ('FlowInstaller-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $temporaryPayload | Out-Null
    $script:flowInstallerTemporaryRoot = $temporaryPayload
    Write-Host 'No se encontró un payload empaquetado; publicando Flow de forma autocontenida…' -ForegroundColor Yellow
    & $dotnet.Source publish $project -c Release -r win-x64 --self-contained true -o $temporaryPayload --nologo | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $temporaryPayload 'Flow.Windows.exe'))) {
        throw 'No se pudo generar el ejecutable autocontenido de Flow.'
    }
    Copy-Item -LiteralPath (Join-Path $installerRoot '..\Flow.Windows\FlowLogo.ico') -Destination $temporaryPayload -Force
    return $temporaryPayload
}

function Stop-InstalledFlow {
    # The old and current installers used different locations. Stop every
    # process with Flow's exact executable name so an old binary cannot keep
    # the tray icon or lock the destination during an upgrade.
    @(Get-CimInstance Win32_Process -Filter "Name='Flow.Windows.exe'" -ErrorAction SilentlyContinue) |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $running = @(Get-CimInstance Win32_Process -Filter "Name='Flow.Windows.exe'" -ErrorAction SilentlyContinue)
        if ($running.Count -eq 0) { return }
        Start-Sleep -Milliseconds 250
    }
    throw 'No se pudo cerrar la instancia anterior de Flow antes de actualizarla.'
}

Write-Host '==========================================' -ForegroundColor Cyan
Write-Host '       Instalador autocontenido de Flow   ' -ForegroundColor Cyan
Write-Host '==========================================' -ForegroundColor Cyan

$payload = Get-PayloadDirectory
$bundledPayload = Join-Path $installerRoot 'payload'
$temporaryPayload = $payload -ne $bundledPayload
$executable = Join-Path $InstallDir 'Flow.Windows.exe'

try {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Stop-InstalledFlow
    Copy-Item -Path (Join-Path $payload '*') -Destination $InstallDir -Recurse -Force

    if (-not (Test-Path -LiteralPath $executable)) {
        throw 'La instalación no contiene Flow.Windows.exe.'
    }
    $iconPath = Join-Path $InstallDir 'FlowLogo.ico'
    if (-not (Test-Path -LiteralPath $iconPath)) {
        throw 'La instalación no contiene el icono vectorial de Flow.'
    }

    $registryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $registryPath -Force | Out-Null
    if ($NoAutoStart) {
        Remove-ItemProperty -Path $registryPath -Name 'FlowVoice' -ErrorAction SilentlyContinue
    }
    else {
        Set-ItemProperty -Path $registryPath -Name 'FlowVoice' -Value ('"{0}" --background' -f $executable)
    }

    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    New-Item -ItemType Directory -Force -Path $startMenu | Out-Null
    $shortcutPath = Join-Path $startMenu 'Flow.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $executable
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.Description = 'Flow - dictado por voz y reuniones'
    $shortcut.IconLocation = "$iconPath,0"
    $shortcut.Save()
    # Tell Explorer to invalidate the old cached icon for the shortcut.
    $iconRefresh = Join-Path $env:SystemRoot 'System32\ie4uinit.exe'
    if (Test-Path -LiteralPath $iconRefresh) {
        Start-Process -FilePath $iconRefresh -ArgumentList '-show' -WindowStyle Hidden -Wait
    }

    Write-Host "Flow instalado en: $InstallDir" -ForegroundColor Green
    if ($NoAutoStart) {
        Write-Host 'Inicio automático: desactivado.'
    }
    else {
        Write-Host 'Inicio automático: activado en segundo plano.'
    }
    Write-Host 'Tailscale: desactivado por defecto; no se instala ni se modifica.'

    if ($EnableTailscale) {
        $tailscale = Resolve-TailscaleExecutable
        if ($tailscale) {
            Write-Host "Tailscale opcional detectado en $tailscale. Flow no cambia sus rutas ni inicia sesión por ti." -ForegroundColor Yellow
        }
        else {
            Write-Warning 'Se solicitó la opción Tailscale, pero no está instalado. La instalación de Flow continúa sin Tailscale.'
        }
    }

    if (-not $SkipGroqKey) {
        Write-Host ''
        Write-Host 'La clave de Groq se guardará en el Administrador de credenciales de Windows.' -ForegroundColor Yellow
        Write-Host 'No se escribirá en el repositorio, en un archivo ni en los argumentos del proceso.'
        $groqKey = Read-FlowSecret -Prompt 'Clave de Groq (Enter para configurarla más tarde)'
        if ([string]::IsNullOrWhiteSpace($groqKey)) {
            Write-Warning 'Flow está instalado, pero todavía no tiene una clave de Groq.'
        }
        else {
            Set-FlowCredential -Target 'Flow/GroqApiKey' -Value $groqKey
            Write-Host 'Clave de Groq guardada correctamente.' -ForegroundColor Green
        }
        $groqKey = $null
    }

    $existing = Get-Process -Name 'Flow.Windows' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $executable } | Select-Object -First 1
    if ($null -eq $existing -and -not $NoAutoStart) {
        Start-Process -FilePath $executable -ArgumentList '--background' -WorkingDirectory $InstallDir -WindowStyle Hidden
        Write-Host 'Flow iniciado en segundo plano.' -ForegroundColor Green
    }

    Write-Host ''
    Write-Host 'Instalación completada. Abre Flow desde el menú Inicio para configurar el resto.' -ForegroundColor Green
}
finally {
    if ($script:flowInstallerTemporaryRoot -and (Test-Path -LiteralPath $script:flowInstallerTemporaryRoot)) {
        Remove-Item -LiteralPath $script:flowInstallerTemporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
