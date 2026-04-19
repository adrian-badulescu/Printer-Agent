# Check service status
Write-Host "=== SERVICE STATUS ==="
Get-Service -Name "URSPrinterAgent" -ErrorAction SilentlyContinue | Format-List Name, Status, StartType

# Check recent Application log events related to the service
Write-Host "`n=== RECENT APPLICATION LOG EVENTS ==="
Get-WinEvent -LogName Application -MaxEvents 100 -ErrorAction SilentlyContinue | Where-Object {
    $_.ProviderName -match 'URSPrinterAgent|PrinterAgent|\.NET Runtime|MsiInstaller'
} | Select-Object -First 10 | Format-List TimeCreated, ProviderName, Message

# Check System log for service control manager events
Write-Host "`n=== SYSTEM LOG (Service Control Manager) ==="
Get-WinEvent -LogName System -MaxEvents 100 -ErrorAction SilentlyContinue | Where-Object {
    $_.ProviderName -eq 'Service Control Manager' -and $_.Message -match 'URSPrinterAgent|PrinterAgent'
} | Select-Object -First 5 | Format-List TimeCreated, Message
