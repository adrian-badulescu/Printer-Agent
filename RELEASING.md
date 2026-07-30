# Printer Agent — release pe GitHub

## Cerințe

- Runner **self-hosted** Windows (`self-hosted`, `Windows`).
- **.NET 10 SDK instalat o dată pe mașina runner** (Administrator), pe PATH — workflow-ul **nu** rulează `setup-dotnet` (contul serviciului GitHub Actions nu poate scrie în `C:\Program Files\dotnet`).
- Secret **Actions** opțional: `REDIS_PASSWORD` (legacy — parolă globală în MSI). **Recomandat:** nu seta `REDIS_PASSWORD`; agenții primesc credențiale Redis **per restaurant** după enroll (`GET /api/agents/{id}/redis-credentials`). Recomandat producție: `REDIS_HOST`=`10.60.0.2`, `BACKEND_URL`=`https://universalrestaurant.systems`.

## Secrete Redis (nu în Git)

**Nu** pune parola Redis în `agent.json` commit-at. Template-ul din repo are `"Password": ""`.

### Model recomandat (per restaurant)

După enroll, agentul apelează backend-ul pentru credențiale ACL limitate la `print.jobs.{restaurantId}` și le salvează în `%ProgramData%\URSPrinterAgent\redis.credentials.json` (DPAPI pe Windows). MSI-ul nu mai conține parola globală.

### Legacy (un release)

Dacă setezi secretul `REDIS_PASSWORD`, CI îl injectează în MSI ca înainte (fallback pentru agenți vechi).

### Configurare o singură dată

GitHub → **Settings** → **Secrets and variables** → **Actions** → **New repository secret**:

| Secret | Obligatoriu | Descriere |
|--------|-------------|-----------|
| `REDIS_PASSWORD` | Nu (legacy) | Parola Redis globală în MSI — evită pentru build-uri noi |
| `REDIS_HOST` | Recomandat prod | `10.60.0.2` (Redis VPS via WireGuard). Dacă lipsește, rămâne valoarea din `agent.json`. |
| `BACKEND_URL` | Nu | Dacă lipsește, rămâne `BackendUrl` din `agent.json` (prod: `https://universalrestaurant.systems`). |
| `REDIS_USER` | Nu | Doar pentru legacy MSI cu ACL user global |
| `UPDATE_SIGNATURE_SECRET` | Recomandat prod | Secret HMAC pentru `release-manifest.json` + `UpdateSignatureSecret` în MSI. **Nu** comite în git. |

### Build local (fără CI)

1. Copiază [`PrinterAgent.Worker/agent.json.example`](PrinterAgent.Worker/agent.json.example) peste `agent.json` sau editează `agent.json` local.
2. Completează `Redis:Password` (și `BackendUrl`) **doar pe mașina ta** — nu face commit la parolă.
3. Alternativ: `agent.local.json` (ignorat de git) dacă adaugi suport în viitor; pentru acum editezi `agent.json` local necomitat.

După instalare, operatorul poate suprascrie enroll/imprimante în `%ProgramData%\URSPrinterAgent\agent.json`. Cheile Redis din MSI (lângă EXE) au prioritate pentru Host/Port; parola vine din `redis.credentials.json` după enroll (sau din MSI doar la build-uri legacy cu `REDIS_PASSWORD`).

## Instalare la client (fără scripturi manuale)

`URSPrinterAgentSetup.exe` de la GitHub Actions este singurul pas pentru restaurant:

1. Rulează setup-ul (Administrator / UAC).
2. La final, Configurator pentru enroll + imprimante.

MSI **1.0.14+** curăță automat servicii zombie (`DISABLED` / `DeleteFlag`) înainte de `InstallServices` — **nu** e nevoie de `scripts/Cleanup-*.ps1` la client. Scripturile din `scripts/` sunt doar pentru suport intern pe mașini de dev.

Dacă un client are deja o instalare stricată: trimite același link de download (ultimul release) și cere **reinstalare** peste ce există (MajorUpgrade); setup-ul repară înregistrarea serviciului.

## Cum publici

1. Actualizează versiunea **în același commit** în **trei** fișiere (sau rulează scriptul — vezi [`docs/config.md`](docs/config.md)):
   - `PrinterAgent.Installer/Package.wxs` — format `1.5.14.0`
   - `PrinterAgent.Bundle/Bundle.wxs` — format `1.5.14.0`
   - `PrinterAgent.Worker/agent.json` → `Version` — format `1.5.14`

   ```powershell
   .\scripts\Bump-ReleaseVersion.ps1 -Version 1.5.14
   ```

2. Tag-ul git trebuie să fie `v` + aceeași versiune (ex. `v1.5.14`). CI verifică alinierea la build pe tag.
3. Setează **`BackendUrl`** în `agent.json` (fără parolă Redis în commit).
4. Push pe `main` → CI produce artifact; sau tag pentru Release:
   ```bash
   git tag v1.5.14
   git push origin v1.5.14
   ```
5. CI produce **`URSPrinterAgentSetup.exe`** + **`release-manifest.json`** (Release la tag `v*`; artifact la push `main`).

## Auto-update (fără restart backend)

La tag `v*`, agenții enrolled verifică la ~30s manifestul GitHub și se actualizează singuri.

| Asset release | Rol |
|---------------|-----|
| `URSPrinterAgentSetup.exe` | Installer WiX Burn (MSI + WireGuard embedded) |
| `release-manifest.json` | Versiune, URL download, SHA256, semnătură HMAC |

Manifest (generat de CI):

`https://github.com/adrian-badulescu/Printer-Agent/releases/latest/download/release-manifest.json`

Agentul compară `manifest.version` cu `Version` locală din install-dir, verifică semnătura + hash, descarcă EXE în `%ProgramData%\URSPrinterAgent\updates\`, apoi lansează installer-ul cu ~7s întârziere (serviciul iese înainte ca WiX să facă upgrade).

Log installer auto-update: `%TEMP%\urs-agent-update.log`

**Prima activare:** agenții foarte vechi (fără `UpdateManifestUrl` / secret real) pot necesita o reinstalare manuală; după aceea lanțul auto-update funcționează.

**Fallback:** dacă `UpdateManifestUrl` e gol în `agent.json`, agentul folosește vechiul `GET api/agents/{id}/update` (backend).

Script local (debug):

```powershell
.\scripts\New-ReleaseManifest.ps1 `
  -InstallerPath .\URSPrinterAgentSetup.exe `
  -Version 1.5.0 `
  -OutputPath .\release-manifest.json `
  -UpdateSignatureSecret $env:UPDATE_SIGNATURE_SECRET
```

Log installer auto-update: `%TEMP%\urs-agent-update.log`

## Link download (FE)

`https://github.com/<OWNER>/<REPO>/releases/download/<TAG>/URSPrinterAgentSetup.exe`

Sau latest (dacă numele asset-ului rămâne fix):

`https://github.com/<OWNER>/<REPO>/releases/latest/download/URSPrinterAgentSetup.exe`

## Instalare

Rulează `URSPrinterAgentSetup.exe` (WireGuard + agent). La final, bifează lansarea Configuratorului: enroll + imprimante, apoi repornește serviciul dacă e oprit.

Vezi `docs/E2E_AGENT_DEPLOYMENT_CHECKLIST.md` și `docs/PRODUCTION_AGENT_CHECKLIST.md`.

## Build local (opțional)

```powershell
# Cu parolă Redis (ca la CI):
$env:REDIS_PASSWORD = '...'
$env:REDIS_HOST = '10.60.0.2'
.\scripts\Build-ProductionInstaller.ps1

# Fără inject (doar dev; Redis.Password gol în MSI):
dotnet build PrinterAgent.Bundle/PrinterAgent.Bundle.wixproj -c Release -p:SelfSignedMsiSigning=true
# → PrinterAgent.Bundle\bin\Release\URSPrinterAgentSetup.exe
```

**CI:** workflow-ul rulează pe runner **self-hosted** Windows. Dacă build-urile rămân `queued`, pornește runner-ul sau rulează `Build-ProductionInstaller.ps1` și încarcă manual asset-urile la release (`gh release upload v1.2.7 URSPrinterAgentSetup.exe release-manifest.json`).
