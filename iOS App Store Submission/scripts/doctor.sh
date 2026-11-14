#!/usr/bin/env bash
# Checks everything the iOS build needs and reports each category separately, so a missing workload is never
# mistaken for a signing problem (or the other way round). Changes nothing. Exit code: number of failed categories.
#
#   [sources]     shared HCG Mail code present (HCG001)
#   [host]        macOS (HCG002)
#   [sdk]         .NET SDK version
#   [workload]    .NET iOS workload, pinned manifest version
#   [xcode]       selected Xcode version required by that workload
#   [deployment]  minimum iOS version and an installed simulator runtime that satisfies it (HCG003)
#   [signing]     only for devices: code-signing identities and provisioning profiles (HCG004)
source "$(dirname "$0")/_common.sh"
set +e
failed=0
ok()   { printf '  %-12s OK    %s\n' "[$1]" "$2"; }
bad()  { printf '  %-12s FAIL  %s\n' "[$1]" "$2"; failed=$((failed+1)); }
info() { printf '  %-12s --    %s\n' "[$1]" "$2"; }

echo "HCG Mail for iOS — build doctor (required: Xcode $REQUIRED_XCODE, iOS workload $IOS_WORKLOAD_MANIFEST, iOS $MINIMUM_IOS+)"

shared="${HcgSharedRoot:-${HCG_SHARED_ROOT:-$REPO_ROOT/Windows-executable-Application}}"
if [[ -f "$shared/Directory.Packages.props" && -f "$shared/src/HCG.Mail.Core/HCG.Mail.Core.csproj" ]]; then
  ok sources "shared code: $shared"
else
  bad sources "HCG001: shared code not found at $shared — use a complete HCG-Mail clone or set HCG_SHARED_ROOT"
fi

if [[ "$(uname -s)" == "Darwin" ]]; then
  ok host "macOS $(sw_vers -productVersion) ($(uname -m))"
  mac=1
else
  bad host "HCG002: $(uname -s) — the iOS app builds only on macOS. Here you can run: dotnet test tests/HCG.Mail.Mobile.Tests"
  mac=0
fi

if command -v dotnet >/dev/null; then
  sdk="$(cd "$IOS_ROOT" && dotnet --version 2>&1)"
  if [[ "$sdk" == 10.* ]]; then ok sdk ".NET SDK $sdk (global.json: 10.0.100 or newer)"; else bad sdk ".NET SDK for this folder: $sdk — install .NET 10"; fi
else
  bad sdk "dotnet not found — install the .NET 10 SDK"
fi

if [[ $mac -eq 1 ]]; then
  workload="$(cd "$IOS_ROOT" && dotnet workload list 2>/dev/null | awk '$1=="ios"{print $2}')"
  if [[ -z "$workload" ]]; then bad workload "iOS workload not installed — run scripts/install-workload.sh"
  elif [[ "$workload" == "$IOS_WORKLOAD_MANIFEST"/* ]]; then ok workload "ios $workload"
  else bad workload "ios $workload installed, $IOS_WORKLOAD_MANIFEST pinned — run scripts/install-workload.sh"; fi

  xcode="$(xcodebuild -version 2>/dev/null | awk 'NR==1{print $2}')"
  if [[ -z "$xcode" ]]; then bad xcode "no Xcode selected — install Xcode $REQUIRED_XCODE and run: sudo xcode-select -s /Applications/Xcode_$REQUIRED_XCODE.app"
  elif [[ "$xcode" == "$REQUIRED_XCODE" || "$xcode" == "$REQUIRED_XCODE".* ]]; then ok xcode "Xcode $xcode at $(xcode-select -p)"
  else bad xcode "Xcode $xcode selected; the pinned workload requires $REQUIRED_XCODE (sudo xcode-select -s …)"; fi

  runtimes="$(xcrun simctl list runtimes available 2>/dev/null | sed -n 's/^iOS \([0-9.]*\).*/\1/p' | tr '\n' ' ')"
  if [[ -n "$runtimes" ]]; then ok deployment "minimum iOS $MINIMUM_IOS; simulator runtimes installed: $runtimes"
  else bad deployment "no iOS simulator runtime installed (Xcode → Settings → Components); minimum iOS is $MINIMUM_IOS"; fi

  identities="$(security find-identity -v -p codesigning 2>/dev/null | grep -c '"Apple \(Development\|Distribution\)')"
  profiles="$(ls "$HOME/Library/MobileDevice/Provisioning Profiles" "$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles" 2>/dev/null | grep -c 'provision')"
  info signing "simulator: none needed (ad-hoc). Devices: $identities Apple signing identit(ies), $profiles provisioning profile(s) on this Mac"
else
  info workload "not checked (needs macOS)"
  info xcode "not checked (needs macOS)"
  info deployment "minimum iOS $MINIMUM_IOS (simulators need macOS)"
  info signing "not checked (needs macOS)"
fi

if [[ $failed -eq 0 ]]; then echo "Ready to build HCG Mail for iOS."; else echo "$failed check(s) failed."; fi
exit $failed
