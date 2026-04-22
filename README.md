# URS Printer Agent

Serviciu Windows care consumă job-uri de print din Redis, trimite heartbeat la backend și poate aplica update-uri semnate. Repo-ul include worker-ul .NET și documentație de enrollment în `docs/`.

## Date pe disc (ProgramData)

Directorul este **`%ProgramData%\URSPrinterAgent`** (ex. `C:\ProgramData\URSPrinterAgent`). La pornire este creat dacă lipsește.

| Fișier | Rol |
|--------|-----|
| `agent.json` | Config: `BackendUrl`, `RedisConnectionString`, `EnrollmentCode` (provisioning temporar), etc. |
| `agent.session.json` | Sesiune după enroll/refresh: `agentId`, `restaurantId`, expirare access JWT, tokenuri (pe Windows sunt stocate criptat cu **DPAPI LocalMachine** în câmpurile `*Protected`). |
| `client.instance` | GUID stabil al mașinii; trebuie să coincidă cu `agentId` din sesiune (același GUID în format `D`). |

## Autentificare: enroll și refresh

### Enroll

`POST /api/agents/enroll` (anonim, rate limit) — body: `enrollmentCode`, `clientInstanceId`.

Răspuns include **`accessToken`** (JWT), **`refreshToken`** (opac, returnat o singură dată), **`expiresAtUtc`** (expirarea access token-ului, aliniată cu claim-ul `exp` din JWT).

### Refresh

`POST /api/agents/refresh` (anonim, același tip de rate limit ca enroll) — body:

- `agentId` — același string ca la enroll (GUID instanță, format `D`)
- `clientInstanceId` — trebuie să fie același GUID ca `agentId`
- `refreshToken` — tokenul opac primit la enroll sau la refresh-ul anterior

La succes, răspunsul conține o nouă pereche **access + refresh** (rotație la refresh token) și **`expiresAtUtc`**.

**Înrolări vechi în baza de date:** înregistrările create înainte de coloanele `RefreshTokenHash` / `RefreshTokenExpiresUtc` nu au refresh stocat; agentul trebuie **re-enroll-at** (cod nou din manager) ca să primească refresh.

### Comportament agent

1. La pornire: încarcă sesiunea; dacă access JWT e încă valid (cu marjă 5 minute), nu apelează API-ul de auth.
2. Dacă access e expirat dar există refresh în `agent.session.json`, încearcă **`TryRenewIfAccessExpiredAsync`** (apel la `/api/agents/refresh`).
3. Dacă refresh reușește, nu mai cere `EnrollmentCode`.
4. Înainte de fiecare heartbeat, aceeași logică de reînnoire (proactiv).
5. La **401** pe heartbeat, sesiunea este ștearsă; este nevoie de re-enroll dacă nu mai există refresh valid.

Detalii suplimentare: [docs/TECHNICAL_PRINTER_AGENT_ENROLLMENT.md](docs/TECHNICAL_PRINTER_AGENT_ENROLLMENT.md), [docs/TOKEN_EXPIRY_AND_REENROLL.md](docs/TOKEN_EXPIRY_AND_REENROLL.md).

## Configurare backend (durate token)

În API, secțiunea **`PrinterAgent`** din `appsettings`:

- **`AgentAccessTokenLifetimeMinutes`** — durata JWT-ului de access pentru agenți (în Development poate fi setat mic, ex. `5`, pentru teste).
- **`AgentRefreshTokenLifetimeMinutes`** — cât timp este valabil refresh token-ul stocat (hash în DB); la fiecare refresh se emite unul nou și se invalidează vechiul.

Valorile sunt limitate în cod la interval rezonabil (minim 1 minut, maxim 365 de zile exprimate în minute).

## MSI (WiX) și Configurator

- **MSI:** `dotnet build PrinterAgent.Installer/PrinterAgent.Installer.wixproj -c Release` generează `PrinterAgent.Installer/bin/Release/PrinterAgent.msi` plus `cab1.cab` / `cab2.cab` în același folder. Înainte de build, oprește serviciul **URSPrinterAgent** dacă apare *Access denied* la publish (EXE blocat). **`dotnet publish` pe `.wixproj` poate să nu regenereze MSI-ul**; folosește **`dotnet build`** pe proiectul de installer. La finalul instalării, checkbox-ul **„Pornește Configuratorul…”** (bifat implicit) lansează `PrinterAgent.Configurator.exe` după **Terminare**.
- **Configurator (WPF):** după instalare, deschide **`C:\Program Files\URSPrinterAgent\`** (sau `Program Files (x86)` doar dacă ai forțat altfel). Acolo trebuie să fie `PrinterAgent.Configurator.exe` și shortcut-ul **Configure URS Printer Agent** lângă `PrinterAgent.Worker.exe`. **Nu** apare în Meniul Start (limitări ICE WiX pentru pachet per-machine). Dacă vezi doar worker-ul după ce ai avut deja un MSI mai vechi **fără** Configurator, Windows poate păstra fișierele vechi: **dezinstalează** „URS Printer Agent” din Setări → Aplicații, apoi instalează un MSI cu **versiune mai mare** (ex. 1.0.4 față de 1.0.3), sau rulează reparare din `msiexec /fvomus ...` după documentația Microsoft. Wizard: cod enroll, scan TCP 9100, imprimantă + `PrinterId`, salvare în `%ProgramData%\URSPrinterAgent\agent.json`. După editare config, **repornește serviciul**.
- **Mai multe imprimante (același subnet):** în Configurator, după prima salvare folosește **„Adaugă o altă imprimantă”**: același scan (sau din nou), altă adresă IP din listă, alt **PrinterId** (ex. `kitchen-1`, `bar-1`). `agent.json` va conține mai multe intrări în `Printers[]`; joburile de print trebuie să specifice `printerId` potrivit.
- **Verificare rapidă:** `.\scripts\Verify-UrsPrinterAgentInstall.ps1` (opțional `-RepoRoot ..` pentru a verifica și artefactele MSI din repo).

## Verificare E2E pe Windows (P0)

Pași compleți (ProgramData, serviciu, enroll, sesiune, heartbeat, refresh, print opțional): [docs/E2E_AGENT_DEPLOYMENT_CHECKLIST.md](docs/E2E_AGENT_DEPLOYMENT_CHECKLIST.md).

După primul start, sumar rapid al sesiunii (fără a expune tokenuri):

```powershell
.\scripts\Show-AgentSessionSummary.ps1
```

## Rulare locală

1. Populează `PrinterAgent.Worker/agent.json` (sau copiază în `%ProgramData%\URSPrinterAgent\agent.json` după modelul din repo).
2. Rulează worker-ul cu backend-ul și migrarea DB aferente enrollment/refresh aplicate.

Proiect principal: `PrinterAgent.Worker`.
