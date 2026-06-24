# Production checklist: Printer Agent

Use before tagging a production release (`v*`).

## Backend smoke (verified)

| Check | Command / action | Expected |
|-------|------------------|----------|
| Public API | `Invoke-WebRequest https://universalrestaurant.systems/api/ping-lite` | HTTP 200 |
| EF migrations | On production DB | `PrinterAgentEnrollmentCodes`, `PrinterAgentRegistrations`, `PrinterAgentRestaurantRedisCredentials`, refresh columns |
| Enrollment pepper | `/etc/urs/qrapi-production.env` → `PrinterAgent__EnrollmentCodePepper` | Non-empty |
| Update signature | `PrinterAgent__UpdateSignatureSecret` | Matches value baked into MSI at CI build |
| Redis VPS | From App VPS with WG: `redis-cli -h 10.60.0.2 -a 'PASSWORD' ping` | `PONG` |
| WireGuard SSH | `wg-peer-upsert` on hub; `AllowedIps` = `10.60.0.2/32` | Peer created on enroll |
| Enrollment code | Manager UI → Settings → printer agent | New 10-char code for pilot restaurant |

## GitHub Actions secrets (Printer-Agent repo)

| Secret | Production value |
|--------|------------------|
| `REDIS_PASSWORD` | **Optional (legacy)** — omit for new builds; agents fetch per-restaurant ACL creds after enroll |
| `REDIS_HOST` | `10.60.0.2` |
| `BACKEND_URL` | `https://universalrestaurant.systems` (optional; bundled `agent.json` already has prod URL) |

CI no longer requires `REDIS_PASSWORD`. New MSI builds ship with empty `Redis.Password`; credentials land in `%ProgramData%\URSPrinterAgent\redis.credentials.json` after enroll (`GET /api/agents/{id}/redis-credentials`). Backend must be deployed with the migration and Redis 6+ ACL support before pilot.

## Pilot E2E

See [E2E_AGENT_DEPLOYMENT_CHECKLIST.md](E2E_AGENT_DEPLOYMENT_CHECKLIST.md).

Automated smoke on pilot machine (2026-06-11):

- `scripts/Validate-ProductionConnectivity.ps1` — prod `ping-lite` + enroll endpoint OK
- `scripts/Verify-UrsPrinterAgentInstall.ps1 -ExpectServiceRunning` — install layout OK
- Local `URSPrinterAgentSetup.exe` v1.2.7 upgrade (`/quiet`) — install-dir `BackendUrl` + `Redis.Host` now prod
- Agent reaches `https://universalrestaurant.systems` (enroll returns 401 until valid prod enrollment code in ProgramData)
- Redis consumer: **per-restaurant ACL** via `redis-credentials` endpoint (or legacy MSI with `REDIS_PASSWORD` for one release)

**Remaining for full pilot:** prod enrollment code in Configurator, CI release asset (self-hosted runner must be online), print test from manager UI.

## Upgrading from dev/pilot to production MSI

The installer **does not replace** `%ProgramData%\URSPrinterAgent\agent.json` if it already exists (enrollment, printers preserved). After upgrade you may see:

| File | What you might see | What the service actually uses |
|------|-------------------|--------------------------------|
| `C:\Program Files\URSPrinterAgent\agent.json` | `BackendUrl` prod, `Redis.Host` `10.60.0.2`, `Version` 1.2.7 | **BackendUrl, Redis password/host** (BundledFirstKeys) |
| `%ProgramData%\URSPrinterAgent\agent.json` | Old dev `192.168.43.142`, `Version` 1.2.3 | EnrollmentCode, Printers only |
| `%ProgramData%\...\wireguard\urs-printer-agent.conf` | Dev hub `Endpoint = 192.168.43.142` | Tunnel routes — **must be reprovisioned** |

**Repair (Admin PowerShell):**

```powershell
cd path\to\Printer-Agent\scripts
.\Repair-ProductionAgentInstall.ps1
# If Redis NOAUTH until new MSI: .\Repair-ProductionAgentInstall.ps1 -SetRedisPassword 'prod-redis-password'
```

Agent **1.2.8+** auto-detects stale WireGuard `.conf` (LAN `192.168.*` or missing prod Redis in `AllowedIPs`) and re-downloads from backend.

**If `wireguard-conf` returns HTTP 400:** fix `PrinterAgent:WireGuard` + SSH on production API (see `docs/WIREGUARD-SSH-DEV.md`).

**If Redis `NOAUTH` on `10.60.0.2`:** ensure backend deployed with ACL provisioning, agent enrolled (check `%ProgramData%\URSPrinterAgent\redis.credentials.json`), WireGuard up. Legacy MSI: set `REDIS_PASSWORD` secret and rebuild, or `Repair-ProductionAgentInstall.ps1 -SetRedisPassword`.
