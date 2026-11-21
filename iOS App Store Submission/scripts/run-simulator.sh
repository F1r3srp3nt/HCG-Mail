#!/usr/bin/env bash
# Installs and starts HCG Mail in an iOS Simulator.
#   scripts/run-simulator.sh                         first available iPhone, normal start
#   scripts/run-simulator.sh "iPad Air 11-inch (M3)" choose a device by name (xcrun simctl list devices)
#   scripts/run-simulator.sh --ipad                  first available iPad
#   scripts/run-simulator.sh --preview               Debug builds: sample mailbox, no network (for screenshots)
#   scripts/run-simulator.sh --self-test             Keychain / sandbox / sanitizer checks; exits non-zero on failure
#   scripts/run-simulator.sh --screenshot out.png    save a screenshot after start-up
#   scripts/run-simulator.sh --preview --screen reader|compose   preview only: open that screen (store screenshots)
# Never signs in to or sends from a real account.
source "$(dirname "$0")/_common.sh"
require_macos

DEVICE=""; FAMILY="iPhone"; PREVIEW=0; SELFTEST=0; SHOT=""; SCREEN=""; CONFIG="${HCG_IOS_CONFIGURATION:-Debug}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --ipad) FAMILY="iPad" ;;
    --preview) PREVIEW=1 ;;
    --self-test) SELFTEST=1 ;;
    --screenshot) SHOT="$2"; shift ;;
    --screen) SCREEN="$2"; shift ;;
    *) DEVICE="$1" ;;
  esac
  shift
done

APP="${HCG_IOS_APP:-$("$IOS_ROOT/scripts/build-simulator.sh" "$CONFIG" | tail -1)}"

if [[ -z "$DEVICE" ]]; then
  DEVICE="$(xcrun simctl list devices available -j | FAMILY="$FAMILY" python3 -c 'import json,os,sys; d=json.load(sys.stdin)["devices"]; print(next(x["name"] for r in sorted(d, reverse=True) if "iOS" in r for x in d[r] if x["name"].startswith(os.environ["FAMILY"])))')"
fi
UDID="$(xcrun simctl list devices available -j | DEVICE="$DEVICE" python3 -c 'import json,os,sys; d=json.load(sys.stdin)["devices"]; print(next(x["udid"] for r in sorted(d, reverse=True) if "iOS" in r for x in d[r] if x["name"]==os.environ["DEVICE"]))')"
echo "Simulator: $DEVICE ($UDID)"

xcrun simctl boot "$UDID" 2>/dev/null || true
xcrun simctl bootstatus "$UDID" -b >/dev/null
xcrun simctl install "$UDID" "$APP"

export SIMCTL_CHILD_HCG_MAIL_PREVIEW="$PREVIEW"
export SIMCTL_CHILD_HCG_MAIL_SELF_TEST="$SELFTEST"
export SIMCTL_CHILD_HCG_MAIL_PREVIEW_SCREEN="$SCREEN"

if [[ $SELFTEST -eq 1 ]]; then
  LOG="$(mktemp)"
  xcrun simctl terminate "$UDID" "$BUNDLE_ID" 2>/dev/null || true
  # --console-pty streams the app's stdout; stop as soon as the verdict is printed (or after 3 minutes).
  ( xcrun simctl launch --console-pty --terminate-running-process "$UDID" "$BUNDLE_ID" > "$LOG" 2>&1 & echo $! > "$LOG.pid" )
  for _ in $(seq 1 180); do
    grep -q 'HCG-SELFTEST: \(PASS\|FAIL\)' "$LOG" && break
    sleep 1
  done
  kill "$(cat "$LOG.pid")" 2>/dev/null || true
  grep 'HCG-SELFTEST' "$LOG" || true
  [[ -n "$SHOT" ]] && xcrun simctl io "$UDID" screenshot "$SHOT"
  grep -q 'HCG-SELFTEST: PASS' "$LOG"
  exit $?
fi

if [[ -n "$SCREEN" && -n "$SHOT" ]]; then
  # Store screenshots: the app prints "HCG-PREVIEW: ready <screen>" once that screen is open (sample mailbox loaded);
  # wait for it instead of a fixed delay, which is not enough on slow build machines.
  LOG="$(mktemp)"
  xcrun simctl terminate "$UDID" "$BUNDLE_ID" 2>/dev/null || true
  ( xcrun simctl launch --console-pty --terminate-running-process "$UDID" "$BUNDLE_ID" > "$LOG" 2>&1 & echo $! > "$LOG.pid" )
  for _ in $(seq 1 180); do
    grep -q 'HCG-PREVIEW: \(ready\|not ready\)' "$LOG" && break
    sleep 1
  done
  grep 'HCG-PREVIEW' "$LOG" || echo "HCG-PREVIEW: no answer from the app within 3 minutes"
  sleep 2   # let the last frame render
  xcrun simctl io "$UDID" screenshot "$SHOT"
  kill "$(cat "$LOG.pid")" 2>/dev/null || true
  echo "Screenshot: $SHOT"
  grep -q "HCG-PREVIEW: ready $SCREEN" "$LOG"
  exit $?
fi

xcrun simctl launch --terminate-running-process "$UDID" "$BUNDLE_ID"
if [[ -n "$SHOT" ]]; then
  sleep "${HCG_IOS_SCREENSHOT_DELAY:-12}"
  xcrun simctl io "$UDID" screenshot "$SHOT"
  echo "Screenshot: $SHOT"
fi
