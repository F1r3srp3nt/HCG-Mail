#!/usr/bin/env bash
# Builds the self-contained Windows x64 test build of HCG Mail from macOS or Linux.
# Output: artifacts/publish/win-x64/HCGMail.exe (single file, no .NET install needed on the PC)
#         artifacts/HCGMail-<version>-win-x64.zip
# The build is NOT code-signed: Windows SmartScreen will warn ("Windows protected your PC" →
# More info → Run anyway). Production signing requires a certificate and founder approval.
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION=$(sed -n 's:.*<InformationalVersion>\(.*\)</InformationalVersion>.*:\1:p' Directory.Build.props)
OUT=artifacts/publish/win-x64
rm -rf "$OUT"
dotnet publish src/HCG.Mail.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "$OUT"
ZIP="artifacts/HCGMail-${VERSION}-win-x64.zip"
rm -f "$ZIP"
(cd "$OUT" && zip -q -9 "../../../$ZIP" HCGMail.exe)
if command -v shasum >/dev/null; then shasum -a 256 "$ZIP" "$OUT/HCGMail.exe"; else sha256sum "$ZIP" "$OUT/HCGMail.exe"; fi
echo "Windows build ready: $ZIP"
