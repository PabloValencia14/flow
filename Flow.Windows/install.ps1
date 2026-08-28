$ErrorActionPreference = 'Stop'

# Compatibility entry point. The distributable installer lives outside the
# project so it can also carry a self-contained payload.
$installer = Join-Path $PSScriptRoot '..\installer\Install-Flow.ps1'
if (-not (Test-Path -LiteralPath $installer)) {
    throw 'No se encontró flow\installer\Install-Flow.ps1.'
}

& $installer @args
exit $LASTEXITCODE
