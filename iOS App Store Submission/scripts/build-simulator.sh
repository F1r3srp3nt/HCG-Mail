#!/usr/bin/env bash
# Builds HCG Mail for the iOS Simulator (no signing identity needed).
#   scripts/build-simulator.sh            Debug (default)
#   scripts/build-simulator.sh Release
# Prints the path of the .app bundle on the last line.
source "$(dirname "$0")/_common.sh"
require_macos
CONFIG="${1:-Debug}"
RID="$(simulator_rid)"
OUT="$OUT_ROOT/simulator/$CONFIG"

extra=()
[[ -n "${HCG_MICROSOFT_CLIENT_ID:-}" ]] && extra+=("-p:HcgMicrosoftClientId=$HCG_MICROSOFT_CLIENT_ID")

dotnet build "$APP_PROJECT" -c "$CONFIG" -f "$TFM" -r "$RID" -o "$OUT" "${extra[@]}" >&2

APP="$(find "$OUT" -maxdepth 1 -name '*.app' -type d | head -1)"
[[ -d "$APP" ]] || { echo "error: no .app bundle in $OUT" >&2; exit 1; }

# The asset catalog must be compiled into the bundle (app icon).
[[ -f "$APP/Assets.car" ]] || { echo "error: $APP has no Assets.car — the AppIcon asset catalog was not compiled" >&2; exit 1; }
echo "$APP"
