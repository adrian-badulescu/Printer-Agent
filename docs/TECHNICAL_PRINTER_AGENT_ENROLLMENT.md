# Printer agent: enrollment, session storage, and backend integration

This document describes the technical design implemented for the URS printer agent and its pairing with the QR restaurant backend.

## Goals

- Replace MAC-derived `AgentId` with a **stable GUID per machine** (`clientInstanceId`), aligned with backend `PrinterAgentRegistrations`.
- Use **short-lived JWTs** for API calls after a **one-time (or limited-use) enrollment code** from the manager UI.
- Resolve **restaurant identity on the server** from the enrollment code only (no manual `RestaurantId` in config for provisioning).
- Store secrets and session data under a **machine-wide, service-friendly directory** (same path in development and production).

## Agent data directory

All persistent files use **`Environment.SpecialFolder.CommonApplicationData`** + **`URSPrinterAgent`**:

| Platform | Typical path |
|----------|----------------|
| Windows | `%ProgramData%\URSPrinterAgent` → e.g. `C:\ProgramData\URSPrinterAgent` |

The directory is created on startup if missing. Code: `PrinterAgent.Application.Storage.AgentProgramData`.

### Files

| File | Description |
|------|-------------|
| `agent.json` | Main configuration: `BackendUrl`, Redis, printers, optional `EnrollmentCode`, `UpdateSignatureSecret`, etc. Loaded with `SetBasePath(AgentProgramData.Root)`. |
| `client.instance` | Single-line GUID; sent as `clientInstanceId` to `POST /api/agents/enroll`. |
| `agent.session.json` | Written after successful enroll: `agentId`, `accessToken`, `restaurantId`, `expiresAtUtc`. |

**Deployment:** Copy or generate `agent.json` in this folder (not next to the executable). The repository still ships a template `agent.json` in the Worker project output for convenience—operators must place the effective file under `ProgramData` (or use an installer).

**Permissions:** The Windows service account must have read/write access to this folder (set ACLs in the installer).

## Runtime flow

1. **`AgentEnrollmentHostedService`** runs before **`AgentWorker`**.
2. Loads `agent.session.json` if present. If the token is still valid (skew, e.g. 5 minutes), enroll is skipped.
3. Otherwise requires `EnrollmentCode` in `agent.json`; calls **`POST /api/agents/enroll`** with `{ enrollmentCode, clientInstanceId }` (no Bearer).
4. On success, writes `agent.session.json`.
5. **`PrinterAgentAuthHandler`** adds `Authorization: Bearer` from the session (fallback: `BackendJwtToken` for local dev).
6. **Heartbeat**, **Redis stream consumer**, and **print job validation** use `AgentId` and effective `RestaurantId` from the session (with optional config fallback for `RestaurantId`).

## Backend (`QR_Restaurant_backend`)

### Enrollment API

- **`POST /api/agents/enroll`** (anonymous): body `enrollmentCode` (10 alphanumeric characters), `clientInstanceId` (GUID).
- Response: `agentId`, `accessToken`, `restaurantId`, `expiresAtUtc`.
- Codes are stored with a **globally unique** `LookupHash` (HMAC of normalized code with configured pepper). Restaurant is read from the matching row.
- **Rate limiting:** Redis-backed middleware (`AgentEnrollRedisRateLimitMiddleware`) uses an atomic `INCR` + `EXPIRE` script per client IP; options in `EnrollRateLimit` (`PermitLimit`, `WindowSeconds`). Additional ASP.NET rate limiter policy may apply on the same route.
- **EF migration** `PrinterAgentEnrollmentGlobalLookup`: replaces composite unique index on `(RestaurantId, LookupHash)` with unique `LookupHash`; existing enrollment rows are deleted (algorithm change).

### Manager API

- Authenticated routes under the restaurant admin area to **create**, **list**, and **revoke** enrollment codes.

## Frontend (`QRFE`)

- Manager **Settings** includes printer-agent enrollment: generate code (shown once), list metadata, revoke. UI copy reflects **10-character** codes.

## Security notes

- Enrollment endpoint is sensitive: rate limits, short expiry, limited uses, revocation.
- Session and token files on disk are plain JSON in v1; optional hardening (e.g. Windows DPAPI) can be added later.

## Operations

- Backend production steps: see the backend repository `docs/PRODUCTION_BACKEND_CHECKLIST.md` (EF migrate, Redis, pepper, regen codes).
- Agent E2E: [E2E_AGENT_DEPLOYMENT_CHECKLIST.md](E2E_AGENT_DEPLOYMENT_CHECKLIST.md).
- Scripts (run from `Printer-Agent/scripts`): `Setup-ProgramData.ps1` (ACL + optional template copy), `Install-UrsPrinterAgent.ps1` (Windows service).

## Session file protection (Windows)

- On Windows, new saves of `agent.session.json` store the JWT as **DPAPI** (`LocalMachine`) in field `accessTokenProtected` instead of plaintext `accessToken`.
- Legacy files with plaintext `accessToken` still load; new saves use protection when supported.

## Heartbeat 401 and re-enroll

See [TOKEN_EXPIRY_AND_REENROLL.md](TOKEN_EXPIRY_AND_REENROLL.md).

## Future work (non-blocking)

- Optional **setup wizard** or installer page to capture the enrollment code and write `agent.json` / trigger enroll without manual file edit.
