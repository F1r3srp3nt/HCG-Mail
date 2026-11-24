#!/usr/bin/env bash
# Build tool of HCGMail.xcodeproj (External Build System targets). Xcode runs it with the build settings in the
# environment; it maps them onto the existing .NET for iOS build, so Xcode builds the real HCG Mail app:
#
#   Scheme "HCG Mail"            destination iOS Simulator → scripts/build-simulator.sh <configuration>
#                                destination "Any iOS Device (arm64)" → unsigned ios-arm64 compile (Release = full AOT,
#                                the App Store configuration; a compile check, not an installable build)
#   Scheme "HCG Mail Simulator"  (--launch) builds for the simulator, installs and starts it (scripts/run-simulator.sh)
#   Product › Clean Build Folder  removes this configuration's build output
#   Product › Archive             not supported: stops with an explanation. The App Store archive and .ipa come from
#                                scripts/export-ipa.sh (its .xcarchive appears in Xcode's Organizer).
#
# It can also be run by hand, e.g.  ACTION=build CONFIGURATION=Debug PLATFORM_NAME=iphonesimulator scripts/xcode-build.sh
set -euo pipefail

launch=0
[[ "${1:-}" == "--launch" ]] && launch=1

action="${ACTION:-build}"; [[ -z "$action" ]] && action=build   # Xcode passes an empty ACTION for "build"
configuration="${CONFIGURATION:-Debug}"
platform="${PLATFORM_NAME:-iphonesimulator}"
here="$(cd "$(dirname "$0")" && pwd)"

# Run the .NET build in a clean environment: Xcode exports hundreds of build settings (CONFIGURATION, SDKROOT, ARCHS,
# CODE_SIGN_*, …) and MSBuild would read every one of them as a property. Keep only what the build needs.
if [[ -z "${HCG_XCODE_CLEAN_ENV:-}" ]]; then
  dotnet_dir=""
  for candidate in "${DOTNET_ROOT:-}/dotnet" "$(command -v dotnet 2>/dev/null || true)" /usr/local/share/dotnet/dotnet \
                   "$HOME/.dotnet/dotnet" /opt/homebrew/bin/dotnet /usr/local/bin/dotnet; do
    if [[ -n "$candidate" && -x "$candidate" ]]; then dotnet_dir="$(dirname "$(readlink -f "$candidate" 2>/dev/null || echo "$candidate")")"; break; fi
  done
  if [[ -z "$dotnet_dir" ]]; then
    echo "error: the .NET SDK was not found (looked in DOTNET_ROOT, PATH, /usr/local/share/dotnet, ~/.dotnet, Homebrew). Install .NET 10: https://dotnet.microsoft.com/download/dotnet/10.0" >&2
    exit 1
  fi
  # The Xcode that runs this build is the one .NET must use (xcode-select honours DEVELOPER_DIR).
  developer_dir="${DEVELOPER_DIR:-$(xcode-select -p 2>/dev/null || true)}"
  keep=(HOME="$HOME" USER="${USER:-}" LOGNAME="${LOGNAME:-}" TMPDIR="${TMPDIR:-/tmp}" LANG="${LANG:-en_US.UTF-8}" SHELL=/bin/bash
        PATH="$dotnet_dir:/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin"
        DEVELOPER_DIR="$developer_dir" MD_APPLE_SDK_ROOT="${developer_dir%/Contents/Developer}"
        ACTION="$action" CONFIGURATION="$configuration" PLATFORM_NAME="$platform" HCG_XCODE_CLEAN_ENV=1
        TARGET_DEVICE_IDENTIFIER="${TARGET_DEVICE_IDENTIFIER:-}" TARGET_DEVICE_PLATFORM_NAME="${TARGET_DEVICE_PLATFORM_NAME:-}"
        CI="${CI:-}"
        DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1)
  [[ -n "${DOTNET_ROOT:-}" ]] && keep+=(DOTNET_ROOT="$DOTNET_ROOT")
  # Optional inputs from the caller's environment: HCG_* (OAuth client IDs, HCG_SHARED_ROOT, …), NuGet and proxies.
  while IFS='=' read -r name _; do
    case "$name" in HCG_*|NUGET_*|HTTPS_PROXY|HTTP_PROXY|NO_PROXY|https_proxy|http_proxy|no_proxy) keep+=("$name=${!name}") ;; esac
  done < <(env)
  exec env -i "${keep[@]}" /bin/bash "$0" "$@"
fi

source "$here/_common.sh"
require_macos

# Errors in the form Xcode lists in the Issue navigator ("error: …").
xcode_errors() { sed -E 's/^(.*): (error|warning) ([A-Z]+[0-9]+): (.*)$/\2: \3: \4 [\1]/'; }

# The .NET build needs Xcode $REQUIRED_XCODE's SDK (toolchain.props). The project can be open in a newer Xcode: the
# compile step then uses an Xcode $REQUIRED_XCODE installed next to it (_common.sh: find_build_xcode), and Simulator,
# simctl and devicectl stay with the Xcode you are using.
check_xcode() {
  RUNNING_XCODE="$(xcodebuild -version 2>/dev/null | awk 'NR==1{print $2}')"
  require_build_xcode
}

case "$action" in
  build)
    check_xcode
    echo "HCG Mail: $configuration build for $platform with .NET $(dotnet --version 2>/dev/null) and the Xcode $BUILD_XCODE_VERSION SDK ($BUILD_XCODE_APP), from Xcode ${RUNNING_XCODE:-?}"
    case "$platform" in
      iphonesimulator)
        app="$(HCG_BUILD_XCODE="$BUILD_XCODE_APP" "$here/build-simulator.sh" "$configuration" 2> >(xcode_errors >&2) | tail -1)"
        echo "HCG Mail app (simulator): $app"
        if [[ $launch -eq 1 ]]; then
          # Boot, install and start after the build has returned: a simulator booted from inside an Xcode build step
          # can wait on the build itself, and a cold boot takes minutes. Progress and result go to launch.log.
          log="$OUT_ROOT/simulator/launch.log"
          mkdir -p "$(dirname "$log")"
          # run-simulator.sh --show opens Simulator.app once the device has booted, on that device. Opened earlier,
          # Simulator.app cold-boots its own default device next to ours (CI run 45063307048: load average 541).
          # Fully detached (new session, double fork, no inherited file descriptors): xcodebuild waits for every process
          # that still holds one of its descriptors or belongs to the build's process group.
          HCG_IOS_APP="$app" HCG_IOS_CONFIGURATION="$configuration" /usr/bin/python3 -I -c '
import os, sys
log, script = sys.argv[1], sys.argv[2]
if os.fork(): sys.exit(0)
os.setsid()
if os.fork(): os._exit(0)
out = os.open(log, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o644)
null = os.open(os.devnull, os.O_RDONLY)
for target, source in ((0, null), (1, out), (2, out)):
    if source != target:
        os.dup2(source, target)
    os.set_inheritable(target, True)
os.closerange(3, 1024)
cmd = "echo \"[$(date +%T)] boot, install, start\"; \"$0\" --show; rc=$?; " \
      "if [ $rc -eq 0 ]; then echo \"[$(date +%T)] HCG-LAUNCH: started\"; else echo \"[$(date +%T)] HCG-LAUNCH: failed ($rc)\"; fi"
os.execv("/bin/bash", ["/bin/bash", "-c", cmd, script])
' "$log" "$here/run-simulator.sh"
          echo "HCG Mail is being installed and started in the Simulator (progress: $log)."
        fi
        ;;
      iphoneos)
        if [[ $launch -eq 1 ]]; then
          echo "error: the \"HCG Mail Simulator\" scheme runs in the iOS Simulator only; choose a simulator as the destination." >&2
          exit 1
        fi
        # A connected iPhone/iPad chosen as the run destination: build signed for development, check, install and
        # start it there (scripts/install-device.sh finds the signing; DEVICE_INSTALL.md §3 if it has none).
        # If Xcode does not pass the destination, the one connected iPhone/iPad is used. CI (no devices) and
        # "Any iOS Device" with nothing connected keep the unsigned compile check below.
        target="${TARGET_DEVICE_IDENTIFIER:-}"
        [[ "${TARGET_DEVICE_PLATFORM_NAME:-iphoneos}" == "iphoneos" ]] || target=""
        if [[ -z "$target" && -z "${CI:-}" ]]; then
          list="$(mktemp "${TMPDIR:-/tmp}/hcg-devices.XXXXXX")"
          xcrun devicectl list devices --json-output "$list" >/dev/null 2>&1 || true
          target="$(python3 -I -c '
import json, sys
try: devices = json.load(open(sys.argv[1])).get("result", {}).get("devices", [])
except (OSError, ValueError): devices = []
ok = [d.get("hardwareProperties", {}).get("udid", "") for d in devices
      if d.get("hardwareProperties", {}).get("platform") == "iOS"
      and d.get("connectionProperties", {}).get("tunnelState") == "connected"]
print(ok[0] if len(ok) == 1 else "")' "$list" || true)"
          rm -f "$list"
        fi
        if [[ -n "$target" ]]; then
          echo "HCG Mail: build, sign, install and start on the iPhone/iPad $target"
          HCG_BUILD_XCODE="$BUILD_XCODE_APP" HCG_IOS_CONFIGURATION="$configuration" "$here/install-device.sh" "$target"
          exit 0
        fi
        out="$OUT_ROOT/device/$configuration"
        # Unsigned: proves the app compiles for devices (Release = the AOT App Store configuration). Signed, installable
        # and App Store builds come from scripts/export-ipa.sh, which needs your signing identity and profile.
        set -o pipefail
        ios_dotnet build "$APP_PROJECT" -c "$configuration" -f "$TFM" -r ios-arm64 -p:EnableCodeSigning=false -o "$out" 2>&1 | xcode_errors
        app="$(find "$out" -maxdepth 1 -name '*.app' -type d | head -1)"
        [[ -d "$app" ]] || { echo "error: no .app bundle in $out" >&2; exit 1; }
        echo "HCG Mail app (device, unsigned — compile check only): $app"
        echo "warning: \"Any iOS Device\" builds are a compile check and are not signed. Choose your connected iPhone as the destination to install HCG Mail on it, or run scripts/install-device.sh."
        ;;
      *)
        echo "error: unsupported platform '$platform' — choose an iOS Simulator or Any iOS Device as the destination." >&2
        exit 1
        ;;
    esac
    ;;
  clean)
    rm -rf "$OUT_ROOT/simulator/$configuration" "$OUT_ROOT/device/$configuration"
    dotnet clean "$APP_PROJECT" -c "$configuration" -f "$TFM" -v quiet >/dev/null 2>&1 || true
    echo "HCG Mail: removed the $configuration build output."
    ;;
  install)
    echo "error: Product › Archive is not supported by this Xcode project: the app is built by .NET for iOS, and Xcode cannot package an External Build System target as an App Store archive. Run scripts/export-ipa.sh instead — it builds the signed App Store .ipa and an .xcarchive that appears in Xcode's Organizer (see APP_STORE_SUBMISSION.md)." >&2
    exit 1
    ;;
  *)
    echo "HCG Mail: nothing to do for '$action'."
    ;;
esac
