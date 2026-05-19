# Printer Agent — release pe GitHub

## Cerințe

- Runner **self-hosted** Windows (`self-hosted`, `Windows`).
- .NET SDK 10 pe runner.

## Cum publici

1. Actualizează versiunea în `Package.wxs`, `Bundle.wxs`, și `Version` din `PrinterAgent.Worker/agent.json`.
2. Setează **`BackendUrl`** (și Redis) în `PrinterAgent.Worker/agent.json` (inclus în MSI la build).
3. Tag și push:
   ```bash
   git tag v1.0.13
   git push origin v1.0.13
   ```
4. CI produce **`URSPrinterAgentSetup.exe`** pe GitHub Release (fără ZIP).

## Link download (FE)

`https://github.com/<OWNER>/<REPO>/releases/download/<TAG>/URSPrinterAgentSetup.exe`

Sau latest (dacă numele asset-ului rămâne fix):

`https://github.com/<OWNER>/<REPO>/releases/latest/download/URSPrinterAgentSetup.exe`

## Instalare

Rulează `URSPrinterAgentSetup.exe` (WireGuard + agent). La final, bifează lansarea Configuratorului: enroll + imprimante, apoi repornește serviciul dacă e oprit.

Vezi `docs/E2E_AGENT_DEPLOYMENT_CHECKLIST.md`.

## Build local (opțional)

```powershell
dotnet build PrinterAgent.Bundle/PrinterAgent.Bundle.wixproj -c Release -p:SelfSignedMsiSigning=true
# → PrinterAgent.Bundle\bin\Release\URSPrinterAgentSetup.exe
```
