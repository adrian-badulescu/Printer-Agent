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

## Release

1. Align versions in `Package.wxs`, `Bundle.wxs`, `agent.json` `Version`.
2. Push `main` → verify CI artifact `URSPrinterAgentSetup.exe`.
3. `git tag vX.Y.Z && git push origin vX.Y.Z` → GitHub Release asset.
4. Update `PrinterAgent:LatestVersion` on backend if using signed auto-update.

## Pilot E2E

See [E2E_AGENT_DEPLOYMENT_CHECKLIST.md](E2E_AGENT_DEPLOYMENT_CHECKLIST.md).
