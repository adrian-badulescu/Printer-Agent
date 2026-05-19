# Token expiry and re-enrollment

## Current behavior

- Printer-agent JWTs issued at enroll expire after the period configured on the backend (e.g. ~30 days).
- Renewal uses **`POST /api/agents/refresh`** when `agent.session.json` still has a valid refresh token.
- If the session file is missing but the device is still registered on the backend, **`POST /api/agents/enroll`** with a fresh manager-issued code **re-issues tokens** for the same `clientInstanceId` (same restaurant) instead of returning “already enrolled”.

## Agent behavior on 401 (heartbeat)

When `POST /api/agents/heartbeat` returns **401 Unauthorized**:

1. The agent logs `URS_Metric HeartbeatUnauthorized` with the `agentId`.
2. **`agent.session.json` is deleted** (`IAgentSessionStore.ClearSessionAsync`). **`client.instance` is kept** so the same device GUID can be used for the next enroll if the backend allows it.
3. Until the service is restarted with a valid **`EnrollmentCode`** in `agent.json` (or a restored session file), heartbeats will not authenticate; print and other API calls may also fail.

**Operator action:** add a fresh enrollment code to `%ProgramData%\URSPrinterAgent\agent.json`, then **restart** the Windows service so `AgentEnrollmentHostedService` runs enroll again.

## Future option: refresh endpoint

A dedicated refresh or silent renew flow would require backend design (e.g. long-lived refresh token, or re-signing with a device secret) and is out of scope for the current release.
