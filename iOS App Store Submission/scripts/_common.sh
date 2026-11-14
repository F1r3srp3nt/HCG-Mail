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
