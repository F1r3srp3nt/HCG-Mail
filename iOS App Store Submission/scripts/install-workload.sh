#!/usr/bin/env bash
# Installs the pinned .NET iOS workload (HcgIosWorkloadManifest in toolchain.props; it requires the Xcode version in
# HcgRequiredXcode). Uses manifest mode so exactly that version is installed, not the newest available.
source "$(dirname "$0")/_common.sh"
require_macos
cd "$IOS_ROOT"
rollback="$(mktemp -t hcg-ios-workload).json"
printf '{ "microsoft.net.sdk.ios": "%s/10.0.100" }\n' "$IOS_WORKLOAD_MANIFEST" > "$rollback"
# The .NET installer for macOS puts .NET in /usr/local/share/dotnet, which only an administrator can change.
dotnet_root="$(dirname "$(readlink -f "$(command -v dotnet)")")"
as_admin=(); [[ -w "$dotnet_root" ]] || { as_admin=(sudo); echo "Installing into $dotnet_root (your Mac password is needed)."; }
${as_admin[@]+"${as_admin[@]}"} dotnet workload config --update-mode manifests
${as_admin[@]+"${as_admin[@]}"} dotnet workload install ios --from-rollback-file "$rollback" --skip-sign-check
rm -f "$rollback"
dotnet workload list
