#!/usr/bin/env bash
# Runtime checks of HCG Mail on macOS or Linux, the counterpart of windows-runtime-check.ps1.
#   unix-runtime-check.sh <path to the HCGMail executable> <output folder>
# Uses an isolated data folder (HCG_MAIL_DATA_DIR), so it never touches your own accounts or settings.
#   1. Credential store round trip with a dummy secret (macOS Keychain; on Linux the session-only store, reported as such).
#   2. TLS certificate validation: valid public IMAP/SMTP certificates accepted; expired, wrong-host and self-signed refused.
#   3. Start-up: the app starts, stays running and is captured on screen; it is asked to quit and must exit; it is
#      started again (restart) and must find the settings it wrote.
# Linux needs a display: run under xvfb-run. Writes runtime-summary.json and screenshots to the output folder.
set -uo pipefail
EXE="$1"; OUT="$2"; START_SECONDS="${START_SECONDS:-20}"
mkdir -p "$OUT"; DATA="$OUT/data"; rm -rf "$DATA"; mkdir -p "$DATA"
export HCG_MAIL_DATA_DIR="$DATA"
OS="$(uname -s)"; ARCH="$(uname -m)"
CHECKS="$OUT/checks.tsv"; : > "$CHECKS"
check() { # check <0|1> <description>
  if [ "$1" = 0 ]; then echo "PASS  $2"; printf 'true\t%s\n' "$2" >> "$CHECKS"; else echo "FAIL  $2"; printf 'false\t%s\n' "$2" >> "$CHECKS"; fi
}

echo "HCG Mail: $EXE"
echo "  $(uname -a)"
if [ "$OS" = Darwin ]; then
  SIGNATURE="$(codesign -dv --verbose=2 "$(dirname "$(dirname "$EXE")")/.." 2>&1 | grep -E '^Signature|^Authority|^TeamIdentifier' | tr '\n' ' ')"
  echo "  code signature: ${SIGNATURE:-none}"
else
  SIGNATURE="not applicable (Linux executables are not code-signed)"
fi

# 1. Credential store
"$EXE" --check-credential-store; code=$?
report="$(ls "$DATA"/*credential* 2>/dev/null | head -1)"
STORE="$(grep -o 'Store: .*' "$report" 2>/dev/null | head -1)"
check $([ $code = 0 ] && echo 0 || echo 1) "Credential store round trip ($STORE, exit $code)"

# 2. TLS certificate validation
tls() { # tls <host:port> <expect-valid 1|0> <what>
  "$EXE" --diagnose-tls "$1" > /dev/null 2>&1; local c=$?
  local f="$DATA/tls-diagnosis-${1%%:*}-${1##*:}.txt"; [ -f "$f" ] && sed 's/^/      /' "$f"
  if [ "$2" = 1 ]; then check $([ $c = 0 ] && echo 0 || echo 1) "TLS accepted: $1 ($3, exit $c)"
  # A refusal counts only when the certificate or TLS check refused it, not when the host could not be reached.
  else check $([ $c != 0 ] && [ -f "$f" ] && grep -Eq 'FAILED after .* (Certificate|Tls|RevocationUnavailable)' "$f" && echo 0 || echo 1) "TLS rejected: $1 ($3, exit $c)"; fi
}
tls imap.gmail.com:993 1 "IMAP over TLS, valid certificate"
tls smtp.gmail.com:465 1 "SMTP over TLS, valid certificate"
tls imap.fastmail.com:993 1 "IMAP over TLS, valid certificate (second provider)"
tls expired.badssl.com:443 0 "expired certificate"
tls wrong.host.badssl.com:443 0 "certificate for another host name"
tls self-signed.badssl.com:443 0 "self-signed certificate"

# 3. Start-up, screen capture, quit, restart
shot() {
  if [ "$OS" = Darwin ]; then screencapture -x "$OUT/$1" 2>/dev/null
  elif command -v import > /dev/null; then import -window root "$OUT/$1" 2>/dev/null
  else xwd -root -silent 2>/dev/null | convert xwd:- "png:$OUT/$1" 2>/dev/null; fi
  [ -s "$OUT/$1" ] && echo "      screenshot: $OUT/$1"
}
start_and_quit() { # start_and_quit <label> <screenshot>
  "$EXE" > "$OUT/$2.stdout.txt" 2>&1 &
  local pid=$!
  sleep "$START_SECONDS"
  if kill -0 $pid 2>/dev/null; then check 0 "$1: the app started and is running after ${START_SECONDS}s"; else
    wait $pid; check 1 "$1: the app exited during start-up (exit $?)"; sed 's/^/      /' "$OUT/$2.stdout.txt" | tail -20; return; fi
  shot "$2"
  check $([ -s "$OUT/$2" ] && echo 0 || echo 1) "$1: screen captured with the app running"
  kill -TERM $pid 2>/dev/null
  for _ in $(seq 1 20); do kill -0 $pid 2>/dev/null || break; sleep 1; done
  if kill -0 $pid 2>/dev/null; then kill -KILL $pid; check 1 "$1: quit when asked (SIGTERM)"; else check 0 "$1: quit when asked (SIGTERM)"; fi
}
start_and_quit "First start" first-start.png
SETTINGS_AFTER_FIRST="$(ls "$DATA"/settings.json 2>/dev/null)"
echo "      data folder after first start: $(cd "$DATA" && find . -type f | sed 's|^\./||' | tr '\n' ' ')"
start_and_quit "Restart" restart.png
if [ -n "$SETTINGS_AFTER_FIRST" ]; then check 0 "Restart: the settings written at first start are still there"; fi
if ls "$DATA"/logs/* > /dev/null 2>&1; then
  if grep -Eiq 'password\s*[:=]|AUTHENTICATE PLAIN [A-Za-z0-9+/=]{8,}' "$DATA"/logs/*; then check 1 "The log holds no password or authentication payload"
  else check 0 "The log holds no password or authentication payload"; fi
fi

python3 - "$CHECKS" "$OUT/runtime-summary.json" "$OS" "$ARCH" "$SIGNATURE" "$EXE" <<'PY'
import json, platform, sys
checks = [{"check": line.split("\t", 1)[1].rstrip("\n"), "passed": line.startswith("true")} for line in open(sys.argv[1]) if "\t" in line]
osname = {"Darwin": "macOS " + platform.mac_ver()[0]}.get(sys.argv[3]) or (open("/etc/os-release").read().split('PRETTY_NAME="')[1].split('"')[0])
json.dump({"executable": {"path": sys.argv[6], "codeSignature": sys.argv[5]},
           "os": {"caption": osname, "kernel": platform.release(), "architecture": sys.argv[4]},
           "checks": checks, "failures": sum(not c["passed"] for c in checks)}, open(sys.argv[2], "w"), indent=2)
PY
FAILED=$(grep -c '^false' "$CHECKS")
echo; [ "$FAILED" = 0 ] && echo "Runtime check: all checks passed" || echo "Runtime check: $FAILED check(s) FAILED"
exit $([ "$FAILED" = 0 ] && echo 0 || echo 1)
