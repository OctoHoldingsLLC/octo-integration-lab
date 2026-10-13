param(
    [string]$BaseUrl = 'http://127.0.0.1:5188',
    [string]$TraceUrl = 'http://127.0.0.1:16686'
)
$ErrorActionPreference = 'Stop'
$null = Invoke-RestMethod "$BaseUrl/api/worker" -Method Post -ContentType 'application/json' -Body '{"paused":false}'
$run = Invoke-RestMethod "$BaseUrl/api/runs" -Method Post -ContentType 'application/json' -Headers @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() } -Body '{"scenario":"two-partners"}'
$deadline = [DateTime]::UtcNow.AddSeconds(60)
do {
    $result = Invoke-RestMethod "$BaseUrl/api/runs/$($run.id)"
    if ($result.complete) { break }
    Start-Sleep -Milliseconds 300
} while ([DateTime]::UtcNow -lt $deadline)
if (!$result.complete) { throw 'Tracing experiment did not complete.' }
$details = @($run.deliveryIds | ForEach-Object { Invoke-RestMethod "$BaseUrl/api/deliveries/$_" })
$traceIds = @($details.attempts.traceId | Sort-Object -Unique)
if ($traceIds.Count -ne 1 -or $traceIds[0] -notmatch '^[a-f0-9]{32}$') { throw 'Retries and destinations did not share one trace.' }
$traceId = $traceIds[0]
$deadline = [DateTime]::UtcNow.AddSeconds(45)
$valid = $false
do {
    try {
        $response = Invoke-RestMethod "$TraceUrl/api/traces/$traceId"
        $trace = $response.data[0]
        $services = @($trace.processes.psobject.Properties.Value.serviceName | Sort-Object -Unique)
        $workers = @($trace.spans | Where-Object operationName -eq 'deliver order')
        $receiverSpans = @($trace.spans | Where-Object operationName -eq 'POST /webhooks/orders')
        $creation = @($trace.spans | Where-Object operationName -eq 'POST /api/runs')
        $valid = $services -contains 'octo-lab' -and $services -contains 'octo-partner-primary' -and $services -contains 'octo-partner-secondary' -and $workers.Count -eq 4 -and $receiverSpans.Count -eq 4 -and $creation.Count -eq 1
        if ($valid) {
            foreach ($worker in $workers) {
                if ($worker.references.spanID -notcontains $creation[0].spanID) { throw 'Worker lost its persisted creation parent.' }
            }
            foreach ($receiver in $receiverSpans) {
                $http = @($trace.spans | Where-Object spanID -eq $receiver.references[0].spanID)
                if ($http.Count -ne 1 -or $workers.spanID -notcontains $http[0].references[0].spanID) { throw 'Receiver is not connected through its HTTP send span.' }
            }
            break
        }
    } catch { $lastError = $_.Exception.Message; $valid = $false }
    Start-Sleep -Milliseconds 500
} while ([DateTime]::UtcNow -lt $deadline)
if (!$valid) { throw "Exported trace was incomplete. Services: $services. Last error: $lastError" }
Write-Host "PASS: creation, four delivery attempts, and both independent partners share trace $traceId with valid parent links."
