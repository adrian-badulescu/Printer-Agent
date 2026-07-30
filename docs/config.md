# Printer Agent — config & release version

## Versiune release (3 locuri)

Înainte de push / tag pentru un feature release, versiunea trebuie actualizată **în același commit** în:

| # | Fișier | Format exemplu |
|---|--------|----------------|
| 1 | [`PrinterAgent.Worker/agent.json`](../PrinterAgent.Worker/agent.json) → `Version` | `1.5.14` |
| 2 | [`PrinterAgent.Bundle/Bundle.wxs`](../PrinterAgent.Bundle/Bundle.wxs) → `Bundle Version=` | `1.5.14.0` |
| 3 | [`PrinterAgent.Installer/Package.wxs`](../PrinterAgent.Installer/Package.wxs) → `Package Version=` | `1.5.14.0` |

Tag git: `v1.5.14` (prefix `v` + același număr ca în `agent.json`).

### Bump automat (recomandat)

Nu edita manual cele 3 fișiere. Rulează din rădăcina repo:

```powershell
.\scripts\Bump-ReleaseVersion.ps1 -Version 1.5.14
```

Scriptul actualizează `agent.json`, `Bundle.wxs` și `Package.wxs`, apoi verifică alinierea cu `Assert-ReleaseVersionAlignment.ps1`.

### Verificare

```powershell
.\scripts\Assert-ReleaseVersionAlignment.ps1
# sau, cu tag:
.\scripts\Assert-ReleaseVersionAlignment.ps1 -TagVersion 1.5.14
```

Detalii publicare: [`RELEASING.md`](../RELEASING.md).

## Workflow agent (feature → release)

Când se termină un feature care merită installer nou:

1. `.\scripts\Bump-ReleaseVersion.ps1 -Version X.Y.Z` (patch +1 față de ultima versiune din `agent.json`, dacă nu e specificat altfel)
2. Commit cu schimbările de feature **și** cele 3 fișiere de versiune
3. Push pe `main`
4. `git tag vX.Y.Z` + `git push origin vX.Y.Z` pentru GitHub Release + auto-update
