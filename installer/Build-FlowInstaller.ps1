[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot 'release\Flow-Windows-Installer'),
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installerRoot = $PSScriptRoot
$project = Join-Path $installerRoot '..\Flow.Windows\Flow.Windows.csproj'
$outputRoot = [IO.Path]::GetFullPath($OutputDir)
$payload = Join-Path $outputRoot 'payload'
$zip = [IO.Path]::GetFullPath((Join-Path (Split-Path $outputRoot -Parent) 'Flow-Windows-Installer.zip'))

if (-not (Test-Path -LiteralPath $project)) {
    throw "No se encontró el proyecto: $project"
}
$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    throw 'Se necesita .NET SDK 9 para generar el instalador autocontenido.'
}

if ($Clean -and (Test-Path -LiteralPath $outputRoot)) {
    $installerParent = [IO.Path]::GetFullPath((Join-Path $installerRoot 'release'))
    $resolvedOutputRoot = [IO.Path]::GetFullPath($outputRoot)
    if (-not $resolvedOutputRoot.StartsWith($installerParent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OutputDir debe permanecer dentro de flow\installer\release.'
    }
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $payload | Out-Null
Write-Host 'Publicando Flow.Windows como aplicación autocontenida win-x64…' -ForegroundColor Yellow
& $dotnet.Source publish $project -c Release -r win-x64 --self-contained true -o $payload --nologo | Out-Host
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $payload 'Flow.Windows.exe'))) {
    throw 'La publicación de Flow.Windows ha fallado.'
}

Copy-Item -LiteralPath (Join-Path $installerRoot 'Install-Flow.ps1') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $installerRoot 'Set-FlowGroqKey.ps1') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $installerRoot 'CredentialTools.ps1') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $installerRoot 'install-flow.bat') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $installerRoot 'README.md') -Destination (Join-Path $outputRoot 'README-INSTALLER.md') -Force

if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $outputRoot '*') -DestinationPath $zip -CompressionLevel Optimal

$payloadSize = [Math]::Round(((Get-ChildItem -LiteralPath $payload -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
$zipSize = [Math]::Round(((Get-Item -LiteralPath $zip).Length / 1MB), 1)
Write-Host "Payload autocontenido: $payloadSize MB" -ForegroundColor Green
Write-Host "ZIP generado: $zipSize MB" -ForegroundColor Green
Write-Host "Carpeta instalable: $outputRoot" -ForegroundColor Green
