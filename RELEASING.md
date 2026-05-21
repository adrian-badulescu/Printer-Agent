# Printer Agent — release pe GitHub

## Cerințe

- Runner **self-hosted** Windows (`self-hosted`, `Windows`).
- **.NET 10 SDK instalat o dată pe mașina runner** (Administrator), pe PATH — workflow-ul **nu** rulează `setup-dotnet` (contul serviciului GitHub Actions nu poate scrie în `C:\Program Files\dotnet`).
- Secret **Actions** în repo: `REDIS_PASSWORD` (obligatoriu pentru build CI). Opțional: `REDIS_HOST`, `REDIS_USER`.

## Secrete Redis (nu în Git)

**Nu** pune parola Redis în `agent.json` commit-at. Template-ul din repo are `"Password": ""`.

La fiecare build CI (push `main`, `workflow_dispatch`, sau tag `v*`), workflow-ul injectează parola din GitHub Secrets în `agent.json` pe runner, apoi construiește MSI-ul. Parola ajunge în fișierul `agent.json` lângă EXE din installer, fără să fie în istoricul Git.

### Configurare o singură dată

GitHub → **Settings** → **Secrets and variables** → **Actions** → **New repository secret**:

| Secret | Obligatoriu | Descriere |
|--------|-------------|-----------|
| `REDIS_PASSWORD` | Da | Parola Redis (ACL / requirepass) |
| `REDIS_HOST` | Nu | Dacă lipsește, rămâne valoarea din `agent.json` (ex. `10.8.0.1`) |
| `REDIS_USER` | Nu | User ACL Redis, dacă e cazul |

### Build local (fără CI)

1. Copiază [`PrinterAgent.Worker/agent.json.example`](PrinterAgent.Worker/agent.json.example) peste `agent.json` sau editează `agent.json` local.
2. Completează `Redis:Password` (și `BackendUrl`) **doar pe mașina ta** — nu face commit la parolă.
3. Alternativ: `agent.local.json` (ignorat de git) dacă adaugi suport în viitor; pentru acum editezi `agent.json` local necomitat.

După instalare, operatorul poate suprascrie enroll/imprimante în `%ProgramData%\URSPrinterAgent\agent.json`. Cheile Redis din MSI (lângă EXE) au prioritate dacă sunt non-goale.

## Instalare la client (fără scripturi manuale)

`URSPrinterAgentSetup.exe` de la GitHub Actions este singurul pas pentru restaurant:

1. Rulează setup-ul (Administrator / UAC).
2. La final, Configurator pentru enroll + imprimante.

MSI **1.0.14+** curăță automat servicii zombie (`DISABLED` / `DeleteFlag`) înainte de `InstallServices` — **nu** e nevoie de `scripts/Cleanup-*.ps1` la client. Scripturile din `scripts/` sunt doar pentru suport intern pe mașini de dev.

Dacă un client are deja o instalare stricată: trimite același link de download (ultimul release) și cere **reinstalare** peste ce există (MajorUpgrade); setup-ul repară înregistrarea serviciului.

## Cum publici

1. Actualizează versiunea în `Package.wxs`, `Bundle.wxs`, și `Version` din `PrinterAgent.Worker/agent.json`.
2. Setează **`BackendUrl`** în `agent.json` (fără parolă Redis în commit).
3. Push pe `main` → CI produce artifact; sau tag pentru Release:
   ```bash
   git tag v1.0.13
   git push origin v1.0.13
   ```
4. CI produce **`URSPrinterAgentSetup.exe`** (Release la tag `v*`; artifact la push `main`).

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
