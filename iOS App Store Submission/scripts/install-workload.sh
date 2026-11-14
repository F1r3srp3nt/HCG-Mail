#!/usr/bin/env bash
# Installs the pinned .NET iOS workload (HcgIosWorkloadManifest in toolchain.props; it requires the Xcode version in
# HcgRequiredXcode). Uses manifest mode so exactly that version is installed, not the newest available.
source "$(dirname "$0")/_common.sh"
require_macos
cd "$IOS_ROOT"
rollback="$(mktemp -t hcg-ios-workload).json"
printf '{ "microsoft.net.sdk.ios": "%s/10.0.100" }\n' "$IOS_WORKLOAD_MANIFEST" > "$rollback"
dotnet workload config --update-mode manifests
dotnet workload install ios --from-rollback-file "$rollback" --skip-sign-check
rm -f "$rollback"
dotnet workload list
