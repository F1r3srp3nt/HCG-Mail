#!/usr/bin/env bash
# Builds a signed Release .ipa for devices / TestFlight / App Store — and stops there.
# It never uploads to TestFlight or App Store Connect; that is a manual, founder-approved step.
#
# Required (from your own Apple Developer account; never commit them):
#   HCG_IOS_SIGNING_IDENTITY      e.g. "Apple Distribution: Hakmi Capital Group (TEAMID1234)"
#                                 (for a development device build: "Apple Development: …")
#   HCG_IOS_PROVISIONING_PROFILE  name or UUID of an installed provisioning profile for the bundle identifier
# Optional:
#   HCG_IOS_BUILD_NUMBER          CFBundleVersion, must increase with every upload (default 1)
#   HCG_MICROSOFT_CLIENT_ID       enables Microsoft sign-in (public client ID GUID; see the README)
source "$(dirname "$0")/_common.sh"
require_macos

: "${HCG_IOS_SIGNING_IDENTITY:?set HCG_IOS_SIGNING_IDENTITY (see the comment at the top of this script)}"
: "${HCG_IOS_PROVISIONING_PROFILE:?set HCG_IOS_PROVISIONING_PROFILE (see the comment at the top of this script)}"
OUT="$OUT_ROOT/ipa"

extra=()
[[ -n "${HCG_MICROSOFT_CLIENT_ID:-}" ]] && extra+=("-p:HcgMicrosoftClientId=$HCG_MICROSOFT_CLIENT_ID")

dotnet publish "$APP_PROJECT" -c Release -f "$TFM" -r ios-arm64 \
  -p:ArchiveOnBuild=true \
  -p:CodesignKey="$HCG_IOS_SIGNING_IDENTITY" \
  -p:CodesignProvision="$HCG_IOS_PROVISIONING_PROFILE" \
  -p:ApplicationVersion="${HCG_IOS_BUILD_NUMBER:-1}" \
  -p:BuildIpa=true \
  -o "$OUT" "${extra[@]}"

IPA="$(find "$OUT" -maxdepth 1 -name '*.ipa' | head -1)"
[[ -f "$IPA" ]] || { echo "error: no .ipa produced in $OUT" >&2; exit 1; }
echo
echo "Signed IPA: $IPA"
codesign -dv --verbose=2 "$(find "$OUT" -name '*.app' -maxdepth 3 -type d | head -1)" 2>&1 | grep -E 'Identifier|TeamIdentifier|Authority' || true
echo "Not uploaded. To distribute, upload it yourself with Transporter or Xcode Organizer after review."
