# Dist output

Run from repo root (Linux CI / this box):

```bash
./scripts/publish-win-x64.sh
```

Produces:

- `dist/win-x64/` — self-contained win-x64 publish (UnifiedRgb.App.exe + deps)
- `dist/UnifiedRgb-win-x64.zip` — zip of publish + install scripts
- `dist/Install-UnifiedRgb.ps1` / `Uninstall-UnifiedRgb.ps1`

On Windows, after unzipping:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-UnifiedRgb.ps1 -SourceDir .\win-x64
```

This Linux agent can cross-publish the .exe payload; a full Inno/Velopack `.exe` setup is optional and Windows-only (see main README).
