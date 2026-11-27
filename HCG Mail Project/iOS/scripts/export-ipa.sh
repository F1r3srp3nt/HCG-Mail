#!/usr/bin/env bash
# Builds the signed Release .ipa and .xcarchive for devices / TestFlight / the App Store — and stops there.
# It never uploads to TestFlight or App Store Connect; that is a manual, founder-approved step
# (APP_STORE_SUBMISSION.md: upload with Transporter).
#
# Required (from your own Apple Developer account; never commit them):
#   HCG_IOS_SIGNING_IDENTITY      e.g. "Apple Distribution: Hakmi Capital Group (TEAMID1234)"
#                                 (for a development device build: "Apple Development: …")
#   HCG_IOS_PROVISIONING_PROFILE  name, UUID or path of the provisioning profile for com.hakmicapitalgroup.hcgmail
# Optional:
#   HCG_IOS_BUILD_NUMBER          CFBundleVersion, must increase with every upload (default 1)
#   HCG_IOS_MARKETING_VERSION     CFBundleShortVersionString shown on the App Store (default: <Version> in
#                                 HCG Mail Project/Desktop/Directory.Build.props); must match the App Store
#                                 Connect version you submit
#   HCG_IOS_DISTRIBUTION          app-store (default: all App Store checks must pass) or other (ad hoc / development)
#   HCG_MICROSOFT_CLIENT_ID       enables Microsoft sign-in (public client ID GUID; see the README)
#
# Output (artifacts/ is ignored by git):
#   artifacts/ios/ipa/HCGMail.iOS.ipa               the signed app — upload this one with Transporter
#   artifacts/ios/ipa/HCGMail.iOS.ipa.sha256        its SHA-256 checksum (shasum -a 256 -c to re-check before upload)
#   artifacts/ios/archive/HCGMail.iOS.xcarchive     the archive, written there directly by .NET (ArchiveDir); double-click
#                                                    it to add it to Xcode › Window › Organizer › Archives
#   artifacts/ios/verification.txt                  the checks below, for the release record
# The paths are fixed (no date-stamped folder in ~/Library), so other tools can rely on them. Each run replaces them.
source "$(dirname "$0")/_common.sh"
require_macos

: "${HCG_IOS_SIGNING_IDENTITY:?set HCG_IOS_SIGNING_IDENTITY (see the comment at the top of this script)}"
: "${HCG_IOS_PROVISIONING_PROFILE:?set HCG_IOS_PROVISIONING_PROFILE (see the comment at the top of this script)}"
BUILD_NUMBER="${HCG_IOS_BUILD_NUMBER:-1}"
DISTRIBUTION="${HCG_IOS_DISTRIBUTION:-app-store}"
OUT="$OUT_ROOT/ipa"
ARCHIVE_OUT="$OUT_ROOT/archive"
REPORT="$OUT_ROOT/verification.txt"
VERIFY=(python3 -I "$IOS_ROOT/scripts/verify-ios-build.py")
store_flag=(); [[ "$DISTRIBUTION" == "app-store" ]] && store_flag=(--app-store)

mkdir -p "$OUT_ROOT"
rm -rf "$OUT"
{
  echo "HCG Mail iOS release build — $(date -u '+%Y-%m-%d %H:%M UTC'), commit $(git -C "$IOS_ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"
  echo
} > "$REPORT"

# 1. Before anything is built: toolchain, then the identity and profile against the bundle ID (= the App ID of the
#    App Store Connect app record).
"$IOS_ROOT/scripts/doctor.sh" || echo "warning: the build doctor reported problems above (a missing simulator runtime does not matter here); the build stops if they do." >&2
"${VERIFY[@]}" profile --profile "$HCG_IOS_PROVISIONING_PROFILE" --identity "$HCG_IOS_SIGNING_IDENTITY" ${store_flag[@]+"${store_flag[@]}"} | tee -a "$REPORT"

# 2. Build, sign and archive.
extra=(-p:ApplicationVersion="$BUILD_NUMBER")
[[ -n "${HCG_IOS_MARKETING_VERSION:-}" ]] && extra+=(-p:ApplicationDisplayVersion="$HCG_IOS_MARKETING_VERSION")
ARCHIVE="$ARCHIVE_OUT/HCGMail.iOS.xcarchive"
IPA="$OUT/HCGMail.iOS.ipa"
# The .NET iOS SDK's ArchiveDir is the .xcarchive directory itself (not a folder to put archives in). It must not
# hold an older archive: the Archive task copies into it without emptying it.
rm -rf "$ARCHIVE"; mkdir -p "$ARCHIVE_OUT"
# Mailbox sign-in values (HCG_MICROSOFT_CLIENT_ID etc.) reach the build through oauth-clients.props.
require_build_xcode
echo "Building with Xcode $BUILD_XCODE_VERSION ($BUILD_XCODE_APP)" | tee -a "$REPORT"
ios_dotnet publish "$APP_PROJECT" -c Release -f "$TFM" -r ios-arm64 \
  -p:ArchiveOnBuild=true -p:ArchiveDir="$ARCHIVE" \
  -p:CodesignKey="$HCG_IOS_SIGNING_IDENTITY" \
  -p:CodesignProvision="$HCG_IOS_PROVISIONING_PROFILE" \
  -p:BuildIpa=true -p:IpaPackagePath="$IPA" \
  -o "$OUT" "${extra[@]}"

# 3. Both products at their fixed paths.
[[ -f "$IPA" ]] || { echo "error: no .ipa at $IPA" >&2; exit 1; }
[[ -f "$ARCHIVE/Info.plist" && -d "$ARCHIVE/Products/Applications/HCGMail.iOS.app" ]] \
  || { echo "error: no complete .xcarchive at $ARCHIVE (Info.plist and Products/Applications/HCGMail.iOS.app)" >&2; exit 1; }
ARCHIVE_COPY="$ARCHIVE"

# 4. Verify what will be uploaded: the .ipa, and the app inside the archive.
echo | tee -a "$REPORT"
set +e
"${VERIFY[@]}" app "$IPA" --kind device --build-number "$BUILD_NUMBER" \
  ${HCG_IOS_MARKETING_VERSION:+--marketing-version "$HCG_IOS_MARKETING_VERSION"} ${store_flag[@]+"${store_flag[@]}"} | tee -a "$REPORT"
ipa_ok=${PIPESTATUS[0]}
archive_ok=0
if [[ -n "${ARCHIVE_COPY:-}" ]]; then
  echo | tee -a "$REPORT"
  "${VERIFY[@]}" app "$ARCHIVE_COPY/Products/Applications/HCGMail.iOS.app" --kind device --build-number "$BUILD_NUMBER" \
    ${store_flag[@]+"${store_flag[@]}"} | tee -a "$REPORT"
  archive_ok=${PIPESTATUS[0]}
fi
set -e

# 5. Checksum of exactly what will be uploaded, for the release record.
( cd "$(dirname "$IPA")" && shasum -a 256 "$(basename "$IPA")" ) > "$IPA.sha256"
{ echo; echo "SHA-256: $(cut -d' ' -f1 "$IPA.sha256")  $(basename "$IPA")"; } | tee -a "$REPORT"

echo
echo "Signed IPA:   $IPA"
echo "SHA-256:      $IPA.sha256"
echo "Archive:      $ARCHIVE  (double-click to add it to Xcode › Window › Organizer)"
echo "Checks:       $REPORT"
if [[ $ipa_ok -ne 0 || $archive_ok -ne 0 ]]; then
  echo "error: the build did not pass every check — do not upload it. See $REPORT." >&2
  exit 1
fi
echo "Not uploaded. Upload the .ipa yourself with Transporter (APP_STORE_SUBMISSION.md) after review."
