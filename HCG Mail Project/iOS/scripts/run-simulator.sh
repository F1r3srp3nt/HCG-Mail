#!/usr/bin/env bash
# Installs and starts HCG Mail in an iOS Simulator.
#   scripts/run-simulator.sh                         newest iPhone Pro (Dynamic Island), normal start
#   scripts/run-simulator.sh "iPad Air 11-inch (M3)" choose a device by name (xcrun simctl list devices)
#   scripts/run-simulator.sh --ipad                  first available iPad
#   scripts/run-simulator.sh --preview               Debug builds: sample mailbox, no network (for screenshots)
#   scripts/run-simulator.sh --self-test             Keychain / sandbox / sanitizer checks; exits non-zero on failure
#   scripts/run-simulator.sh --screenshot out.png    save a screenshot after start-up
#   scripts/run-simulator.sh --preview --screen inbox|reader|compose   preview only: open that screen, wait until the
#                                                    app reports it is open, then take the screenshot (store screenshots)
#   scripts/run-simulator.sh --exclusive …           shut down every other booted simulator first (CI: two booted
#                                                    simulators on a small build machine can stall the second boot)
#   scripts/run-simulator.sh --boot-only …           only boot the simulator (and stop there)
#   scripts/run-simulator.sh --show …                open Simulator.app on this device once it has booted
# Without a device name it uses a simulator that is already booted (or booting), if one of the right kind is, rather
# than boot a second one; otherwise the first available one.
# Never signs in to or sends from a real account.
#
# Every stage (find, boot, install, launch, ready, screenshot) has its own time limit and is logged with a timestamp
# ("HCG-SIM: <stage> …"). When a stage fails or runs out of time the script prints the simulator's state, the
# CoreSimulator logs and the machine's load and memory, stops what it started, and exits non-zero.
# Time limits in seconds (environment): HCG_IOS_FIND_TIMEOUT (300: listing the simulators took ~100 s on a busy CI
# runner), HCG_IOS_BOOT_TIMEOUT (1500: a cold boot on a hosted CI runner has taken ~18 minutes), HCG_IOS_INSTALL_TIMEOUT (300), HCG_IOS_LAUNCH_TIMEOUT (180), HCG_IOS_READY_TIMEOUT (180),
# HCG_IOS_SCREENSHOT_TIMEOUT (120). HCG_IOS_SIM_LOG_DIR keeps each stage's full output (default: a temporary folder).
source "$(dirname "$0")/_common.sh"
require_macos

DEVICE=""; FAMILY="iPhone"; PREVIEW=0; SELFTEST=0; SHOT=""; SCREEN=""; EXCLUSIVE=0; BOOT_ONLY=0; SHOW=0
CONFIG="${HCG_IOS_CONFIGURATION:-Debug}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --ipad) FAMILY="iPad" ;;
    --preview) PREVIEW=1 ;;
    --self-test) SELFTEST=1 ;;
    --screenshot) SHOT="$2"; shift ;;
    --screen) SCREEN="$2"; shift ;;
    --exclusive) EXCLUSIVE=1 ;;
    --boot-only) BOOT_ONLY=1 ;;
    --show) SHOW=1 ;;
    *) DEVICE="$1" ;;
  esac
  shift
done

FIND_TIMEOUT="${HCG_IOS_FIND_TIMEOUT:-300}"
BOOT_TIMEOUT="${HCG_IOS_BOOT_TIMEOUT:-1500}"
INSTALL_TIMEOUT="${HCG_IOS_INSTALL_TIMEOUT:-300}"
LAUNCH_TIMEOUT="${HCG_IOS_LAUNCH_TIMEOUT:-180}"
READY_TIMEOUT="${HCG_IOS_READY_TIMEOUT:-180}"
SHOT_TIMEOUT="${HCG_IOS_SCREENSHOT_TIMEOUT:-120}"
LOG_DIR="${HCG_IOS_SIM_LOG_DIR:-$(mktemp -d -t hcg-sim)}"
mkdir -p "$LOG_DIR"
UDID=""; STAGE="start"; STAGE_LOG="/dev/null"; STAGE_START=$SECONDS; LIMIT=0
BACKGROUND=()   # processes this script started and must stop

# --- cleanup: whatever happens, leave no helper process behind ---------------------------------------------------
stop_process() {   # the process and its children
  local pid="$1"
  kill -0 "$pid" 2>/dev/null || return 0
  pkill -TERM -P "$pid" 2>/dev/null || true
  kill -TERM "$pid" 2>/dev/null || true
  for _ in 1 2 3 4 5; do kill -0 "$pid" 2>/dev/null || return 0; sleep 1; done
  pkill -KILL -P "$pid" 2>/dev/null || true
  kill -KILL "$pid" 2>/dev/null || true
}
cleanup() {
  local pid
  for pid in ${BACKGROUND[@]+"${BACKGROUND[@]}"}; do stop_process "$pid"; done
}
trap cleanup EXIT
trap 'echo "HCG-SIM: interrupted during $STAGE" >&2; exit 130' INT TERM

# --- stages ------------------------------------------------------------------------------------------------------
now() { date -u '+%H:%M:%S'; }
stage() {   # stage NAME LIMIT
  STAGE="$1"; LIMIT="$2"; STAGE_START=$SECONDS
  STAGE_LOG="$LOG_DIR/$(echo "${DEVICE:-device}" | tr -cd '[:alnum:]')-$1.log"
  : > "$STAGE_LOG"
  echo "[$(now)] HCG-SIM: $STAGE (limit ${LIMIT} s)"
}
stage_done() { echo "[$(now)] HCG-SIM: $STAGE done in $((SECONDS - STAGE_START)) s"; }

# Runs a command (or shell function) in the background with its output in the stage log; returns its exit status,
# or 124 if it is still running after LIMIT seconds (it is then stopped). Prints a heartbeat once a minute.
bounded() {
  "$@" >> "$STAGE_LOG" 2>&1 &
  local pid=$! start=$SECONDS beat=60
  BACKGROUND+=("$pid")
  while kill -0 "$pid" 2>/dev/null; do
    if (( SECONDS - start >= LIMIT )); then stop_process "$pid"; wait "$pid" 2>/dev/null || true; return 124; fi
    sleep 1
    if (( SECONDS - start >= beat )); then echo "[$(now)] HCG-SIM: $STAGE still running after $((SECONDS - start)) s"; beat=$((beat + 60)); fi
  done
  wait "$pid"
}

# A diagnostic command that itself must not hang (CoreSimulator can be the thing that is stuck).
probe() { local limit="$1"; shift; "$@" 2>&1 & local pid=$!; BACKGROUND+=("$pid")
  local w=0; while kill -0 "$pid" 2>/dev/null; do (( w >= limit )) && { stop_process "$pid"; echo "(no answer within ${limit} s)"; return 0; }; sleep 1; w=$((w + 1)); done
  wait "$pid" || true; }

diagnose() {
  [[ -n "${GITHUB_ACTIONS:-}" ]] && echo "::group::HCG-SIM diagnostics ($STAGE)"
  echo "--- output of $STAGE (last 40 lines; full log: $STAGE_LOG)"; tail -40 "$STAGE_LOG" 2>/dev/null || true
  echo "--- this simulator"; [[ -n "$UDID" ]] && probe 30 xcrun simctl list devices | grep -F "$UDID" || true
  echo "--- booted simulators"; probe 30 xcrun simctl list devices booted
  echo "--- load and memory"; uptime; probe 10 memory_pressure -Q | tail -3; probe 10 vm_stat | head -6
  echo "--- simulator processes (top CPU)"; ps -Ao pid,pcpu,rss,etime,comm | grep -iE 'simulator|launchd_sim|CoreSimulator|HCGMail|simctl' | sort -k2 -nr 2>/dev/null | head -15 || true
  if [[ -n "$UDID" && -f "$HOME/Library/Logs/CoreSimulator/$UDID/system.log" ]]; then
    echo "--- device system.log (last 30 lines)"; tail -30 "$HOME/Library/Logs/CoreSimulator/$UDID/system.log"
  fi
  if [[ -f "$HOME/Library/Logs/CoreSimulator/CoreSimulator.log" ]]; then
    # Without the "device identity" errors CoreSimulatorService logs continuously on hosted runners.
    echo "--- CoreSimulator.log (last 30 other lines)"
    tail -3000 "$HOME/Library/Logs/CoreSimulator/CoreSimulator.log" | grep -v 'Failed to get a device identity' | tail -30 || true
  fi
  [[ -n "${GITHUB_ACTIONS:-}" ]] && echo "::endgroup::"
  return 0
}

fail() {   # fail STATUS [WHAT]
  local status="$1" what="${2:-}"
  if [[ "$status" -eq 124 ]]; then
    echo "[$(now)] HCG-SIM: $STAGE TIMED OUT after ${LIMIT} s${what:+ ($what)}" >&2
  else
    echo "[$(now)] HCG-SIM: $STAGE FAILED (exit $status)${what:+: $what}" >&2
  fi
  diagnose >&2
  exit 1
}

# --- find the app and the simulator ------------------------------------------------------------------------------
APP="${HCG_IOS_APP:-$("$IOS_ROOT/scripts/build-simulator.sh" "$CONFIG" | tail -1)}"

stage find "$FIND_TIMEOUT"
devices_json="$LOG_DIR/devices.json"
bounded sh -c 'xcrun simctl list devices available -j > "$0"' "$devices_json" || fail $? "xcrun simctl list devices"
# Without a device name: an iPhone is a standard numbered model (not, for example, the two-display "iPhone Duo" of
# the iOS 27.2 runtime, which simctl cannot screenshot without --display), preferably a Pro with the Dynamic Island,
# the newest model first; a device that is already booted is reused. Runtimes are tried newest first.
pick() { FAMILY="$FAMILY" DEVICE="$DEVICE" python3 -I -c '
import json, os, re, sys
d = json.load(open(sys.argv[1]))["devices"]
want = os.environ["DEVICE"]
devices = [x for r in sorted(d, reverse=True) if "iOS" in r for x in d[r]]
if want:
    match = [x for x in devices if x["name"] == want]
else:
    family = [x for x in devices if x["name"].startswith(os.environ["FAMILY"])]
    def rank(x):
        name, booted = x["name"], x.get("state") in ("Booted", "Booting")
        if os.environ["FAMILY"] != "iPhone":
            return (booted,)
        standard = re.fullmatch(r"iPhone \d+(e| Plus| Pro| Pro Max)?", name) is not None
        number = int(re.search(r"\d+", name).group()) if standard else -1
        return (standard, booted, name.endswith(" Pro"), number)
    match = sorted(family, key=rank, reverse=True)  # stable: equal ranks keep the newest runtime first
if not match: sys.exit(1)
print(match[0]["name"]); print(match[0]["udid"])' "$devices_json"; }
found="$(pick)" || { echo "No available simulator matches '${DEVICE:-$FAMILY}'. Installed:" >&2; xcrun simctl list devices available >&2; exit 1; }
DEVICE="$(echo "$found" | sed -n 1p)"; UDID="$(echo "$found" | sed -n 2p)"
echo "Simulator: $DEVICE ($UDID)"

if [[ $EXCLUSIVE -eq 1 ]]; then
  stage shutdown-others 120
  others() { xcrun simctl list devices booted -j | UDID="$UDID" python3 -I -c '
import json, os, sys
for r in json.load(sys.stdin)["devices"].values():
    for x in r:
        if x.get("state") == "Booted" and x["udid"] != os.environ["UDID"]: print(x["udid"])'; }
  shutdown_others() { local u; for u in $(others); do echo "shutting down $u"; xcrun simctl shutdown "$u" || true; done; }
  bounded shutdown_others || fail $?
  stage_done
fi

stage boot "$BOOT_TIMEOUT"
boot_device() {
  # "Unable to boot device in current state: Booted" is fine; bootstatus -b waits until it is ready for apps.
  xcrun simctl boot "$UDID" || true
  xcrun simctl bootstatus "$UDID" -b
}
bounded boot_device || fail $? "$DEVICE"
stage_done
if [[ $SHOW -eq 1 ]]; then
  # On the device just booted: Simulator.app then shows it instead of booting its own default device.
  stage show 60
  bounded open -a "${DEVELOPER_DIR:-$(xcode-select -p)}/Applications/Simulator.app" --args -CurrentDeviceUDID "$UDID" || true
  stage_done
fi
[[ $BOOT_ONLY -eq 1 ]] && exit 0

stage install "$INSTALL_TIMEOUT"
bounded xcrun simctl install "$UDID" "$APP" || fail $? "$APP"
stage_done

export SIMCTL_CHILD_HCG_MAIL_PREVIEW="$PREVIEW"
export SIMCTL_CHILD_HCG_MAIL_SELF_TEST="$SELFTEST"
export SIMCTL_CHILD_HCG_MAIL_PREVIEW_SCREEN="$SCREEN"

take_screenshot() {
  stage screenshot "$SHOT_TIMEOUT"
  # On an overloaded machine `simctl io screenshot` occasionally stalls (CI run 45298799853, load average 586): one
  # fresh attempt before failing.
  if ! bounded xcrun simctl io "$UDID" screenshot "$SHOT"; then
    echo "[$(now)] HCG-SIM: screenshot did not finish; one more attempt"
    stage screenshot-retry "$SHOT_TIMEOUT"
    bounded xcrun simctl io "$UDID" screenshot "$SHOT" || fail $? "$SHOT"
  fi
  stage_done
  echo "Screenshot: $SHOT"
}

# Starts the app with its console attached (in the background) and waits for a line matching PATTERN.
# Sets APP_LOG. Returns 0 when the line appeared, 1 if the app exited first, 124 on time-out.
launch_and_wait() {   # launch_and_wait PATTERN
  stage launch "$LAUNCH_TIMEOUT"
  LIMIT=30 bounded xcrun simctl terminate "$UDID" "$BUNDLE_ID" || true
  APP_LOG="$LOG_DIR/$(echo "$DEVICE" | tr -cd '[:alnum:]')-app.log"
  : > "$APP_LOG"
  xcrun simctl launch --console-pty --terminate-running-process "$UDID" "$BUNDLE_ID" > "$APP_LOG" 2>&1 &
  local pid=$! start=$SECONDS
  BACKGROUND+=("$pid")
  # The launch is done once the app says it has started ("HCG-PREVIEW: started", "HCG-SELFTEST: start"). With
  # --console-pty simctl prints no "<bundle id>: <pid>" line; it is accepted too in case a later simctl does.
  until grep -q "HCG-PREVIEW: started\|HCG-SELFTEST: start\|$BUNDLE_ID: [0-9]" "$APP_LOG" 2>/dev/null; do
    kill -0 "$pid" 2>/dev/null || { STAGE_LOG="$APP_LOG"; fail 1 "simctl launch exited"; }
    (( SECONDS - start >= LAUNCH_TIMEOUT )) && { STAGE_LOG="$APP_LOG"; fail 124; }
    sleep 1
  done
  stage_done
  stage ready "$READY_TIMEOUT"
  STAGE_LOG="$APP_LOG"; start=$SECONDS
  until grep -q "$1" "$APP_LOG" 2>/dev/null; do
    kill -0 "$pid" 2>/dev/null || return 1
    (( SECONDS - start >= READY_TIMEOUT )) && return 124
    sleep 1
  done
  stage_done
}

if [[ $SELFTEST -eq 1 ]]; then
  launch_and_wait 'HCG-SELFTEST: \(PASS\|FAIL\)' || fail $? "no self-test verdict from the app"
  grep -E 'HCG-SELFTEST|HCG-STARTUP' "$APP_LOG" || true
  [[ -n "$SHOT" ]] && take_screenshot
  grep -q 'HCG-SELFTEST: PASS' "$APP_LOG" || { echo "HCG-SIM: self-test FAILED" >&2; exit 1; }
  exit 0
fi

if [[ -n "$SCREEN" ]]; then
  # The app prints "HCG-PREVIEW: ready <screen>" once that screen is open (sample mailbox loaded), or
  # "HCG-PREVIEW: not ready …" with the reason.
  [[ $PREVIEW -eq 1 ]] || { echo "error: --screen needs --preview" >&2; exit 2; }
  launch_and_wait 'HCG-PREVIEW: \(ready\|not ready\)' || fail $? "the app did not report the $SCREEN screen"
  grep 'HCG-PREVIEW' "$APP_LOG"
  grep -q "HCG-PREVIEW: ready $SCREEN" "$APP_LOG" || { STAGE_LOG="$APP_LOG"; fail 1 "the $SCREEN screen did not open"; }
  sleep 2   # let the last frame render
  [[ -n "$SHOT" ]] && take_screenshot
  exit 0
fi

stage launch "$LAUNCH_TIMEOUT"
bounded xcrun simctl launch --terminate-running-process "$UDID" "$BUNDLE_ID" || fail $?
cat "$STAGE_LOG"
stage_done
if [[ -n "$SHOT" ]]; then
  sleep "${HCG_IOS_SCREENSHOT_DELAY:-12}"
  take_screenshot
fi
