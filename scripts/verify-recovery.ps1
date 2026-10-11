param([string]$BaseUrl = 'http://127.0.0.1:5188')

$ErrorActionPreference = 'Stop'
# Compose publishes IPv4 loopback. Avoid a slow IPv6 fallback masking the two-second crash window.
$baseUri = [UriBuilder]::new($BaseUrl)
if ($baseUri.Host -eq 'localhost') { $baseUri.Host = '127.0.0.1' }
$BaseUrl = $baseUri.Uri.AbsoluteUri.TrimEnd('/')
Push-Location (Join-Path $PSScriptRoot '..')

function Call-Lab([string]$Path, $Body = $null, $Headers = @{}) {
    $parameters = @{ Uri = "$BaseUrl$Path"; Headers = $Headers; TimeoutSec = 5 }
    if ($null -ne $Body) {
        $parameters.Method = 'Post'
        $parameters.ContentType = 'application/json'
        $parameters.Body = $Body | ConvertTo-Json -Depth 10 -Compress
    }
    Invoke-RestMethod @parameters
}

function Wait-For([scriptblock]$Condition, [string]$Description, [int]$Seconds = 40) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { if (& $Condition) { return } } catch { }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out: $Description"
}

function Read-Event([string]$Id) {
    (Call-Lab '/api/state').deliveries | Where-Object id -eq $Id | Select-Object -First 1
}

function New-TestOrder {
    $key = [Guid]::NewGuid().ToString()
    Call-Lab '/api/orders' @{ reference = "RECOVERY-$($key.Substring(0, 8))"; sku = 'KIT-03'; quantity = 1 } @{ 'Idempotency-Key' = $key }
}

function Assert-SingleEffect([string]$Id) {
    $receipt = (Call-Lab '/api/partner').receipts | Where-Object eventId -eq $Id
    if (@($receipt).Count -ne 1) { throw "Expected exactly one durable receiver receipt for $Id" }
    $count = docker compose exec -T db psql -U lab -d integration_lab -Atc "SELECT count(*) FROM partner_orders WHERE event_id = '$Id';"
    if ($LASTEXITCODE -ne 0 -or "$count".Trim() -ne '1') { throw "Expected one business effect for $Id" }
}

try {
    Call-Lab '/api/partner' @{ mode = 'healthy'; delayMs = 5000 } | Out-Null
    Call-Lab '/api/worker' @{ paused = $true } | Out-Null
    $queued = New-TestOrder
    docker compose restart lab
    if ($LASTEXITCODE -ne 0) { throw 'Could not restart lab.' }
    Wait-For { (Call-Lab '/health').status -eq 'healthy' } 'lab restart'
    if ((Read-Event $queued.eventId).status -ne 'Pending' -or !(Call-Lab '/api/state').paused) {
        throw 'Queued event or pause setting did not survive restart.'
    }
    Call-Lab '/api/worker' @{ paused = $false } | Out-Null
    Wait-For { (Read-Event $queued.eventId).status -eq 'Delivered' } 'queued event delivery'
    Assert-SingleEffect $queued.eventId
    Write-Output 'PASS: paused settings and queued order survive a real container restart.'

    Call-Lab '/api/partner' @{ mode = 'lost-ack'; delayMs = 15000 } | Out-Null
    $crashed = New-TestOrder
    Wait-For {
        $receipt = (Call-Lab '/api/partner').receipts | Where-Object eventId -eq $crashed.eventId
        $receipt -and (Read-Event $crashed.eventId).status -eq 'Delivering'
    } 'receiver commit before acknowledgement' 10
    docker compose kill -s SIGKILL lab
    if ($LASTEXITCODE -ne 0) { throw 'Could not kill the lab container.' }
    docker compose start lab
    if ($LASTEXITCODE -ne 0) { throw 'Could not restart the killed lab.' }
    Wait-For { (Read-Event $crashed.eventId).status -eq 'Delivered' } 'expired-lease recovery'
    $attempts = (Call-Lab "/api/deliveries/$($crashed.eventId)").attempts
    if ($attempts[0].outcome -ne 'Interrupted' -or $attempts[-1].outcome -ne 'Duplicate') {
        throw 'Expected an interrupted attempt followed by a deduplicated retry.'
    }
    Assert-SingleEffect $crashed.eventId
    Write-Output 'PASS: a real process crash recovers its lease without repeating the business effect.'
}
finally {
    try {
        docker compose start lab | Out-Null
        Wait-For { (Call-Lab '/health').status -eq 'healthy' } 'final health'
        Call-Lab '/api/worker' @{ paused = $false } | Out-Null
        Call-Lab '/api/partner' @{ mode = 'healthy'; delayMs = 5000 } | Out-Null
    }
    finally { Pop-Location }
}
