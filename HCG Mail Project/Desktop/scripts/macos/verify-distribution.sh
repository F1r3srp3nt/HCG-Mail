#!/bin/bash
# Checks an HCG Mail for macOS download (ZIP, PKG or DMG) the way a customer receives it, on this Mac:
#   verify-distribution.sh <file> <output folder> [--expect developer-id|test] [--install] [--no-launch]
# 1. The file gets the quarantine attribute a browser gives a download (it is never removed).
# 2. Gatekeeper's verdict on the file and on the app inside it, with that quarantine: for a Developer ID release the
#    only accepted answer is "accepted, source=Notarized Developer ID" (spctl, syspolicy_check, stapler).
# 3. Install as the customer would: ZIP extracted with ditto (as Archive Utility does, quarantine carried onto the
#    app), DMG mounted and the app copied out, PKG installed with installer(8) into /Applications (only with --install:
#    it needs administrator rights and replaces /Applications/HCG Mail.app).
# 4. Launch: the installed app is started and quit twice by unix-runtime-check.sh (credential store, TLS validation,
#    start-up, restart) with an isolated data folder, so it never touches existing accounts.
# Writes verification-<file>.json. A test build (ad-hoc) is expected to be rejected by Gatekeeper; that is recorded,
# not counted as passed.
set -uo pipefail
FILE="$1"; OUT="$2"; shift 2
EXPECT=developer-id INSTALL=0 LAUNCH=1
while [ $# -gt 0 ]; do
  case "$1" in --expect) EXPECT="$2"; shift 2 ;; --install) INSTALL=1; shift ;; --no-launch) LAUNCH=0; shift ;; *) echo "unknown option $1" >&2; exit 2 ;; esac
done
here="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$OUT"; OUT="$(cd "$OUT" && pwd)"
name="$(basename "$FILE")"; CHECKS="$OUT/verify-checks.tsv"; : > "$CHECKS"
check() { if [ "$1" = 0 ]; then echo "PASS  $2 — $3"; printf 'true\t%s\t%s\n' "$2" "$3" >> "$CHECKS"; else echo "FAIL  $2 — $3"; printf 'false\t%s\t%s\n' "$2" "$3" >> "$CHECKS"; fi; }
record() { echo "INFO  $1 — $2"; printf 'info\t%s\t%s\n' "$1" "$2" >> "$CHECKS"; }
gatekeeper() { # gatekeeper <label> <spctl args…>: Developer ID must be accepted as "Notarized Developer ID"
  local label="$1"; shift; local out ok src
  out="$(spctl "$@" 2>&1)" && ok=0 || ok=1; src="$(echo "$out" | sed -n 's/^source=//p' | head -1)"
  echo "$out" > "$OUT/gatekeeper-$label.txt"
  if [ "$EXPECT" = developer-id ]; then check $([ $ok = 0 ] && [ "$src" = "Notarized Developer ID" ] && echo 0 || echo 1) "Gatekeeper: $label" "$([ $ok = 0 ] && echo accepted || echo rejected) (${src:-$(echo "$out" | tail -1)})"
  else record "Gatekeeper: $label" "$([ $ok = 0 ] && echo accepted || echo rejected) (${src:-$(echo "$out" | tail -1)}); a test build is expected to be rejected"; fi
}

# A Developer ID release is checked and launched with the quarantine attribute on the app, as Archive Utility and Finder
# leave it. A test build (ad-hoc) would be refused with it, so its copy is launched without it, to test the hardened
# runtime only; its Gatekeeper verdict is recorded from the quarantined file.
quarantine_app() { if [ "$EXPECT" = developer-id ]; then find "$APP" -print0 | xargs -0 xattr -w com.apple.quarantine "$Q" 2> /dev/null; fi; }
W="$(mktemp -d "${TMPDIR:-/tmp}/hcg-mail-verify.XXXXXX")"; trap 'hdiutil detach "$W/mnt" > /dev/null 2>&1; rm -rf "$W"' EXIT
cp "$FILE" "$W/$name"; F="$W/$name"
echo "HCG Mail download check: $name ($(shasum -a 256 "$F" | cut -d' ' -f1)) on macOS $(sw_vers -productVersion) $(uname -m)"
# The attribute Safari writes on a download: flags 0083 (downloaded, user-approved download), time, agent, identifier.
Q="0083;$(printf '%x' "$(date +%s)");Safari;$(uuidgen)"
xattr -w com.apple.quarantine "$Q" "$F"
check $([ -n "$(xattr -p com.apple.quarantine "$F" 2>/dev/null)" ] && echo 0 || echo 1) "Quarantine attribute set, as on a download" "$(xattr -p com.apple.quarantine "$F" 2>/dev/null)"

APP=""
case "$name" in
  *.zip)
    mkdir -p "$W/zip" && ditto -x -k "$F" "$W/zip"; APP="$W/zip/HCG Mail.app"
    [ -d "$APP" ] || { check 1 "ZIP holds HCG Mail.app" "$(ls "$W/zip")"; }
    # Archive Utility gives every extracted file the archive's quarantine attribute.
    quarantine_app
    check $([ -d "$APP" ] && echo 0 || echo 1) "ZIP extracted (ditto$([ "$EXPECT" = developer-id ] && echo ", quarantine carried onto the app"))" "$APP"
    ;;
  *.dmg)
    gatekeeper "disk image" --assess --type open --context context:primary-signature -vv "$F"
    st="$(xcrun stapler validate "$F" 2>&1)" && ok=0 || ok=1
    if [ "$EXPECT" = developer-id ]; then check $ok "Notarization ticket stapled to the disk image" "$(echo "$st" | tail -1)"; else record "Stapled ticket (disk image)" "$(echo "$st" | tail -1)"; fi
    mkdir -p "$W/mnt" && hdiutil attach "$F" -nobrowse -readonly -mountpoint "$W/mnt" > "$OUT/hdiutil-attach.txt" 2>&1
    check $? "Disk image mounts" "$(tail -1 "$OUT/hdiutil-attach.txt")"
    check $([ -L "$W/mnt/Applications" ] && echo 0 || echo 1) "Disk image has the Applications link" "$(ls "$W/mnt" | tr '\n' ' ')"
    mkdir -p "$W/Applications" && ditto "$W/mnt/HCG Mail.app" "$W/Applications/HCG Mail.app" && APP="$W/Applications/HCG Mail.app"
    quarantine_app
    hdiutil detach "$W/mnt" > /dev/null 2>&1
    check $([ -n "$APP" ] && echo 0 || echo 1) "App copied out of the disk image" "${APP:-missing}"
    ;;
  *.pkg)
    gatekeeper "installer" --assess --type install -vv "$F"
    sig="$(pkgutil --check-signature "$F" 2>&1)"; echo "$sig" > "$OUT/pkg-signature.txt"
    if [ "$EXPECT" = developer-id ]; then check $(echo "$sig" | grep -q '1\. Developer ID Installer: ' && echo 0 || echo 1) "Installer signed with Developer ID Installer" "$(echo "$sig" | sed -n 's/^ *1\. //p' | head -1)"
    else record "Installer signature" "$(echo "$sig" | sed -n 's/^ *Status: //p' | head -1)"; fi
    st="$(xcrun stapler validate "$F" 2>&1)" && ok=0 || ok=1
    if [ "$EXPECT" = developer-id ]; then check $ok "Notarization ticket stapled to the installer" "$(echo "$st" | tail -1)"; else record "Stapled ticket (installer)" "$(echo "$st" | tail -1)"; fi
    if [ "$INSTALL" = 1 ]; then
      sudo installer -pkg "$F" -target / -verboseR > "$OUT/installer.txt" 2>&1
      check $? "Installed with installer(8)" "$(grep -E 'installer: (The install was successful|Install failed)' "$OUT/installer.txt" | tail -1)"
      APP="/Applications/HCG Mail.app"
      check $([ -d "$APP" ] && echo 0 || echo 1) "Installed at /Applications/HCG Mail.app" "$(ls -ld "$APP" 2>&1)"
      check $([ "$(stat -f %Su "$APP")" = root ] && echo 0 || echo 1) "Installed app owned by root (cannot be altered without administrator rights)" "$(stat -f '%Su:%Sg %Sp' "$APP")"
    else record "Installation" "not run (--install installs into /Applications and needs administrator rights)"; fi
    ;;
  *) echo "not a ZIP, PKG or DMG: $name" >&2; exit 2 ;;
esac

if [ -n "$APP" ] && [ -d "$APP" ]; then
  v="$(codesign --verify --deep --strict --verbose=2 "$APP" 2>&1)" && ok=0 || ok=1
  check $ok "Installed app's signature is intact" "$(echo "$v" | tail -1)"
  gatekeeper "installed app" --assess --type execute -vv "$APP"
  st="$(xcrun stapler validate "$APP" 2>&1)" && ok=0 || ok=1
  if [ "$EXPECT" = developer-id ]; then check $ok "Notarization ticket stapled to the app" "$(echo "$st" | tail -1)"; else record "Stapled ticket (app)" "$(echo "$st" | tail -1)"; fi
  if command -v syspolicy_check > /dev/null 2>&1; then
    sp="$(syspolicy_check distribution "$APP" 2>&1)" && ok=0 || ok=1; echo "$sp" > "$OUT/syspolicy_check.txt"
    if [ "$EXPECT" = developer-id ]; then check $ok "syspolicy_check distribution" "$(echo "$sp" | tail -1)"; else record "syspolicy_check distribution" "$(echo "$sp" | tail -1)"; fi
  fi
  d="$(codesign -dvv "$APP" 2>&1)"
  record "Signature" "$(echo "$d" | grep -E '^(Authority|TeamIdentifier|Signature)=' | head -2 | tr '\n' ' ')$(echo "$d" | sed -n 's/^CodeDirectory .*flags=0x[0-9a-f]*(\([^)]*\)).*/flags \1/p')"
  if [ "$LAUNCH" = 1 ]; then
    # Started directly (not through Finder) so the run cannot wait on the "downloaded from the Internet" confirmation
    # Finder shows once for every downloaded app; Gatekeeper's verdict on that same quarantined copy is checked above.
    bash "$here/../unix-runtime-check.sh" "$APP/Contents/MacOS/HCGMail" "$OUT/runtime" > "$OUT/runtime.txt" 2>&1; rc=$?
    tail -25 "$OUT/runtime.txt"
    grep '^FAIL' "$OUT/runtime.txt" | sed 's/^/      failed: /' || true
    check $rc "Launch and runtime checks (unix-runtime-check.sh)" "$(tail -1 "$OUT/runtime.txt")"
  fi
fi

/usr/bin/python3 - "$CHECKS" "$OUT/verification-$name.json" "$name" "$(shasum -a 256 "$FILE" | cut -d' ' -f1)" "$EXPECT" <<'PY'
import json, platform, sys
checks = [dict(zip(("state", "check", "detail"), l.rstrip("\n").split("\t", 2))) for l in open(sys.argv[1]) if "\t" in l]
json.dump({"file": sys.argv[3], "sha256": sys.argv[4], "expected": sys.argv[5],
           "macos": platform.mac_ver()[0], "machine": platform.machine(),
           "checks": [{"check": c["check"], "passed": c["state"] == "true", "info": c["state"] == "info", "detail": c["detail"]} for c in checks],
           "failures": sum(c["state"] == "false" for c in checks)}, open(sys.argv[2], "w"), indent=2)
PY
failed="$(grep -c '^false' "$CHECKS")"
echo; [ "$failed" = 0 ] && echo "Download check passed: $name" || echo "Download check: $failed check(s) FAILED for $name"
exit $([ "$failed" = 0 ] && echo 0 || echo 1)
