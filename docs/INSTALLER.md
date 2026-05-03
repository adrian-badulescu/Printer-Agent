## URS Printer Agent installer (single EXE)

The bundle output is built at:

- `PrinterAgent.Bundle/bin/Release/URSPrinterAgentSetup.exe`

### Interactive install

- Run `URSPrinterAgentSetup.exe`
- Pick UI language from the dropdown on the first screen
- Install

### Silent install / upgrade

```powershell
Start-Process -FilePath ".\URSPrinterAgentSetup.exe" -ArgumentList "/quiet /norestart" -Wait -NoNewWindow
```

### Silent uninstall

```powershell
Start-Process -FilePath ".\URSPrinterAgentSetup.exe" -ArgumentList "/uninstall /quiet /norestart" -Wait -NoNewWindow
```

### Logs

- Bundle logs can be written with:

```powershell
Start-Process -FilePath ".\URSPrinterAgentSetup.exe" -ArgumentList "/log `"$env:TEMP\\URSPrinterAgentSetup.log`"" -Wait -NoNewWindow
```

### Printer IPs and heartbeats (support)

- **Source of truth for printer IPs is local `agent.json`**, under `%ProgramData%\URSPrinterAgent\agent.json`. The manager UI reads **`PrinterAgentHeartbeats.PrintersJson`**, which is overwritten on **every successful agent heartbeat** with whatever the agent sends.
- **Manual SQL edits to `PrintersJson` do not stick**: the next heartbeat replaces them from `agent.json`.
- **Same LAN, DHCP changed IP**: the agent can recover via MAC/ARP and port 9100 discovery (see product release notes). **VLAN / L3 change** (agent and printer no longer in the same broadcast domain) is **out of scope** for automatic recovery—use **agent re-setup** (Configurator, printers, `agent.json`), not only a DB fix.

