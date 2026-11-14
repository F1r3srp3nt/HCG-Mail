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
| Build host | **macOS only** (Xcode 26.3 + .NET 10 + .NET iOS workload 26.2.10233, see `iOS App Store Submission/toolchain.props`) |
| Must not be selected | `Windows-executable-Application/src/HCG.Mail.App` (desktop, `WinExe`), any library, any test project |

The repository describes this machine-readably in **`hcg-apps.json`** (repository root, schema `hcg-apps/1`):
applications with project, solution, framework, build host, toolchain file and build commands; plus the lists of
libraries and test projects. `iOS App Store Submission/scripts/verify-app-selection.py` checks on every CI run that
the manifest and a plain scan of all `*.csproj` files agree: exactly **one** executable targets `net10.0-ios`.

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

Optional: `HCG_MICROSOFT_CLIENT_ID=<GUID>` enables Microsoft sign-in in the build.

## 4. Diagnostics Maker should pass through unchanged

| Code / source | Meaning |
|---|---|
| `HCG001` | shared sources missing (layout / `HcgSharedRoot`) |
| `HCG002` | not running on macOS |
| `HCG003` | minimum iOS lowered below 16.0 |
| `HCG004` | device build without signing settings (informational) |
| `NETSDK1147` | .NET iOS workload missing → `scripts/install-workload.sh` |
| "requires Xcode 26.3" | wrong Xcode selected → `xcode-select` |
| "No valid iOS code signing keys" | no signing identity/profile on the Mac (devices only) |

## Not Maker's job

Maker should not convert, re-target or copy source for iOS: the iOS app already exists and builds with Apple's
toolchain through `dotnet build`. It must not upload to TestFlight or App Store Connect; distribution stays a
manual, founder-approved step.
