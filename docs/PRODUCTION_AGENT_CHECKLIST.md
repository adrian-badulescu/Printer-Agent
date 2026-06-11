# Production checklist: Printer Agent

Use before tagging a production release (`v*`).

## Backend smoke (verified)

| Check | Command / action | Expected |
|-------|------------------|----------|
| Public API | `Invoke-WebRequest https://universalrestaurant.systems/api/ping-lite` | HTTP 200 |
| EF migrations | On production DB | `PrinterAgentEnrollmentCodes`, `PrinterAgentRegistrations`, refresh columns |
| Enrollment pepper | `/etc/urs/qrapi-production.env` → `PrinterAgent__EnrollmentCodePepper` | Non-empty |
| Update signature | `PrinterAgent__UpdateSignatureSecret` | Matches value baked into MSI at CI build |
| Redis VPS | From App VPS with WG: `redis-cli -h 10.60.0.2 -a 'PASSWORD' ping` | `PONG` |
| WireGuard SSH | `wg-peer-upsert` on hub; `AllowedIps` = `10.60.0.2/32` | Peer created on enroll |
| Enrollment code | Manager UI → Settings → printer agent | New 10-char code for pilot restaurant |

## GitHub Actions secrets (Printer-Agent repo)

| Secret | Production value |
|--------|------------------|
| `REDIS_PASSWORD` | Redis VPS `requirepass` |
| `REDIS_HOST` | `10.60.0.2` |
| `REDIS_USER` | Optional ACL user |
| `BACKEND_URL` | `https://universalrestaurant.systems` (optional; bundled `agent.json` already has prod URL) |

## Release (v1.2.7)

- Tag: `v1.2.7` on `main`
- Download: `https://github.com/adrian-badulescu/Printer-Agent/releases/download/v1.2.7/URSPrinterAgentSetup.exe`
- Backend `PrinterAgent:LatestVersion` = `1.2.7` (deploy `production` branch to apply)

CI on self-hosted runner requires `REDIS_PASSWORD` (and recommended `REDIS_HOST`=`10.60.0.2`). Set under GitHub → Settings → Secrets and variables → Actions, then re-run workflow or push a noop commit if the release build failed.

## Pilot E2E

See [E2E_AGENT_DEPLOYMENT_CHECKLIST.md](E2E_AGENT_DEPLOYMENT_CHECKLIST.md).

Automated smoke on dev machine (2026-06-11):

- `scripts/Validate-ProductionConnectivity.ps1` — prod `ping-lite` + enroll endpoint OK
- `scripts/Verify-UrsPrinterAgentInstall.ps1 -ExpectServiceRunning` — install layout OK
- `scripts/Show-AgentSessionSummary.ps1` — session present (re-enroll required after MSI upgrade to prod `BackendUrl`)

Full pilot: install `URSPrinterAgentSetup.exe` v1.2.7, new enrollment code from prod manager UI, Configurator, print test.
