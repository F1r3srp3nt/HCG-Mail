#!/bin/bash
# HCG Mail for macOS: Developer ID signing, notarization, stapling and the distribution files (ZIP, PKG, DMG).
# Runs on a Mac with Xcode or the Command Line Tools. Works with macOS's own bash 3.2. Never prints, exports or copies a
# private key: identities are used by SHA-1 from the keychain, notarization credentials by keychain profile or API key.
#
#   sign-and-notarize.sh --check-setup [--keychain <path>] [--notary-profile <name>]
#       Lists what this Mac has for signing HCG Mail (identities, notarization profile, tools). Changes nothing.
#
#   sign-and-notarize.sh --app "<HCG Mail.app>" --version 1.0.0-beta.2 --arch arm64|x64 --out <dir>
#       --identity <Developer ID Application: SHA-1 or full name>
#       --installer-identity <Developer ID Installer: SHA-1 or full name>
#       (--notary-profile <keychain profile> | --notary-key <AuthKey_XXXX.p8> --notary-key-id <id> --notary-issuer <uuid>)
#       [--keychain <path>] [--entitlements <plist>] [--build-number N]
#       [--zip-name "HCG Mail.app.zip"] [--pkg-name "HCG Mail-<version>-osx-<arch>.pkg"] [--dmg]
#
#   sign-and-notarize.sh --app "<HCG Mail.app>" --version … --arch … --out <dir> --identity - --test
#       Ad-hoc test build: the same layout checks, hardened runtime and entitlements, but no certificate and no
#       notarization. For testing on build machines only: Gatekeeper rejects it on every other Mac. Never distribute it.
#
# The input .app is never changed: it is copied first. Output in --out (names configurable):
#   HCG Mail.app.zip                        the notarized, stapled app (ditto ZIP)
#   HCG Mail-<version>-osx-<arch>.pkg       installer for /Applications, signed Developer ID Installer, notarized, stapled
#   HCG Mail-<version>-osx-<arch>.dmg       with --dmg: the app and an Applications link, signed, notarized, stapled
#   signing-evidence-osx-<arch>.json   what was signed with what, every verification, notarization submissions
#   logs/                              codesign, notarytool, stapler, spctl and pkgutil output
# Test builds are named HCGMail-<version>-osx-<arch>-adhoc-TEST-not-for-distribution.{zip,pkg,dmg}.
# Version consistency is checked: Info.plist CFBundleShortVersionString is the numeric part of --version,
# CFBundleVersion is --build-number, the app's own version (AssemblyInformationalVersion) is --version, and the
# installer package carries --version.
set -euo pipefail

die() { echo "ERROR: $*" >&2; exit 1; }
say() { echo "── $*"; }
here="$(cd "$(dirname "$0")" && pwd)"

APP="" VERSION="" ARCH="" OUT="" IDENTITY="" INSTALLER="" PROFILE="" KEY="" KEY_ID="" ISSUER="" KEYCHAIN="" BUILD=""
ENTITLEMENTS="$here/../../packaging/macos/HCGMail.entitlements" TEST=0 CHECK=0 ZIP_NAME="" PKG_NAME_OUT="" WANT_DMG=0
while [ $# -gt 0 ]; do
  case "$1" in
    --app) APP="$2"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    --arch) ARCH="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --identity) IDENTITY="$2"; shift 2 ;;
    --installer-identity) INSTALLER="$2"; shift 2 ;;
    --notary-profile) PROFILE="$2"; shift 2 ;;
    --notary-key) KEY="$2"; shift 2 ;;
    --notary-key-id) KEY_ID="$2"; shift 2 ;;
    --notary-issuer) ISSUER="$2"; shift 2 ;;
    --keychain) KEYCHAIN="$2"; shift 2 ;;
    --entitlements) ENTITLEMENTS="$2"; shift 2 ;;
    --build-number) BUILD="$2"; shift 2 ;;
    --test) TEST=1; shift ;;
    --zip-name) ZIP_NAME="$2"; shift 2 ;;
    --pkg-name) PKG_NAME_OUT="$2"; shift 2 ;;
    --dmg) WANT_DMG=1; shift ;;
    --check-setup) CHECK=1; shift ;;
    *) die "unknown option $1 (see the header of this script)" ;;
  esac
done
[ "$(uname -s)" = Darwin ] || die "this runs on a Mac (codesign, notarytool, pkgbuild, hdiutil)"

# ---------------------------------------------------------------------------------------------------------- identities
# security find-identity prints:   1) 0123…ABCD "Developer ID Application: Name (TEAMID)"
identities() { # identities <policy>  → "SHA1<TAB>name" per valid identity
  if [ -n "$KEYCHAIN" ]; then security find-identity -v -p "$1" "$KEYCHAIN"; else security find-identity -v -p "$1"; fi 2>/dev/null |
    sed -n 's/^ *[0-9][0-9]*) \([0-9A-F]\{40\}\) "\(.*\)"$/\1	\2/p'
}
resolve() { # resolve <policy> <SHA-1 or name> <required name prefix>  → "SHA1<TAB>name" or dies
  local found
  found="$(identities "$1" | awk -F'\t' -v want="$2" 'toupper($1) == toupper(want) || $2 == want' | sort -u)"
  [ -n "$found" ] || die "no valid identity '$2' in the keychain (sign-and-notarize.sh --check-setup lists what is there)"
  [ "$(printf '%s\n' "$found" | wc -l | tr -d ' ')" = 1 ] || die "'$2' matches more than one identity; pass its SHA-1"
  case "$(printf '%s' "$found" | cut -f2)" in "$3"*) ;; *) die "'$2' is not a '$3' identity: $(printf '%s' "$found" | cut -f2)" ;; esac
  printf '%s' "$found"
}
team_of() { printf '%s' "$1" | sed -n 's/.*(\([A-Z0-9]\{10\}\))$/\1/p'; }

if [ "$CHECK" = 1 ]; then
  echo "Signing setup on this Mac (read only)"
  echo "  macOS $(sw_vers -productVersion) ($(uname -m)); $(xcode-select -p 2>/dev/null || echo 'no Xcode or Command Line Tools: run xcode-select --install')"
  for t in codesign pkgbuild productbuild hdiutil spctl; do printf '  %-14s %s\n' "$t" "$(command -v $t || echo MISSING)"; done
  printf '  %-14s %s\n' notarytool "$(xcrun --find notarytool 2>/dev/null || echo 'MISSING (needs Xcode 13 or later, or current Command Line Tools)')"
  printf '  %-14s %s\n' stapler "$(xcrun --find stapler 2>/dev/null || echo MISSING)"
  echo "  Developer ID Application identities (sign the app and the DMG):"
  identities codesigning | awk -F'\t' '$2 ~ /^Developer ID Application: / {print "    " $1 "  " $2; n++} END {if (!n) print "    NONE — create one: Xcode › Settings › Accounts › Manage Certificates › + › Developer ID Application"}'
  echo "  Developer ID Installer identities (sign the .pkg):"
  identities basic | awk -F'\t' '$2 ~ /^Developer ID Installer: / {print "    " $1 "  " $2; n++} END {if (!n) print "    NONE — create one: Xcode › Settings › Accounts › Manage Certificates › + › Developer ID Installer"}'
  echo "  Other code-signing identities (not usable for HCG Store distribution):"
  identities codesigning | awk -F'\t' '$2 !~ /^Developer ID Application: / {print "    " $2; n++} END {if (!n) print "    none"}'
  if [ -n "$PROFILE" ]; then
    if xcrun notarytool history --keychain-profile "$PROFILE" > /dev/null 2>&1; then echo "  Notarization profile '$PROFILE': works (notarytool history answered)"
    else echo "  Notarization profile '$PROFILE': NOT usable — create it: xcrun notarytool store-credentials \"$PROFILE\" --key <AuthKey.p8> --key-id <id> --issuer <uuid>"; fi
  else echo "  Notarization profile: pass --notary-profile <name> to test it"; fi
  exit 0
fi

[ -d "$APP" ] && [ -f "$APP/Contents/Info.plist" ] || die "--app must be the HCG Mail.app bundle"
[ -n "$VERSION" ] && [ -n "$OUT" ] || die "--version and --out are required"
case "$ARCH" in arm64) MACHO_ARCH=arm64; HOST_ARCH=arm64 ;; x64) MACHO_ARCH=x86_64; HOST_ARCH=x86_64 ;; *) die "--arch is arm64 or x64" ;; esac
[ -f "$ENTITLEMENTS" ] || die "entitlements file not found: $ENTITLEMENTS"
plutil -lint "$ENTITLEMENTS" > /dev/null || die "entitlements file is not a valid property list"
[ -n "$IDENTITY" ] || die "--identity is required (a Developer ID Application identity, or - with --test)"

if [ "$IDENTITY" = - ]; then
  [ "$TEST" = 1 ] || die "--identity - (ad-hoc) is only for --test builds, which must not be distributed"
  APP_SHA=- APP_NAME="ad-hoc (no certificate)" TEAM="" TS="--timestamp=none" SUFFIX="-adhoc-TEST-not-for-distribution"
else
  [ "$TEST" = 0 ] || die "--test builds are ad-hoc (--identity -)"
  r="$(resolve codesigning "$IDENTITY" "Developer ID Application: ")"; APP_SHA="$(printf '%s' "$r" | cut -f1)"; APP_NAME="$(printf '%s' "$r" | cut -f2)"
  TEAM="$(team_of "$APP_NAME")"; [ -n "$TEAM" ] || die "could not read the team ID from '$APP_NAME'"
  [ -n "$INSTALLER" ] || die "--installer-identity is required (Developer ID Installer) for the .pkg"
  r="$(resolve basic "$INSTALLER" "Developer ID Installer: ")"; PKG_SHA="$(printf '%s' "$r" | cut -f1)"; PKG_NAME="$(printf '%s' "$r" | cut -f2)"
  [ "$(team_of "$PKG_NAME")" = "$TEAM" ] || die "the installer identity ($PKG_NAME) belongs to another team than $APP_NAME"
  if [ -n "$PROFILE" ]; then NOTARY=(--keychain-profile "$PROFILE")
  elif [ -n "$KEY" ] && [ -n "$KEY_ID" ] && [ -n "$ISSUER" ]; then [ -f "$KEY" ] || die "API key file not found"; NOTARY=(--key "$KEY" --key-id "$KEY_ID" --issuer "$ISSUER")
  else die "notarization credentials are required: --notary-profile, or --notary-key/--notary-key-id/--notary-issuer"; fi
  if [ -n "$KEYCHAIN" ] && [ -n "$PROFILE" ]; then NOTARY+=(--keychain "$KEYCHAIN"); fi
  TS="--timestamp" SUFFIX=""
fi
KC=(); [ -n "$KEYCHAIN" ] && KC=(--keychain "$KEYCHAIN")

BASE="HCGMail-$VERSION-osx-$ARCH$SUFFIX"
mkdir -p "$OUT/logs"; OUT="$(cd "$OUT" && pwd)"; LOGS="$OUT/logs"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/hcg-mail-sign.XXXXXX")"; trap 'rm -rf "$WORK"' EXIT
CHECKS="$WORK/checks.tsv"; : > "$CHECKS"
check() { # check <0|1> <name> <detail>
  if [ "$1" = 0 ]; then echo "PASS  $2 — $3"; printf 'true\t%s\t%s\n' "$2" "$3" >> "$CHECKS"; else echo "FAIL  $2 — $3"; printf 'false\t%s\t%s\n' "$2" "$3" >> "$CHECKS"; fi
}
SUBMISSIONS="$WORK/submissions.tsv"; : > "$SUBMISSIONS"

say "HCG Mail $VERSION ($ARCH): $APP_NAME"
APPDIR="$WORK/app"; mkdir -p "$APPDIR"
ditto "$APP" "$APPDIR/HCG Mail.app"; A="$APPDIR/HCG Mail.app"
xattr -cr "$A"   # build-machine metadata (quarantine, Finder info) is not part of the app and breaks codesign
MAIN="$(plutil -extract CFBundleExecutable raw -o - "$A/Contents/Info.plist")"
BUNDLE_ID="$(plutil -extract CFBundleIdentifier raw -o - "$A/Contents/Info.plist")"
SHORT="$(plutil -extract CFBundleShortVersionString raw -o - "$A/Contents/Info.plist" 2>/dev/null || echo '')"
MINOS="$(plutil -extract LSMinimumSystemVersion raw -o - "$A/Contents/Info.plist" 2>/dev/null || echo '')"
[ "$BUNDLE_ID" = com.hakmicapitalgroup.hcgmail ] || die "unexpected bundle identifier $BUNDLE_ID"

# ------------------------------------------------------------------------------------------------- 1. bundle layout audit
# Contents/MacOS must hold code only: a data file there can be signed only in extended attributes, which ZIP tools,
# installer payloads and file transfers drop, and macOS then reports the downloaded app as damaged.
say "1. Bundle layout"
bad="" n=0
while IFS= read -r -d '' f; do
  n=$((n + 1))
  if ! file -b "$f" | grep -q '^Mach-O'; then bad="$bad ${f#$A/}"; continue; fi
  lipo -archs "$f" 2>/dev/null | tr ' ' '\n' | grep -qx "$MACHO_ARCH" || bad="$bad ${f#$A/}(no-$MACHO_ARCH)"
done < <(find "$A/Contents/MacOS" -type f -print0)
check $([ -z "$bad" ] && echo 0 || echo 1) "Contents/MacOS holds only Mach-O code for $MACHO_ARCH" "$n files${bad:+; not code or wrong architecture:$bad}"
[ -z "$bad" ] || die "the bundle layout is not signable for distribution (see above)"
other="$(find "$A/Contents" -type f ! -path "$A/Contents/MacOS/*" -print0 | xargs -0 file | grep -c 'Mach-O' || true)"
check $([ "$other" = 0 ] && echo 0 || echo 1) "No code outside Contents/MacOS" "$other Mach-O file(s) elsewhere"
[ "$other" = 0 ] || die "code outside Contents/MacOS is not handled by this script"

# ---------------------------------------------------------------------------------------------- version consistency
say "Version"
CORE="${VERSION%%-*}"; BVER="$(plutil -extract CFBundleVersion raw -o - "$A/Contents/Info.plist" 2>/dev/null || echo '')"
check $([ "$SHORT" = "$CORE" ] && echo 0 || echo 1) "Version: CFBundleShortVersionString" "$SHORT (expected $CORE, the numeric part of $VERSION)"
if [ -n "$BUILD" ]; then check $([ "$BVER" = "$BUILD" ] && echo 0 || echo 1) "Version: CFBundleVersion" "$BVER (expected build $BUILD)"; fi
# The app reports AssemblyInformationalVersion (Settings › About, Mail Health diagnostics); it is in the single-file host.
VERSION_IN_APP="$(LC_ALL=C grep -a -o "$VERSION[+0-9a-f]*" "$A/Contents/MacOS/$MAIN" | sort | uniq -c | sort -rn | head -1 | awk '{print $2}')"
check $([ -n "$VERSION_IN_APP" ] && echo 0 || echo 1) "Version: the app's own version" "${VERSION_IN_APP:-$VERSION not found in $MAIN}"
grep -q '^false' "$CHECKS" && die "version mismatch (see above)"

# ----------------------------------------------------------------------------------------------------------- 2. signing
# Inside out: every library and helper, then the bundle (which signs the main executable with the entitlements).
say "2. Code signing (hardened runtime, $([ "$TS" = --timestamp ] && echo 'secure timestamp' || echo 'no timestamp: test build'))"
sign_log="$LOGS/codesign-$ARCH$SUFFIX.log"; : > "$sign_log"
while IFS= read -r -d '' f; do
  [ "$(basename "$f")" = "$MAIN" ] && continue
  codesign --force --options runtime $TS --sign "$APP_SHA" ${KC[@]+"${KC[@]}"} "$f" >> "$sign_log" 2>&1 || { cat "$sign_log"; die "codesign failed on ${f#$A/}"; }
done < <(find "$A/Contents/MacOS" -type f -print0)
codesign --force --options runtime $TS --entitlements "$ENTITLEMENTS" --sign "$APP_SHA" ${KC[@]+"${KC[@]}"} "$A" >> "$sign_log" 2>&1 || { cat "$sign_log"; die "codesign failed on the app bundle"; }

# ------------------------------------------------------------------------------------------------------ 3. verification
say "3. Signature verification"
v="$(codesign --verify --deep --strict --verbose=2 "$A" 2>&1)" && ok=0 || ok=1
echo "$v" > "$LOGS/verify-$ARCH$SUFFIX.txt"
check $ok "Signature seals the whole bundle" "codesign --verify --deep --strict: $(echo "$v" | tail -1)"
d="$(codesign -dvvv "$A" 2>&1)"; echo "$d" > "$LOGS/describe-$ARCH$SUFFIX.txt"
flags="$(echo "$d" | sed -n 's/^CodeDirectory .*flags=\(0x[0-9a-f]*\)(\([^)]*\)).*/\2/p')"
check $(echo "$flags" | grep -q runtime && echo 0 || echo 1) "Hardened runtime" "flags: ${flags:-none}"
if [ "$APP_SHA" = - ]; then
  check $(echo "$d" | grep -q '^Signature=adhoc' && echo 0 || echo 1) "Signature kind" "ad-hoc (test build; not Developer ID)"
else
  leaf="$(echo "$d" | sed -n 's/^Authority=//p' | head -1)"; team="$(echo "$d" | sed -n 's/^TeamIdentifier=//p')"
  check $([ "$leaf" = "$APP_NAME" ] && [ "$team" = "$TEAM" ] && echo 0 || echo 1) "Developer ID Application signature" "$leaf, team $team; chain: $(echo "$d" | sed -n 's/^Authority=//p' | tr '\n' '>' | sed 's/>$//;s/>/ → /g')"
  check $(echo "$d" | grep -q '^Timestamp=' && echo 0 || echo 1) "Secure timestamp" "$(echo "$d" | sed -n 's/^Timestamp=//p')"
fi
ent="$(codesign -d --entitlements - --xml "$A" 2>/dev/null | sed -n '/<?xml/,$p')"; printf '%s\n' "$ent" > "$LOGS/entitlements-$ARCH$SUFFIX.plist"
want="$(plutil -convert json -o - "$ENTITLEMENTS")"; got="$(printf '%s' "$ent" | plutil -convert json -o - - 2>/dev/null || echo '{}')"
check $([ "$want" = "$got" ] && echo 0 || echo 1) "Entitlements are exactly HCGMail.entitlements" "$got"
check $(printf '%s' "$got" | grep -q get-task-allow && echo 1 || echo 0) "No debugging entitlement (get-task-allow)" "absent"
nested_bad=""
while IFS= read -r -d '' f; do
  [ "$(basename "$f")" = "$MAIN" ] && continue
  nd="$(codesign -dvv "$f" 2>&1)"
  echo "$nd" | grep -q 'flags=.*runtime' || nested_bad="$nested_bad ${f##*/}(no-runtime)"
  if [ "$APP_SHA" != - ]; then echo "$nd" | grep -q "^TeamIdentifier=$TEAM$" || nested_bad="$nested_bad ${f##*/}(team)"; fi
  [ -z "$(xattr "$f" | grep '^com.apple.cs.' || true)" ] || nested_bad="$nested_bad ${f##*/}(xattr-signature)"
done < <(find "$A/Contents/MacOS" -type f -print0)
check $([ -z "$nested_bad" ] && echo 0 || echo 1) "Every nested library: hardened runtime, same signer, signature inside the file" "${nested_bad:-all $((n - 1)) libraries}"

# ------------------------------------------------------------------------------------------------------ 4. notarization
notarize() { # notarize <file> <label>: submits, waits, saves the log; records the submission
  local f="$1" label="$2" j="$WORK/notary-$2.json" id status
  say "   notarytool submit $(basename "$f") (usually a few minutes)"
  xcrun notarytool submit "$f" "${NOTARY[@]}" --wait --timeout 1h --output-format json > "$j" 2> "$WORK/notary-$label.err" || true
  id="$(plutil -extract id raw -o - "$j" 2>/dev/null || echo '')"; status="$(plutil -extract status raw -o - "$j" 2>/dev/null || echo 'no answer')"
  [ -n "$id" ] && xcrun notarytool log "$id" "${NOTARY[@]}" "$LOGS/notary-$label-$id.json" > /dev/null 2>&1 || true
  printf '%s\t%s\t%s\n' "$label" "${id:-none}" "$status" >> "$SUBMISSIONS"
  check $([ "$status" = Accepted ] && echo 0 || echo 1) "Notarization: $label" "submission ${id:-none}: $status$( [ "$status" = Accepted ] || echo "; $(tail -2 "$WORK/notary-$label.err" | tr '\n' ' ') — see logs/notary-$label-*.json")"
  [ "$status" = Accepted ] || die "Apple's notary service did not accept $label"
}
staple() { # staple <path> <label>
  xcrun stapler staple "$1" > "$LOGS/staple-$2.txt" 2>&1 && xcrun stapler validate "$1" >> "$LOGS/staple-$2.txt" 2>&1 && ok=0 || ok=1
  check $ok "Notarization ticket stapled: $2" "$(tail -1 "$LOGS/staple-$2.txt")"
  [ $ok = 0 ] || die "stapling $2 failed"
}
if [ "$APP_SHA" != - ]; then
  say "4. Notarization of the app"
  ditto -c -k --keepParent "$A" "$WORK/notarize-app.zip"
  notarize "$WORK/notarize-app.zip" "app-$ARCH"
  staple "$A" "app-$ARCH"
else
  say "4. Notarization: skipped (test build — the notary service accepts only Developer ID signatures)"
fi

# ---------------------------------------------------------------------------------------------- 5. distribution files
say "5. Distribution files"
if [ -n "$SUFFIX" ]; then ZIP="$OUT/$BASE.zip"; PKG="$OUT/$BASE.pkg"; DMG="$OUT/$BASE.dmg"
else
  ZIP="$OUT/${ZIP_NAME:-HCG Mail.app.zip}"; PKG="$OUT/${PKG_NAME_OUT:-HCG Mail-$VERSION-osx-$ARCH.pkg}"; DMG="$OUT/HCG Mail-$VERSION-osx-$ARCH.dmg"
fi
rm -f "$ZIP" "$PKG" "$DMG"
ditto -c -k --sequesterRsrc --keepParent "$A" "$ZIP"

# PKG: installs exactly /Applications/HCG Mail.app, no scripts, not relocatable (never redirected to another copy).
root="$WORK/pkgroot"; mkdir -p "$root/Applications"; ditto "$A" "$root/Applications/HCG Mail.app"
pkgbuild --analyze --root "$root" "$WORK/component.plist" > /dev/null
plutil -replace 0.BundleIsRelocatable -bool false "$WORK/component.plist"
plutil -replace 0.BundleHasStrictIdentifier -bool true "$WORK/component.plist"
plutil -replace 0.BundleOverwriteAction -string upgrade "$WORK/component.plist"
pkgbuild --root "$root" --component-plist "$WORK/component.plist" --identifier "$BUNDLE_ID.pkg" --version "$VERSION" \
  --install-location / "$WORK/HCGMail-component.pkg" > "$LOGS/pkgbuild-$ARCH$SUFFIX.log" 2>&1 || { cat "$LOGS/pkgbuild-$ARCH$SUFFIX.log"; die "pkgbuild failed"; }
cat > "$WORK/distribution.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="2">
    <title>HCG Mail</title>
    <options customize="never" require-scripts="false" hostArchitectures="$HOST_ARCH"/>
    <domains enable_anywhere="false" enable_currentUserHome="false" enable_localSystem="true"/>
$( [ -n "$MINOS" ] && printf '    <volume-check><allowed-os-versions><os-version min="%s"/></allowed-os-versions></volume-check>\n' "$MINOS" )
    <choices-outline><line choice="$BUNDLE_ID.pkg"/></choices-outline>
    <choice id="$BUNDLE_ID.pkg" visible="false" title="HCG Mail"><pkg-ref id="$BUNDLE_ID.pkg"/></choice>
    <pkg-ref id="$BUNDLE_ID.pkg" version="$VERSION" onConclusion="none">HCGMail-component.pkg</pkg-ref>
</installer-gui-script>
XML
if [ "$APP_SHA" != - ]; then
  productbuild --distribution "$WORK/distribution.xml" --package-path "$WORK" --sign "$PKG_SHA" --timestamp ${KC[@]+"${KC[@]}"} "$PKG" > "$LOGS/productbuild-$ARCH.log" 2>&1 || { cat "$LOGS/productbuild-$ARCH.log"; die "productbuild failed"; }
else
  productbuild --distribution "$WORK/distribution.xml" --package-path "$WORK" "$PKG" > "$LOGS/productbuild-$ARCH$SUFFIX.log" 2>&1 || { cat "$LOGS/productbuild-$ARCH$SUFFIX.log"; die "productbuild failed"; }
fi
s="$(pkgutil --check-signature "$PKG" 2>&1 || true)"; echo "$s" > "$LOGS/pkg-signature-$ARCH$SUFFIX.txt"
if [ "$APP_SHA" != - ]; then
  check $(echo "$s" | grep -q "1\. $PKG_NAME" && echo 0 || echo 1) "Installer signature" "$(echo "$s" | sed -n 's/^ *1\. //p' | head -1)"
else
  check 0 "Installer signature" "unsigned (test build)"
fi
expanded="$WORK/pkg-expanded"; pkgutil --expand "$PKG" "$expanded"
check $([ -z "$(find "$expanded" -name Scripts -o -name preinstall -o -name postinstall | head -1)" ] && echo 0 || echo 1) "Installer runs no scripts" "no Scripts, preinstall or postinstall"

if [ "$WANT_DMG" = 1 ]; then
stage="$WORK/dmg"; mkdir -p "$stage"; ditto "$A" "$stage/HCG Mail.app"; ln -s /Applications "$stage/Applications"
hdiutil create -volname "HCG Mail" -srcfolder "$stage" -fs HFS+ -format UDZO -imagekey zlib-level=9 -ov "$DMG" > "$LOGS/hdiutil-$ARCH$SUFFIX.log" 2>&1 || { cat "$LOGS/hdiutil-$ARCH$SUFFIX.log"; die "hdiutil failed"; }
if [ "$APP_SHA" != - ]; then codesign --force --timestamp --sign "$APP_SHA" ${KC[@]+"${KC[@]}"} "$DMG" >> "$sign_log" 2>&1 || die "codesign failed on the disk image"; fi
fi

if [ "$APP_SHA" != - ]; then
  say "6. Notarization of the installer$([ "$WANT_DMG" = 1 ] && echo ' and the disk image')"
  notarize "$PKG" "pkg-$ARCH"; staple "$PKG" "pkg-$ARCH"
  if [ "$WANT_DMG" = 1 ]; then notarize "$DMG" "dmg-$ARCH"; staple "$DMG" "dmg-$ARCH"; fi
fi

# -------------------------------------------------------------------------------------------------------- 7. Gatekeeper
say "7. Gatekeeper (spctl, syspolicy_check)"
gk() { # gk <label> <spctl args…>
  local label="$1"; shift
  local out; out="$(spctl "$@" 2>&1)" && ok=0 || ok=1
  echo "$out" > "$LOGS/gatekeeper-$label.txt"
  local src; src="$(echo "$out" | sed -n 's/^source=//p' | head -1)"
  if [ "$APP_SHA" = - ]; then
    check 0 "Gatekeeper: $label (recorded)" "$( [ $ok = 0 ] && echo accepted || echo rejected ) ${src:+($src)} — expected to be rejected for a test build"
  else
    check $([ $ok = 0 ] && [ "$src" = "Notarized Developer ID" ] && echo 0 || echo 1) "Gatekeeper: $label" "$( [ $ok = 0 ] && echo accepted || echo rejected ) (${src:-$(echo "$out" | tail -1)})"
  fi
}
gk "app-$ARCH$SUFFIX" --assess --type execute -vv "$A"
gk "pkg-$ARCH$SUFFIX" --assess --type install -vv "$PKG"
[ "$WANT_DMG" = 1 ] && gk "dmg-$ARCH$SUFFIX" --assess --type open --context context:primary-signature -vv "$DMG"
if command -v syspolicy_check > /dev/null 2>&1; then
  sp="$(syspolicy_check distribution "$A" 2>&1)" && ok=0 || ok=1
  echo "$sp" > "$LOGS/syspolicy_check-$ARCH$SUFFIX.txt"
  if [ "$APP_SHA" = - ]; then check 0 "syspolicy_check distribution (recorded)" "$(echo "$sp" | tail -1)"
  else check $ok "syspolicy_check distribution" "$(echo "$sp" | tail -1)"; fi
fi

# ------------------------------------------------------------------------------------------------------- 8. evidence
say "8. Evidence"
FILES=("$ZIP" "$PKG"); [ "$WANT_DMG" = 1 ] && FILES+=("$DMG")
for f in "${FILES[@]}"; do printf '%s  %s\n' "$(shasum -a 256 "$f" | cut -d' ' -f1)" "$(basename "$f")"; done > "$OUT/SHA256SUMS-osx-$ARCH$SUFFIX.txt"
cat "$OUT/SHA256SUMS-osx-$ARCH$SUFFIX.txt"
/usr/bin/python3 - "$OUT/signing-evidence-osx-$ARCH$SUFFIX.json" "$CHECKS" "$SUBMISSIONS" "$VERSION" "$ARCH" "$APP_NAME" "${TEAM:-}" \
  "${PKG_NAME:-}" "$([ "$APP_SHA" = - ] && echo test || echo developer-id)" "$SHORT" "$MINOS" "${BUILD:-}" "$VERSION_IN_APP" "${FILES[@]}" <<'PY'
import hashlib, json, os, platform, subprocess, sys
out, checks, subs, version, arch, app_name, team, pkg_name, mode, short, minos, build, app_version, *files = sys.argv[1:]
def sha(p, algo):
    h = hashlib.new(algo)
    with open(p, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""): h.update(b)
    return h.hexdigest()
def bundle_version():
    return [x for x in c if x["check"] == "Version: CFBundleVersion"][0]["detail"].split()[0] if any(x["check"] == "Version: CFBundleVersion" for x in c) else None
c = [{"check": l.split("\t")[1], "passed": l.startswith("true"), "detail": l.rstrip("\n").split("\t")[2]} for l in open(checks) if "\t" in l]
s = [dict(zip(("container", "submissionId", "status"), l.rstrip("\n").split("\t"))) for l in open(subs) if "\t" in l]
json.dump({
    "format": "hcg-mail.macos-signing-evidence", "formatVersion": 1, "mode": mode,
    "version": version, "buildNumber": int(build) if build else None, "architecture": arch, "bundleShortVersion": short, "bundleVersion": bundle_version(), "appVersion": app_version, "minimumSystemVersion": minos or None,
    "signing": {"application": app_name, "installer": pkg_name or None, "teamId": team or None, "hardenedRuntime": True,
                "entitlements": ["com.apple.security.cs.allow-jit"]},
    "host": {"macos": platform.mac_ver()[0], "machine": platform.machine(),
             "xcode": subprocess.run(["xcode-select", "-p"], capture_output=True, text=True).stdout.strip()},
    "notarization": s, "checks": c, "failures": sum(not x["passed"] for x in c),
    "files": [{"fileName": os.path.basename(p), "sizeBytes": os.path.getsize(p), "sha256": sha(p, "sha256"), "sha512": sha(p, "sha512")} for p in files],
}, open(out, "w"), indent=2)
print(out)
PY
failed="$(grep -c '^false' "$CHECKS" || true)"
echo; [ "$failed" = 0 ] && echo "All checks passed." || { echo "$failed check(s) FAILED"; exit 1; }
[ "$APP_SHA" = - ] && echo "TEST BUILD: ad-hoc signed and not notarized. Do not distribute these files."
exit 0
