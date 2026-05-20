## URS Printer Agent installer (single EXE)

The bundle output is built at:

- `PrinterAgent.Bundle/bin/Release/URSPrinterAgentSetup.exe`

### Interactive install

- Run `URSPrinterAgentSetup.exe`
- Pick UI language from the dropdown on the first screen
- Install

### Windows service (automatic, no support scripts)

MSI **1.0.14+** runs deferred actions on every install/upgrade (before `InstallServices`):

- `sc stop` / `sc delete` (1062 if already stopped is OK)
- `reg delete` of `HKLM\SYSTEM\CurrentControlSet\Services\URSPrinterAgent` when a zombie remains (`DISABLED` + `DeleteFlag=1`)
- then `ServiceInstall` + `start= auto` + `sc start`

**At restaurants:** only run the setup EXE from GitHub Releases/Artifacts. Do not ask clients to run `scripts/Cleanup-*.ps1`.

**Broken install already on site:** ship the latest `URSPrinterAgentSetup.exe` and have them run it again (upgrade/repair); no on-site PowerShell.

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

