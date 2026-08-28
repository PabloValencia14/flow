$ErrorActionPreference = 'Stop'

$root = Join-Path $env:TEMP 'flowhub-sync-verification-21001'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Path $root | Out-Null

$env:FLOW_HUB_URLS = 'http://127.0.0.1:21001'
$env:FLOW_HUB_DATA_ROOT = $root
$env:FLOW_HUB_APP_TOKEN = 'flowhub-test-token'
$exe = Join-Path $PSScriptRoot 'bin\Release\net9.0\FlowHub.exe'
$stdoutLog = Join-Path $root 'server.out.log'
$stderrLog = Join-Path $root 'server.err.log'
$process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden `
  -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -PassThru

try {
    $healthy = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        try {
            $health = Invoke-RestMethod 'http://127.0.0.1:21001/healthz' -TimeoutSec 2
            if ($health.status -eq 'ok') { $healthy = $true; break }
        } catch { }
    }
    if (-not $healthy) { throw 'FlowHub no llegó a healthz.' }

    $headers = @{ Authorization = 'Bearer flowhub-test-token' }
    $device = @{ deviceId = 'test-device'; name = 'Test'; platform = 'windows'; version = 'test' } |
        ConvertTo-Json
    Invoke-RestMethod 'http://127.0.0.1:21001/v1/devices' -Method Post -Headers $headers `
        -ContentType 'application/json' -Body $device | Out-Null

    $operations = @(
        @{ eventId = 'e1'; entity = 'dictations'; entityId = 'd1'; operation = 'create'; payload = @{ dictationId = 'd1'; text = 'uno'; createdAt = '2026-08-27T10:00:00Z'; durationSeconds = 1; wordCount = 1 } },
        @{ eventId = 'e2'; entity = 'dictations'; entityId = 'd2'; operation = 'create'; payload = @{ dictationId = 'd2'; text = 'dos'; createdAt = '2026-08-27T10:00:01Z'; durationSeconds = 2; wordCount = 1 } }
    )
    $pushBody = @{ deviceId = 'test-device'; operations = $operations } | ConvertTo-Json -Depth 8
    $first = Invoke-RestMethod 'http://127.0.0.1:21001/v1/sync/push' -Method Post -Headers $headers `
        -ContentType 'application/json' -Body $pushBody
    $retry = Invoke-RestMethod 'http://127.0.0.1:21001/v1/sync/push' -Method Post -Headers $headers `
        -ContentType 'application/json' -Body $pushBody

    $meetingBody = @{ meetingId = 'm1'; title = 'Test'; startedAt = '2026-08-27T10:00:00Z'; endedAt = '2026-08-27T10:01:00Z'; transcript = 'hola' } | ConvertTo-Json
    $meeting1 = Invoke-RestMethod 'http://127.0.0.1:21001/v1/meetings' -Method Post -Headers $headers `
        -ContentType 'application/json' -Body $meetingBody
    $meeting2 = Invoke-RestMethod 'http://127.0.0.1:21001/v1/meetings' -Method Post -Headers $headers `
        -ContentType 'application/json' -Body $meetingBody

    $events = @((Invoke-WebRequest 'http://127.0.0.1:21001/v1/sync/pull?after=0&limit=500' -Headers $headers).Content | ConvertFrom-Json)
    $unauthorized = 0
    try { Invoke-WebRequest 'http://127.0.0.1:21001/v1/devices' -ErrorAction Stop | Out-Null }
    catch { $unauthorized = [int]$_.Exception.Response.StatusCode }

    [pscustomobject]@{
        firstAccepted = [int]$first.accepted
        firstAcknowledged = @($first.acknowledgedEventIds).Count
        retryAccepted = [int]$retry.accepted
        retryAcknowledged = @($retry.acknowledgedEventIds).Count
        eventCount = $events.Count
        meetingWasIdempotent = ($meeting1.meetingId -eq $meeting2.meetingId)
        unauthorizedStatus = $unauthorized
    } | ConvertTo-Json -Compress
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    if ($process) { $process.Close(); $process.Dispose() }
    Remove-Item Env:FLOW_HUB_URLS -ErrorAction SilentlyContinue
    Remove-Item Env:FLOW_HUB_DATA_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:FLOW_HUB_APP_TOKEN -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 250
    if (Test-Path -LiteralPath $root) {
        try { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction Stop } catch { }
    }
}
