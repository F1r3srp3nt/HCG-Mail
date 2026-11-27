#!/bin/bash
# Screenshots of the real HCG Mail Release app for the HCG Store product page, on this Mac:
#   capture-screenshots.sh "<HCG Mail.app>" <output folder>
# Starts the app with an isolated, empty data folder (no account, no message, nothing personal) once in each theme
# (HCG Dark = HcgFuture, HCG Light = HcgClassic), captures only HCG Mail's window (with its shadow removed) and quits.
# Writes hcg-mail-macos-<theme>.png and screenshots.json (macOS version, architecture, app version, window size).
set -uo pipefail
APP="$1"; OUT="$2"; mkdir -p "$OUT"; OUT="$(cd "$OUT" && pwd)"
EXE="$APP/Contents/MacOS/$(plutil -extract CFBundleExecutable raw -o - "$APP/Contents/Info.plist")"
W="$(mktemp -d "${TMPDIR:-/tmp}/hcg-mail-shots.XXXXXX")"; trap 'rm -rf "$W"' EXIT
cat > "$W/window.swift" <<'SWIFT'
import CoreGraphics
// The largest normal (layer 0) on-screen window of the given process: its window number and size.
let pid = Int32(CommandLine.arguments[1])!
let info = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] ?? []
var best = (id: 0, w: 0.0, h: 0.0)
for w in info {
    guard (w[kCGWindowOwnerPID as String] as? Int32) == pid, (w[kCGWindowLayer as String] as? Int) == 0,
          let b = w[kCGWindowBounds as String] as? [String: Any] else { continue }
    let width = (b["Width"] as? Double) ?? 0, height = (b["Height"] as? Double) ?? 0
    if width * height > best.w * best.h { best = ((w[kCGWindowNumber as String] as? Int) ?? 0, width, height) }
}
print("\(best.id) \(Int(best.w))x\(Int(best.h))")
SWIFT
swiftc -O -o "$W/window" "$W/window.swift" 2> "$OUT/swiftc.txt" || echo "window finder not built (see swiftc.txt): whole-screen captures instead"

results=""
for pair in "dark:0" "light:1"; do
  theme="${pair%%:*}"; value="${pair##*:}"
  data="$W/data-$theme"; mkdir -p "$data"
  printf '{"version":1,"theme":%s}\n' "$value" > "$data/settings.json"
  HCG_MAIL_DATA_DIR="$data" "$EXE" > "$OUT/app-$theme.log" 2>&1 &
  pid=$!
  sleep 20
  if ! kill -0 $pid 2>/dev/null; then echo "HCG Mail exited before the $theme screenshot"; tail -20 "$OUT/app-$theme.log"; continue; fi
  osascript -e "tell application \"System Events\" to set frontmost of (first process whose unix id is $pid) to true" > /dev/null 2>&1 || true
  sleep 2
  win=""; size=""
  [ -x "$W/window" ] && read -r win size < <("$W/window" $pid)
  file="$OUT/hcg-mail-macos-$theme.png"
  if [ -n "$win" ] && [ "$win" != 0 ]; then screencapture -x -o -l "$win" "$file"; else screencapture -x "$file"; size="whole screen"; fi
  kill -TERM $pid 2>/dev/null; for _ in $(seq 1 15); do kill -0 $pid 2>/dev/null || break; sleep 1; done; kill -KILL $pid 2>/dev/null
  if [ -s "$file" ]; then echo "screenshot: $file ($size)"; results="$results{\"theme\":\"$theme\",\"file\":\"$(basename "$file")\",\"window\":\"$size\"},"; fi
done
version="$(LC_ALL=C grep -a -o '1\.[0-9]*\.[0-9]*-[a-z]*\.[0-9]*' "$EXE" | sort | uniq -c | sort -rn | head -1 | awk '{print $2}')"
printf '{"macos":"%s","machine":"%s","appVersion":"%s","data":"empty isolated data folder: no account, no messages","screenshots":[%s]}\n' \
  "$(sw_vers -productVersion)" "$(uname -m)" "$version" "${results%,}" > "$OUT/screenshots.json"
cat "$OUT/screenshots.json"
[ -n "$results" ]
