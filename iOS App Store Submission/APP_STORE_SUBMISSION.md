# HCG Mail for iPhone and iPad — App Store submission handoff

**Audience:** the founder, on the Mac that holds HCG's Apple signing certificate.
**Release path:** `scripts/export-ipa.sh` → upload the `.ipa` with **Transporter** → TestFlight → App Review.
Upload, TestFlight and submission for review are manual steps; nothing in this repository does them.

## Status

| | |
|---|---|
| Bundle ID | `com.hakmicapitalgroup.hcgmail` (iPhone and iPad, iOS 16.0 or later) |
| Version / build | marketing version **0.1.0** (shared `<Version>` in `Windows-executable-Application/Directory.Build.props`, overridable with `HCG_IOS_MARKETING_VERSION`), build number from `HCG_IOS_BUILD_NUMBER` |
| Toolchain | Xcode **26.3** + .NET 10 + .NET iOS workload **26.2.10233** (`toolchain.props`) |
| Apple's upload minimum | Since 28 April 2026, uploads must be built with Xcode 26 and the iOS 26 SDK or later ([Apple](https://developer.apple.com/news/upcoming-requirements/)). The pinned toolchain meets it. `verify-ios-build.py` fails any build whose SDK is older. |
| Verified in CI (macOS, fresh checkout, no signing) | See [What has been verified](#what-has-been-verified). |
| Not yet done | Signing, `.xcarchive` and App Store `.ipa`. These need HCG's distribution certificate, which exists only on your Mac ([step 2](#2-build-the-signed-archive-and-ipa-on-your-mac)). |

## 1. Open and build in Xcode

**Project:** `iOS App Store Submission/HCGMail.xcodeproj`. **Schemes:** `HCG Mail`, `HCG Mail Simulator`.

Setup, once per Mac:

1. Install Xcode 26.3.
2. Install the .NET 10 SDK.
3. Run `scripts/install-workload.sh` in this folder.

`scripts/doctor.sh` should then report "Ready". Open the project with `open HCGMail.xcodeproj`, or double-click it.

The project uses Xcode *External Build System* targets. They run `scripts/xcode-build.sh`, which runs the existing
.NET build with the Xcode you opened. There is no second copy of the app, and nothing to keep in sync.

| In Xcode | Works? | What happens |
|---|---|---|
| Scheme **HCG Mail**, destination an iPhone/iPad **simulator**, **Build** (⌘B) | ✅ | Builds the app. Debug: `artifacts/ios/simulator/Debug/HCGMail.iOS.app`; Release: `…/Release/`. |
| Scheme **HCG Mail Simulator**, **Build** (⌘B) | ✅ | Builds, opens Simulator, then installs and starts HCG Mail on the newest iPhone simulator in the background. The build finishes within seconds. The app appears once the simulator has booted: seconds if it is already running, longer from cold. Progress: `artifacts/ios/simulator/launch.log`. |
| Scheme **HCG Mail**, destination **Any iOS Device (arm64)**, **Build** | ✅ compile check | Builds an unsigned `ios-arm64` app in `artifacts/ios/device/<config>/`. Release is the full AOT App Store configuration and takes about 15 minutes. It cannot be installed, because it is unsigned. |
| **Product › Clean Build Folder** | ✅ | Removes this configuration's build output. |
| **Run** (⌘R), **Test**, **Profile**, debugger | ❌ | Xcode cannot run the product of an External Build System target. Use the `HCG Mail Simulator` scheme. On a device, use `dotnet build … -t:Run` (README, *Run on your own iPhone or iPad*). |
| **Product › Archive** | ❌ by design | Stops with "Product › Archive is not supported…" and points to `scripts/export-ipa.sh`. Xcode can only make a *generic* archive from this kind of target, and Organizer cannot upload that to the App Store. |
| *Signing & Capabilities* tab | not used | Leave it empty. Signing is done by `export-ipa.sh`. |

Notes:

* Xcode does not read your shell's environment when started from the Dock. Microsoft sign-in therefore reaches an
  Xcode build only through `oauth-clients.props` or `oauth-clients.local.props`.
* `xcodebuild` from Terminal passes `HCG_*` variables through.
* Building in Xcode other than 26.3 stops with an explicit error, because the pinned .NET workload requires 26.3.

## 2. Build the signed archive and .ipa on your Mac

This is the step that could not be done here: it needs HCG's private signing key.

### 2a. One-time Apple setup (developer.apple.com and App Store Connect)

1. **App ID:** Certificates, Identifiers & Profiles → Identifiers. Create the explicit App ID
   `com.hakmicapitalgroup.hcgmail` with **Keychain Sharing** enabled, or confirm it already exists.
2. **App record**, and the check that it matches the bundle ID:
   * App Store Connect → Apps → **+ New App**.
   * Platform iOS, name "HCG Mail" (or the name you want, if it is taken), primary language, a SKU (for example
     `hcg-mail-ios`), and **Bundle ID `com.hakmicapitalgroup.hcgmail`**.
   * If the app record already exists, open **App Information → General** and confirm the *Bundle ID* reads exactly
     `com.hakmicapitalgroup.hcgmail`.
   * A record made with another bundle ID cannot receive this build. The bundle ID of a record cannot be changed
     after a build is uploaded.
3. **Certificate:** an **Apple Distribution** certificate whose private key is in this Mac's login keychain. Check
   with `security find-identity -v -p codesigning`.
4. **Profile:**
   * Profiles → **+** → *Distribution → App Store Connect*.
   * App ID `com.hakmicapitalgroup.hcgmail`, and the certificate from step 3.
   * Name it, for example, `HCG Mail App Store`.
   * Download it and double-click it, or use Xcode › Settings › Accounts › *Download Manual Profiles*.

### 2b. Build

```bash
cd "iOS App Store Submission"
git pull                                          # the commit you intend to ship
security find-identity -v -p codesigning          # copy the exact "Apple Distribution: …" name
export HCG_IOS_SIGNING_IDENTITY="Apple Distribution: Hakmi Capital Group (<TEAMID>)"
export HCG_IOS_PROVISIONING_PROFILE="HCG Mail App Store"   # the profile's name, UUID or file path
export HCG_IOS_BUILD_NUMBER=1                      # +1 for every upload
export HCG_IOS_MARKETING_VERSION=0.1.0             # the version shown on the App Store; 1.0.0 if you prefer
scripts/export-ipa.sh
```

What the script does, in order:

1. **Toolchain** (`doctor.sh`): Xcode 26.3, workload 26.2.10233.
2. **Signing preflight**, before anything is built. It checks that:
   * the identity is in the keychain;
   * the profile's App ID is exactly `<TEAMID>.com.hakmicapitalgroup.hcgmail`;
   * the profile is from the same team, is an *App Store* profile, is not expired, allows the Keychain group, and
     contains this certificate.

   Any mismatch stops here with the reason and the fix.
3. **Build:** Release, `ios-arm64`, full AOT, signed, `ArchiveOnBuild` and `BuildIpa`.
4. **Verify** the signed `.ipa` and the archived app. A failure means **do not upload**. The checks are:
   * bundle ID, marketing version and build number;
   * minimum iOS 16.0, iPhone + iPad;
   * built with the iOS 26.x SDK;
   * app icon compiled (`Assets.car`, `CFBundleIconName`, source 1024×1024 without alpha);
   * `LaunchScreen.storyboardc`;
   * `PrivacyInfo.xcprivacy` present (no tracking, no collected data, required-reason APIs declared);
   * Microsoft URL scheme;
   * signature valid (`codesign --verify --deep --strict`), signed by Apple Distribution;
   * entitlements exactly `application-identifier`, team and Keychain groups
     `<TEAMID>.com.hakmicapitalgroup.hcgmail` + `<TEAMID>.com.microsoft.adalcache`, with `get-task-allow` false;
   * an App Store profile embedded.

### Artifacts (not committed; `artifacts/` is ignored by git)

| File | Use |
|---|---|
| `artifacts/ios/ipa/HCGMail.iOS.ipa` | **Upload this** with Transporter |
| `artifacts/ios/archive/HCG Mail <version> (<build>).xcarchive` | Copy of the archive, for your records |
| `~/Library/Developer/Xcode/Archives/<date>/….xcarchive` (path printed by the script) | The same archive as listed in Xcode › Window › Organizer |
| `artifacts/ios/ipa/HCGMail.iOS.ipa.sha256` | SHA-256 of the `.ipa`; re-check before upload with `shasum -a 256 -c HCGMail.iOS.ipa.sha256` (in that folder) |
| `artifacts/ios/verification.txt` | Every preflight and build check from this run, plus the SHA-256; keep it with the release |

## 3. Upload: use Transporter

Use **Transporter** for this build. It uploads exactly the `.ipa` that `export-ipa.sh` signed and verified.

Organizer's *Distribute App* would export and re-sign the archive with Xcode's own settings first, so the binary
uploaded would not be the one that was checked. Organizer is still useful to look at the archive.

1. Install Transporter from the Mac App Store. Sign in with an Apple ID that has the *App Manager* or *Admin* role
   on HCG's team.
2. Drag `artifacts/ios/ipa/HCGMail.iOS.ipa` in → **Verify** → **Deliver**.
3. Wait for App Store Connect to finish processing (usually 10–30 minutes and an e-mail). The build then appears
   under **TestFlight**. Answer the export-compliance question there (see below).

## 4. Test on a real device before submitting

Test the exact uploaded build with internal TestFlight testers (no Beta App Review needed). The step-by-step plan,
14 areas on iPhone and iPad, is in **[TESTFLIGHT_PLAN.md](TESTFLIGHT_PLAN.md)**. A build that crashes on launch is the
most common rejection.

## 5. App Store Connect: still needed before *Submit for Review*

The complete package is in **[APP_STORE_CONNECT.md](APP_STORE_CONNECT.md)**:
* store text (subtitle, description, keywords), category and URLs;
* screenshots at the required sizes;
* the App Privacy answers, checked against the app's actual data flows;
* age-rating guidance and the export-compliance checklist;
* App Review notes and the demo-mailbox requirements;
* the final checklist.

**Microsoft sign-in is hidden in this build.** Without a Microsoft client ID, the iPhone/iPad app no longer shows
*Add Microsoft account*, its account-setup banner, or any text advertising Microsoft accounts.
* `SignInPresentation` is set in `MobileServices`, and mobile tests cover both states.
* The implementation is unchanged. A build with a configured client ID shows the button again.
* Only test and advertise it after it has been verified with a real account (`docs/OAUTH_ACTIVATION_CHECKLIST.md`).

## What has been verified

| Check | Where | Result |
|---|---|---|
| `HCGMail.xcodeproj` opens from a fresh checkout (`xcodebuild -list`: targets, configurations, shared schemes) | GitHub Actions run [44864611757](https://github.com/F1r3srp3nt/HCG-Mail/actions/runs/44864611757), macOS 26, Xcode 26.3 (17C529) | ✅ |
| Scheme `HCG Mail`, Any iOS Device, **Release**: unsigned ios-arm64 AOT build | same | ✅ `** BUILD SUCCEEDED **` (15½ min) |
| That Release app checked by `verify-ios-build.py` | same; report and `Info.plist`, privacy manifest and asset list in the `ios-release-check` artifact | ✅ bundle `com.hakmicapitalgroup.hcgmail`, version 0.1.0 (1), iOS 16.0+, iPhone + iPad, **SDK iphoneos26.2**, Xcode 2630, AppIcon compiled (4 renditions, source 1024×1024 opaque), `LaunchScreen.storyboardc`, privacy manifest present, `msauth.` URL scheme |
| Privacy manifest against the binary | same; re-checked in the PR run [44867647403](https://github.com/F1r3srp3nt/HCG-Mail/actions/runs/44867647403) | ✅ after a fix: the binary uses `NSUserDefaults` (from MSAL) and `activeInputModes` (from Avalonia's text input), which the manifest did not declare. Apple would have rejected the upload (ITMS-91053). Now declared (UserDefaults CA92.1, ActiveKeyboards 54BD.1); the PR run reports both "used and declared" on the Release binary, and the check fails device builds if this happens again. |
| Product › Archive from the scheme | same | ✅ refused with "Product › Archive is not supported… Run scripts/export-ipa.sh" |
| The build leaves the checkout clean (no `xcuserdata`, no generated files) | same | ✅ |
| Scheme `HCG Mail` builds the simulator app, which passes the checks | both runs, simulator job | ✅ |
| Scheme `HCG Mail Simulator` builds, installs and starts the app | runs [44864611757](https://github.com/F1r3srp3nt/HCG-Mail/actions/runs/44864611757), [44871366880](https://github.com/F1r3srp3nt/HCG-Mail/actions/runs/44871366880), [44876023674](https://github.com/F1r3srp3nt/HCG-Mail/actions/runs/44876023674) | ✅ The Xcode build returns in about 14 s ("BUILD SUCCEEDED"). The app then starts in the background and `launch.log` reports `HCG-LAUNCH: started`. On GitHub's macOS runners, a cold simulator boot took up to about 20 minutes. A simulator that is already booted is reused, which is the usual case on a Mac. |
| Safe area (notch/Dynamic Island, home indicator) applied once, measured by the simulator self-test | run [44932364266](https://github.com/F1r3srp3nt/HCG-Mail/actions/runs/44932364266); again in [44946277608](https://github.com/F1r3srp3nt/HCG-Mail/actions/runs/44946277608) | ✅ after a fix: iOS reports 62/34 pt and the mail area now starts at 62 pt. Before the fix it started at 124 pt, because the inset was applied twice. |
| Microsoft sign-in is neither offered nor mentioned when it is not configured (the case for this build); it appears when it is | Linux job, `FlowTests` (`Unconfigured_microsoft_sign_in_is_neither_offered_nor_advertised`, `Configured_microsoft_sign_in_is_offered`), run [44946277608](https://github.com/F1r3srp3nt/HCG-Mail/actions/runs/44946277608) | ✅ |
| App Store screenshots: inbox, message, composer on iPhone 17 Pro Max (1320×2868) and iPad Pro 13-inch M5 (2064×2752) | run [44946277608](https://github.com/F1r3srp3nt/HCG-Mail/actions/runs/44946277608), artifact `app-store-screenshots` (until 19 Feb 2027) | ✅ all six checked by eye: each shows the intended screen with the sample mailbox. The PNGs carry an alpha channel that is fully opaque. If App Store Connect refuses them for that, export them without alpha first (Preview › Export, untick *Alpha*). |
| Signing preflight, signed `.ipa`, entitlements, embedded profile, `.xcarchive` | your Mac (`export-ipa.sh`) | **not yet run**: needs your certificate |
| Real-device test, upload, TestFlight, review | you | not yet done |
