# Sends POST /api/print-jobs (manager JWT) and polls GET .../status until Success, Failed, or timeout.
# Env fallbacks: URS_PRINT_BACKEND_URL, URS_MANAGER_BEARER, URS_PRINT_RESTAURANT_ID, URS_PRINT_PRINTER_ID
#
# While status stays "Printing", the agent may be retrying TCP to the printer (several attempts with backoff).
# Use a long enough -MaxWaitSeconds (default 300) or fix printer IP / use PrinterConnectTimeoutSeconds on the agent.
#
# Example:
#   $env:URS_PRINT_BACKEND_URL = 'http://localhost:7051'
#   $env:URS_MANAGER_BEARER = '<jwt>'
#   $env:URS_PRINT_RESTAURANT_ID = (Get-Content "$env:ProgramData\URSPrinterAgent\agent.session.json" | ConvertFrom-Json).restaurantId
#   $env:URS_PRINT_PRINTER_ID = 'kitchen-1'
#   .\Send-TestPrintJob.ps1

param(
    [string] $BackendUrl = $env:URS_PRINT_BACKEND_URL,
    [string] $BearerToken = $env:URS_MANAGER_BEARER,
    [string] $RestaurantId = $env:URS_PRINT_RESTAURANT_ID,
    [string] $PrinterId = $env:URS_PRINT_PRINTER_ID,
    [string] $OrderId = "e2e-$(Get-Date -Format 'yyyyMMddHHmmss')",
    [int] $PollSeconds = 2,
    [int] $MaxWaitSeconds = 300
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($BackendUrl)) {
    throw 'Set -BackendUrl or URS_PRINT_BACKEND_URL.'
}
if ([string]::IsNullOrWhiteSpace($BearerToken)) {
    throw 'Set -BearerToken or URS_MANAGER_BEARER (JWT with GlobalAdmin_Or_RestaurantManager).'
}
if ([string]::IsNullOrWhiteSpace($RestaurantId)) {
    throw 'Set -RestaurantId or URS_PRINT_RESTAURANT_ID (same GUID as agent session / print stream).'
}
if ([string]::IsNullOrWhiteSpace($PrinterId)) {
    throw 'Set -PrinterId or URS_PRINT_PRINTER_ID (must match Printers[].Id in agent ProgramData agent.json).'
}

$base = $BackendUrl.TrimEnd('/')
$createUri = "$base/api/print-jobs"
$headers = @{
    Authorization = "Bearer $BearerToken"
    'Content-Type' = 'application/json'
}

$bodyObj = [ordered]@{
    restaurantId = $RestaurantId
    printerId    = $PrinterId
    payload      = [ordered]@{
        type    = 'order'
        orderId = $OrderId
        items   = @(
            [ordered]@{ name = 'E2E test item'; quantity = 1; price = 1.0 }
        )
    }
}
$bodyJson = $bodyObj | ConvertTo-Json -Depth 8 -Compress

Write-Host "POST $createUri"
$response = Invoke-RestMethod -Uri $createUri -Method Post -Headers $headers -Body $bodyJson
$jobId = $response.jobId
if (-not $jobId) { $jobId = $response.JobId }
if (-not $jobId) {
    throw "Unexpected response (expected jobId): $($response | ConvertTo-Json -Compress)"
}
Write-Host "JobId: $jobId"

function Get-PrintJobStatusField {
    param($Response)
    $v = $Response.status
    if (-not $v) { $v = $Response.Status }
    if ($null -eq $v) { return $null }
    return "$v".Trim()
}

$statusUri = "$base/api/print-jobs/$([Uri]::EscapeDataString($jobId))/status"
$deadline = (Get-Date).AddSeconds($MaxWaitSeconds)
$last = $null
$pollIndex = 0
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $PollSeconds
    $pollIndex++
    $st = Invoke-RestMethod -Uri $statusUri -Method Get -Headers $headers
    $last = Get-PrintJobStatusField $st
    Write-Host "Status: $last"
    if ($null -ne $last) {
        $n = $last.ToLowerInvariant()
        if ($n -eq 'success' -or $n -eq 'failed') {
            Write-Host "Done: $last"
            exit 0
        }
    }
    if ($pollIndex -eq 8 -and $null -ne $last -and $last.ToLowerInvariant() -eq 'printing') {
        Write-Host '(Still Printing: agent is likely connecting/printing to ESC/POS; retries can take 1-2 min. Increase -MaxWaitSeconds if needed.)'
    }
}

Write-Warning "Timeout after ${MaxWaitSeconds}s; last status: $last. Verify JobId in table public.PrinterPrintJobs and agent Event Viewer."
exit 1
