#!/usr/bin/env bash
# Builds HCG Mail signed for development, checks it, installs it on your own iPhone or iPad, and starts it.
# Nothing is uploaded; App Store / TestFlight builds come from scripts/export-ipa.sh.
#
#   scripts/install-device.sh --list                 connected / paired devices (name, identifier, state)
#   scripts/install-device.sh                        build, check, install and start on the only connected device
#   scripts/install-device.sh "Yasin's iPhone"       … on this device (name, UDID or identifier from --list)
#   scripts/install-device.sh --no-build …           install the last build again (artifacts/ios/device-signed/<config>)
#
# Signing is found automatically: the Apple Development certificate in your keychain and an installed development
# profile for com.hakmicapitalgroup.hcgmail that includes this device (scripts/find-dev-signing.py). If there is
# none, the script says how to let Xcode create them (once, two minutes). To choose explicitly instead:
#   HCG_IOS_DEV_IDENTITY   "Apple Development: <Name> (<TEAMID>)"   (security find-identity -v -p codesigning)
#   HCG_IOS_DEV_PROFILE    name, UUID or path of the development profile
# Optional:
#   HCG_IOS_CONFIGURATION  Release (default: the AOT configuration that ships) or Debug (faster to build)
#   HCG_IOS_BUILD_NUMBER   CFBundleVersion (default 1)
#   HCG_MICROSOFT_CLIENT_ID  enables Microsoft sign-in (see the README); without it the button stays hidden
#
# On the device, once: Settings › Privacy & Security › Developer Mode → On (the iPhone restarts), and trust this Mac
# when it is first connected by cable. Afterwards installs also work over the same Wi-Fi network.
source "$(dirname "$0")/_common.sh"
require_macos

DEVICE=""; BUILD=1; LIST=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --list) LIST=1 ;;
    --no-build) BUILD=0 ;;
    -h|--help) sed -n '2,22p' "$0"; exit 0 ;;
    *) DEVICE="$1" ;;
  esac
  shift
done

CONFIG="${HCG_IOS_CONFIGURATION:-Release}"
BUILD_NUMBER="${HCG_IOS_BUILD_NUMBER:-1}"
OUT="$OUT_ROOT/device-signed/$CONFIG"
VERIFY=(python3 -I "$IOS_ROOT/scripts/verify-ios-build.py")
DEVICECTL=(xcrun devicectl)
step() { echo; echo "== $*"; }

# --- the device ----------------------------------------------------------------------------------------------------
devices_json="$(mktemp "${TMPDIR:-/tmp}/hcg-devices.XXXXXX")"
trap 'rm -f "$devices_json"' EXIT
"${DEVICECTL[@]}" list devices --json-output "$devices_json" >/dev/null 2>&1 \
  || { echo "error: \`xcrun devicectl list devices\` failed — Xcode 15 or later is needed (this project pins Xcode $REQUIRED_XCODE)." >&2; exit 1; }
# One line per iPhone/iPad: identifier, UDID, name, model, OS, pairing state, tunnel state.
devices="$(python3 -I -c '
import json, sys
for d in json.load(open(sys.argv[1])).get("result", {}).get("devices", []):
    hw, props, conn = d.get("hardwareProperties", {}), d.get("deviceProperties", {}), d.get("connectionProperties", {})
    if hw.get("platform") != "iOS": continue
    print("\t".join([d.get("identifier", ""), hw.get("udid", ""), props.get("name", ""), hw.get("marketingName", ""),
                     props.get("osVersionNumber", ""), conn.get("pairingState", ""), conn.get("tunnelState", "")]))
' "$devices_json")"
if [[ $LIST -eq 1 ]]; then
  [[ -n "$devices" ]] || { echo "No iPhone or iPad is paired with this Mac. Connect it by cable, unlock it and tap Trust."; exit 1; }
  printf 'identifier\tUDID\tname\tmodel\tiOS\tpairing\tconnection\n%s\n' "$devices" \
    | awk -F'\t' '{ printf "%-38s %-27s %-22s %-22s %-7s %-9s %s\n", $1, $2, $3, $4, $5, $6, $7 }'
  exit 0
fi
if [[ -n "$DEVICE" ]]; then
  line="$(awk -F'\t' -v d="$DEVICE" '$1==d || $2==d || $3==d' <<<"$devices" | head -1)"
else
  # Without a name: the only device that is connected now.
  connected="$(awk -F'\t' '$7=="connected"' <<<"$devices")"
  [[ "$(grep -c . <<<"$connected")" -eq 1 ]] && line="$connected" || line=""
  [[ -n "$line" ]] || { echo "error: name the device (one of these; connection must be \"connected\"):" >&2; "$0" --list >&2 || true; exit 1; }
fi
[[ -n "$line" ]] || { echo "error: no paired iPhone or iPad called \"$DEVICE\". Paired devices:" >&2; "$0" --list >&2 || true; exit 1; }
IFS=$'\t' read -r DEV_ID DEV_UDID DEV_NAME DEV_MODEL DEV_OS DEV_PAIRING DEV_TUNNEL <<<"$line"
echo "Device: $DEV_NAME ($DEV_MODEL, iOS $DEV_OS), UDID $DEV_UDID — $DEV_PAIRING, $DEV_TUNNEL"
[[ "$DEV_PAIRING" == "paired" ]] || { echo "error: the device is not paired with this Mac: connect it by cable, unlock it and tap Trust." >&2; exit 1; }
if ! python3 -I -c 'import sys; v=lambda s: [int(x) for x in s.split(".") if x.isdigit()]; sys.exit(v(sys.argv[2]) < v(sys.argv[1]))' "$MINIMUM_IOS" "$DEV_OS"; then
  echo "error: HCG Mail needs iOS $MINIMUM_IOS or later; the device runs iOS $DEV_OS." >&2; exit 1
fi

# --- signing setup (before a 10-minute build) ----------------------------------------------------------------------
if [[ $BUILD -eq 1 ]]; then
  step "Signing: development certificate and a profile that includes $DEV_NAME"
  if ! found="$(security find-identity -v -p codesigning | python3 -I "$IOS_ROOT/scripts/find-dev-signing.py" "$BUNDLE_ID" "$DEV_UDID")"; then
    echo >&2
    echo "Let Xcode create them (once, about two minutes):" >&2
    echo "  1. Xcode > Settings > Accounts: sign in with the Apple ID of your developer team." >&2
    echo "  2. Xcode > File > New > Project > iOS > App. Organization Identifier: com.hakmicapitalgroup," >&2
    echo "     Product Name: hcgmail (so the Bundle Identifier reads exactly $BUNDLE_ID); Team: your team." >&2
    echo "  3. Choose $DEV_NAME as the run destination and press Run (Cmd-R). Xcode registers the iPhone and creates" >&2
    echo "     the certificate and the profile. Close that project and run this script again." >&2
    exit 1
  fi
  HCG_IOS_DEV_IDENTITY="$(sed -n 1p <<<"$found")"
  HCG_IOS_DEV_PROFILE="$(sed -n 2p <<<"$found")"
  echo "  identity: $HCG_IOS_DEV_IDENTITY"
  echo "  profile:  $(sed -n 3p <<<"$found") ($HCG_IOS_DEV_PROFILE), includes $DEV_NAME"
  "${VERIFY[@]}" profile --profile "$HCG_IOS_DEV_PROFILE" --identity "$HCG_IOS_DEV_IDENTITY"
fi

# --- build ---------------------------------------------------------------------------------------------------------
if [[ $BUILD -eq 1 ]]; then
  step "Build: $CONFIG, ios-arm64, signed for development (Release takes several minutes: full AOT)"
  rm -rf "$OUT"
  dotnet build "$APP_PROJECT" -c "$CONFIG" -f "$TFM" -r ios-arm64 \
    -p:CodesignKey="$HCG_IOS_DEV_IDENTITY" -p:CodesignProvision="$HCG_IOS_DEV_PROFILE" \
    -p:ApplicationVersion="$BUILD_NUMBER" -o "$OUT"
fi
APP="$(find "$OUT" -maxdepth 1 -name '*.app' -type d 2>/dev/null | head -1)"
[[ -d "$APP" ]] || { echo "error: no .app bundle in $OUT (build first, without --no-build)" >&2; exit 1; }

# --- check what will be installed -----------------------------------------------------------------------------------
step "Check the signed app"
"${VERIFY[@]}" app "$APP" --kind device --build-number "$BUILD_NUMBER"

# --- install and start ----------------------------------------------------------------------------------------------
step "Install on $DEV_NAME"
if ! "${DEVICECTL[@]}" device install app --device "$DEV_ID" "$APP"; then
  cat >&2 <<EOF
error: the install failed. Usual causes:
  * Developer Mode is off: on the device, Settings › Privacy & Security › Developer Mode → On (it restarts).
  * The device is locked or not trusted: unlock it, reconnect the cable, tap Trust.
  * The profile does not include this device (UDID $DEV_UDID), or was made for another certificate.
  * Another build of com.hakmicapitalgroup.hcgmail (e.g. from TestFlight) with a different signature: delete it first.
EOF
  exit 1
fi
step "Start HCG Mail"
"${DEVICECTL[@]}" device process launch --device "$DEV_ID" "$BUNDLE_ID" \
  || echo "warning: installed, but could not start it remotely (the device may be locked). Tap the HCG Mail icon." >&2
echo
echo "Installed: $APP"
echo "On $DEV_NAME: if iOS says \"Untrusted Developer\", open Settings › General › VPN & Device Management, tap the"
echo "Apple Development profile, and trust it. Then follow DEVICE_INSTALL.md › First run."
