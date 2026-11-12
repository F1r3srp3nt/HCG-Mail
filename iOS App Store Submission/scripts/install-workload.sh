#!/usr/bin/env bash
# Installs the pinned .NET iOS workload (manifest version in workload-rollback.json, which requires the Xcode version
# documented in the README). Uses manifest mode so the exact version is installed, not the newest available.
source "$(dirname "$0")/_common.sh"
require_macos
cd "$IOS_ROOT"
dotnet workload config --update-mode manifests
dotnet workload install ios --from-rollback-file "$IOS_ROOT/scripts/workload-rollback.json" --skip-sign-check
dotnet workload list
