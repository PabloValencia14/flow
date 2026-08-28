$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'CredentialTools.ps1')

Write-Host 'Configurar clave de Groq para Flow' -ForegroundColor Cyan
$groqKey = Read-FlowSecret -Prompt 'Clave de Groq'
if ([string]::IsNullOrWhiteSpace($groqKey)) {
    throw 'No se recibió ninguna clave.'
}

Set-FlowCredential -Target 'Flow/GroqApiKey' -Value $groqKey
$groqKey = $null
Write-Host 'Clave guardada en el Administrador de credenciales de Windows.' -ForegroundColor Green
