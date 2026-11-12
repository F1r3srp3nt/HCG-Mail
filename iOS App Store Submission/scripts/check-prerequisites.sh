#!/usr/bin/env bash
# Prints the toolchain versions and verifies what the iOS build needs. Changes nothing.
source "$(dirname "$0")/_common.sh"
require_macos

echo "macOS:  $(sw_vers -productVersion)"
echo "Xcode:  $(xcodebuild -version | tr '\n' ' ')"
echo "        $(xcode-select -p)"
echo ".NET:   $(cd "$IOS_ROOT" && dotnet --version)"
echo "Workloads:"
(cd "$IOS_ROOT" && dotnet workload list) | sed 's/^/        /'

status=0
(cd "$IOS_ROOT" && dotnet workload list) | grep -q '^ios ' || { echo "missing: iOS workload — run scripts/install-workload.sh"; status=1; }
xcrun simctl list runtimes | grep -q 'iOS' || { echo "missing: an iOS simulator runtime (Xcode → Settings → Components)"; status=1; }
[[ $status -eq 0 ]] && echo "OK: ready to build HCG Mail for iOS."
exit $status
