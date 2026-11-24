# Handoff: building HCG Mail for iOS with HCG Executable Maker

**Audience:** whoever maintains HCG Executable Maker. **Status:** the repository side is ready and verified in CI.
Whether Maker already behaves as described below **could not be checked**, because Maker is not in this repository
and was not available to the session that wrote this. Steps 1–4 are what to verify (and, if needed, change) in Maker.

## What Maker must select when the whole HCG-Mail repository is imported

| | |
|---|---|
| Application | **HCG Mail for iPhone and iPad** (`id: hcg-mail-ios` in `hcg-apps.json`) |
| Project (entry point) | `iOS App Store Submission/src/HCG.Mail.iOS/HCG.Mail.iOS.csproj` |
| Solution | `iOS App Store Submission/HCG.Mail.iOS.sln` |
| Framework / output | `net10.0-ios`, `OutputType Exe`, bundle `com.hakmicapitalgroup.hcgmail` |
| Build host | **macOS only** (Xcode 27.2 + .NET 10 + .NET iOS workload 27.2.10000, see `iOS App Store Submission/toolchain.props`) |
| Must not be selected | `Windows-executable-Application/src/HCG.Mail.App` (desktop, `WinExe`), any library, any test project |
| Xcode project | `iOS App Store Submission/HCGMail.xcodeproj` is only an entry point that runs the same .NET build (`scripts/xcode-build.sh`). Maker should keep building the `.csproj` above; if it drives `xcodebuild`, use scheme `HCG Mail` for builds — `archive` is refused by design, signed builds come from `scripts/export-ipa.sh` |

The repository describes this machine-readably in **`hcg-apps.json`** (repository root, schema `hcg-apps/1`):
applications with project, solution, framework, build host, toolchain file and build commands; plus the lists of
libraries and test projects. `iOS App Store Submission/scripts/verify-app-selection.py` checks on every CI run that
the manifest and a plain scan of all `*.csproj` files agree: exactly **one** executable targets `net10.0-ios`.

## Status of Mobile Build Studio with this repository (checked 2026-11-21, read-only)

The checks were made against HCG Executable Maker at commit `2842998` by reading its source. Maker itself was not
run, and it was not changed.

**Before `HCGMail.xcodeproj` (main as of PR #16):**
* Maker's special case for HCG Mail applies (`MobileProjectInspector.Match`: a .NET iOS head next to a desktop app
  scores 90).
* The .NET iOS app `HCG.Mail.iOS.csproj` is attached for Mobile Build Studio.
* Maker builds it with `dotnet build` / `dotnet publish -p:ArchiveOnBuild=true`, then `xcodebuild -exportArchive`.
* Signing: identity and profile, plus team and export method. Nothing is uploaded.

**With `HCGMail.xcodeproj` (PR #17) — confirmed incompatibility:**
1. Any `.xcodeproj` that is not macOS-only adds a score-105 marker (`MobileProjectInspector.cs:25`). This disables the
   HCG Mail special case (`:28`), and the Xcode route is taken instead of the .NET iOS head (`:210`).
2. `XcodeProjectAnalyzer` only reads `PBXNativeTarget` application targets (`XcodeProjectAnalyzer.cs:58`).
   `HCGMail.xcodeproj` has External Build System (`PBXLegacyTarget`) targets, so Maker finds "no iOS application
   target".
3. As a result, every iOS output in Mobile Build Studio is blocked before any command runs.
4. Maker does not read `hcg-apps.json`.

**What fixes it (a change in Maker, which needs separate approval).** Any one of these is enough:
* honour `hcg-apps.json`, using its `project` for the iOS app;
* let a `net*-ios` `Exe` project win over an `.xcodeproj` whose targets are all `PBXLegacyTarget`;
* add `HCGMail.xcodeproj` to Maker's "HCG Mail like" test fixture and keep its expectation (`InspectorId == "dotnet"`,
  `Mobile.DotNetIos` attached).

Counting legacy targets as apps is not a fix: Maker would then run `xcodebuild archive`, which this repository
refuses by design.

**Also out of date in Maker's fixture:**
* It still contains `scripts/workload-rollback.json`, which this repository removed.
* The pinned workload version (`HcgIosWorkloadManifest` 27.2.10000) is now only in `toolchain.props`, so Maker does
  not enforce it for this repository.

## 1. Selection rule (verify in Maker)

Use the first rule that applies:

1. If `hcg-apps.json` exists, take the application whose `platforms` contains `"ios"` and build its `project`.
2. Otherwise choose the project with `<OutputType>Exe</OutputType>` and a `<TargetFramework>` ending in `-ios`.
   Exactly one exists. **Do not** pick "the first executable project" — that would be the desktop app.

If Maker currently picks the first `.sln`/`.csproj` it finds, or ranks by folder name, it will choose the wrong
application (the desktop solution sorts first). That is the change to make.

## 2. Keep the repository layout (verify in Maker)

The iOS app references the shared mail engine and view models in `Windows-executable-Application/src/` through
`$(HcgSharedSource)`, which `iOS App Store Submission/Directory.Build.props` resolves **relative to itself**. Build in
place inside the full checkout. If Maker copies only the iOS folder to a work directory, it must pass the shared
folder explicitly:

```bash
dotnet build "<copy>/src/HCG.Mail.iOS/HCG.Mail.iOS.csproj" -p:HcgSharedRoot="<checkout>/Windows-executable-Application"
# or: export HCG_SHARED_ROOT="<checkout>/Windows-executable-Application"
```

Without it the build stops with **`error HCG001`** (shared sources not found) — not a Maker bug, a missing input.

## 3. Build commands (run on a Mac, working directory `iOS App Store Submission`)

```bash
scripts/install-workload.sh          # pinned .NET iOS workload (once per Mac / SDK update)
scripts/doctor.sh                    # exit code 0 = ready; prints which category fails otherwise

# Simulator app (no signing identity needed)
scripts/build-simulator.sh Debug     # last line of output = path of HCGMail.iOS.app

# Device build / signed IPA (never uploaded)
HCG_IOS_SIGNING_IDENTITY="Apple Distribution: Hakmi Capital Group (<TEAMID>)" \
HCG_IOS_PROVISIONING_PROFILE="<profile name or UUID>" \
HCG_IOS_BUILD_NUMBER=<n> \
scripts/export-ipa.sh                # → artifacts/ios/ipa/*.ipa
```

Optional: `HCG_MICROSOFT_CLIENT_ID=<GUID>` enables Microsoft sign-in in the build (or commit the ID in
`Windows-executable-Application/oauth-clients.props`). For the **desktop** app Maker can also pass
`HCG_GOOGLE_CLIENT_ID` and `HCG_GOOGLE_CLIENT_SECRET` (keep the secret in Maker's secret store; never write it to the
log or the repository). Every build prints an `HCG020` line naming which providers it was configured for. Pass that line
through; it is how to tell whether the values reached the build. See
`Windows-executable-Application/docs/OAUTH_ACTIVATION_CHECKLIST.md`.

## 4. Diagnostics Maker should pass through unchanged

| Code / source | Meaning |
|---|---|
| `HCG001` | shared sources missing (layout / `HcgSharedRoot`) |
| `HCG002` | not running on macOS |
| `HCG003` | minimum iOS lowered below 16.0 |
| `HCG004` | device build without signing settings (informational) |
| `NETSDK1147` | .NET iOS workload missing → `scripts/install-workload.sh` |
| "requires Xcode 27.2" | wrong Xcode selected → `xcode-select` |
| "No valid iOS code signing keys" | no signing identity/profile on the Mac (devices only) |

## Not Maker's job

Maker should not convert, re-target or copy source for iOS: the iOS app already exists and builds with Apple's
toolchain through `dotnet build`. It must not upload to TestFlight or App Store Connect; distribution stays a
manual, founder-approved step.
