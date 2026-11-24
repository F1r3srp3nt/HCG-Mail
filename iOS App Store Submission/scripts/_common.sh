#!/usr/bin/env bash
# Shared settings for the HCG Mail iOS scripts. Source this file; do not run it.
set -euo pipefail

IOS_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "$IOS_ROOT/.." && pwd)"
APP_PROJECT="$IOS_ROOT/src/HCG.Mail.iOS/HCG.Mail.iOS.csproj"
TFM="net10.0-ios"
BUNDLE_ID="${HCG_IOS_BUNDLE_ID:-com.hakmicapitalgroup.hcgmail}"
# Build output stays out of the repository (artifacts/ is ignored by git).
OUT_ROOT="$REPO_ROOT/artifacts/ios"

# Pinned toolchain, read from the single source of truth (toolchain.props).
toolchain_value() { sed -n "s:.*<$1>\(.*\)</$1>.*:\1:p" "$IOS_ROOT/toolchain.props"; }
REQUIRED_XCODE="$(toolchain_value HcgRequiredXcode)"
IOS_WORKLOAD_MANIFEST="$(toolchain_value HcgIosWorkloadManifest)"
MINIMUM_IOS="$(toolchain_value HcgMinimumIos)"

# --- The Xcode that compiles the app -------------------------------------------------------------------------------
# The pinned .NET iOS workload builds with Xcode $REQUIRED_XCODE's SDK. That Xcode can sit next to a newer default
# Xcode: only the .NET build uses it (DEVELOPER_DIR / MD_APPLE_SDK_ROOT for `dotnet`); simctl, devicectl and Simulator
# keep using the selected Xcode. Order: HCG_BUILD_XCODE (path to an Xcode .app), the selected Xcode if it is
# $REQUIRED_XCODE, then any Xcode $REQUIRED_XCODE in /Applications or ~/Applications. Sets BUILD_XCODE_APP and
# BUILD_XCODE_VERSION; returns 1 with BUILD_XCODE_PROBLEM when there is none.
xcode_app_version() {
  python3 -I -c 'import plistlib, sys; print(plistlib.load(open(sys.argv[1], "rb")).get("CFBundleShortVersionString", ""))' \
    "$1/Contents/Info.plist" 2>/dev/null || true
}
is_required_xcode() { [[ "$1" == "$REQUIRED_XCODE" || "$1" == "$REQUIRED_XCODE".* ]]; }
find_build_xcode() {
  local selected candidate version root
  local -a candidates roots
  BUILD_XCODE_APP=""; BUILD_XCODE_VERSION=""; BUILD_XCODE_PROBLEM=""
  if [[ -n "${HCG_BUILD_XCODE:-}" ]]; then
    version="$(xcode_app_version "$HCG_BUILD_XCODE")"
    if is_required_xcode "$version"; then BUILD_XCODE_APP="$HCG_BUILD_XCODE"; BUILD_XCODE_VERSION="$version"; return 0; fi
    BUILD_XCODE_PROBLEM="HCG_BUILD_XCODE=$HCG_BUILD_XCODE is Xcode ${version:-?}, not $REQUIRED_XCODE"; return 1
  fi
  selected="${DEVELOPER_DIR:-$(xcode-select -p 2>/dev/null || true)}"; selected="${selected%/Contents/Developer}"
  candidates=("$selected")
  # HCG_XCODE_SEARCH_DIRS: colon-separated folders to look in (default /Applications and ~/Applications).
  IFS=':' read -r -a roots <<<"${HCG_XCODE_SEARCH_DIRS:-/Applications:$HOME/Applications}"
  for root in "${roots[@]}"; do for candidate in "$root"/Xcode*.app; do candidates+=("$candidate"); done; done
  for candidate in "${candidates[@]}"; do
    [[ -d "$candidate/Contents/Developer" ]] || continue
    version="$(xcode_app_version "$candidate")"
    if is_required_xcode "$version"; then BUILD_XCODE_APP="$candidate"; BUILD_XCODE_VERSION="$version"; return 0; fi
  done
  BUILD_XCODE_PROBLEM="HCG Mail's .NET iOS workload ($IOS_WORKLOAD_MANIFEST) builds with Xcode $REQUIRED_XCODE, and none is installed (the selected Xcode is $(xcode_app_version "$selected")). Keep your Xcode and add Xcode $REQUIRED_XCODE next to it: download \"Xcode $REQUIRED_XCODE\" from https://developer.apple.com/download/all/, unpack it, rename it Xcode_$REQUIRED_XCODE.app, move it to /Applications, then run once: sudo DEVELOPER_DIR=/Applications/Xcode_$REQUIRED_XCODE.app/Contents/Developer xcodebuild -license accept"
  return 1
}
# Runs dotnet with the build Xcode (call find_build_xcode first).
ios_dotnet() {
  DEVELOPER_DIR="$BUILD_XCODE_APP/Contents/Developer" MD_APPLE_SDK_ROOT="$BUILD_XCODE_APP" dotnet "$@"
}
# The pinned iOS workload must be the one installed (dotnet would otherwise suggest `workload restore`, which installs
# the newest workload, built for a newer Xcode).
require_ios_workload() {
  local installed
  installed="$(cd "$IOS_ROOT" && dotnet workload list 2>/dev/null | awk '$1=="ios"{print $2}')"
  if [[ "$installed" != "$IOS_WORKLOAD_MANIFEST"/* ]]; then
    echo "error: the .NET iOS workload $IOS_WORKLOAD_MANIFEST is needed (installed: ${installed:-none}). In Terminal, once: \"$IOS_ROOT/scripts/install-workload.sh\"" >&2
    exit 1
  fi
}
require_build_xcode() {
  find_build_xcode || { echo "error: $BUILD_XCODE_PROBLEM" >&2; exit 1; }
  require_ios_workload
}

simulator_rid() {
  if [[ "$(uname -m)" == "arm64" ]]; then echo "iossimulator-arm64"; else echo "iossimulator-x64"; fi
}

require_macos() {
  if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "error HCG002: building the iOS app needs macOS with Xcode $REQUIRED_XCODE (Apple's iOS SDK and simulators only exist there)." >&2
    echo "       The shared code and the mobile UI tests run anywhere: dotnet test \"$IOS_ROOT/tests/HCG.Mail.Mobile.Tests\"" >&2
    exit 2
  fi
}
