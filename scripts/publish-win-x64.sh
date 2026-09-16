#!/usr/bin/env bash
# Publish self-contained win-x64 Avalonia build into dist/win-x64
# and zip it for the PowerShell installer.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/dist/win-x64"
ZIP="$ROOT/dist/UnifiedRgb-win-x64.zip"

cd "$ROOT"
dotnet restore UnifiedRgb.sln
dotnet build UnifiedRgb.sln -c Release --no-restore
dotnet publish src/UnifiedRgb.App/UnifiedRgb.App.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=false \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$OUT"

# Bundle installer scripts next to the publish output for convenience
mkdir -p "$ROOT/dist"
cp -f "$ROOT/installer/Install-UnifiedRgb.ps1" "$ROOT/dist/"
cp -f "$ROOT/installer/Uninstall-UnifiedRgb.ps1" "$ROOT/dist/"

rm -f "$ZIP"
( cd "$ROOT/dist" && zip -qr "UnifiedRgb-win-x64.zip" win-x64 Install-UnifiedRgb.ps1 Uninstall-UnifiedRgb.ps1 )

echo ""
echo "Published: $OUT"
echo "Zip:       $ZIP"
echo "On Windows: Expand the zip, then:"
echo "  powershell -ExecutionPolicy Bypass -File .\\Install-UnifiedRgb.ps1 -SourceDir .\\win-x64"
