# Printer Agent — release pe GitHub

## Cerințe

- Runner **self-hosted** Windows cu etichete implicite `self-hosted`, `Windows`, `X64` (aplicația GitHub Actions Runner pe PC-ul de build).
- [.NET SDK 10](https://dotnet.microsoft.com/download) instalat pe runner (workflow folosește și `actions/setup-dotnet`).

## Cum publici o versiune nouă

1. Actualizează versiunea în `PrinterAgent.Worker.csproj` (și în backend `PrinterAgent:LatestVersion` dacă folosești update automat).
2. Creează și împinge un tag semver:
   ```bash
   git tag v1.0.6
   git push origin v1.0.6
   ```
3. Workflow-ul **Release Printer Agent** rulează, publică `URS-PrinterAgent-win-x64.zip` pe release-ul asociat tag-ului.

## Link stabil „ultima versiune”

Dacă fiecare release include un asset cu **același nume** `URS-PrinterAgent-win-x64.zip`, URL-ul rămâne mereu:

`https://github.com/<OWNER>/<REPO>/releases/latest/download/URS-PrinterAgent-win-x64.zip`

Înlocuiește `<OWNER>` și `<REPO>` cu repo-ul tău și pune acest URL în frontend (`environment.prod.ts` → `printerAgentDownloadUrl`).

## Etichete runner

Dacă runnerul tău nu are `Windows` / `X64`, editează `runs-on:` din `.github/workflows/release-printer-agent.yml` ca să se potrivească cu etichetele din GitHub → Settings → Actions → Runners.

---

## Workflow CI

Definiția este în repo: [`.github/workflows/release-printer-agent.yml`](./.github/workflows/release-printer-agent.yml).

## QRFE (frontend)

După primul release GitHub, setează în `environment.prod.ts` proprietatea `printerAgentDownloadUrl` la URL-ul `.../releases/latest/download/URS-PrinterAgent-win-x64.zip` al repo-ului tău (dacă org/repo diferă de valoarea implicită din cod).
