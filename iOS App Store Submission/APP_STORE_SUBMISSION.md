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
| Scheme **HCG Mail Simulator**, **Build** (⌘B) | ✅ | Builds, opens Simulator, installs and starts HCG Mail on the newest iPhone simulator. |
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
| `artifacts/ios/verification.txt` | Every preflight and build check from this run; keep it with the release |

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

Test the exact uploaded build. Add yourself as an **internal tester** in TestFlight (no Beta App Review needed) and
install it with the TestFlight app on an iPhone and on an iPad.

To test before uploading, build with a development identity and profile that include the device. Set
`HCG_IOS_DISTRIBUTION=other` and run `export-ipa.sh`, then install the `.ipa` from Xcode › Window › Devices and
Simulators (**+**).

Use a **dedicated test mailbox**, never a real HCG mailbox, and send only to your own test addresses. It takes about
15 minutes:

1. **First start:**
   * the icon and name "HCG Mail" are on the home screen;
   * the launch screen appears;
   * the app opens to the welcome screen, with no sample data.
2. **Mailbox setup:**
   * *Add email account* with the test mailbox's IMAP/SMTP details. *Test connection* shows every step passing.
   * A wrong password shows a clear error, and nothing is saved.
3. **Reading:**
   * The Inbox loads. Pull to refresh works.
   * Open an HTML newsletter (remote images stay blocked until *Load images*) and a plain-text message.
   * On iPad, rotate, and use Split View or Slide Over with another app.
4. **Sending:**
   * Compose to your own second test address and send. It appears in *Sent* and arrives.
   * Reply to it. Save a draft, reopen it, and send it.
5. **Attachments:**
   * Open a received PDF and image (Quick Look or the share sheet).
   * Attach a file from Files to a new message, send it, and open it on arrival.
6. **Relaunch:**
   * Force-quit, reopen: the account is still there with **no password prompt** (Keychain), and the Inbox loads.
   * Turn on Airplane Mode: an offline notice appears and the loaded list stays. Turn it off and refresh.
   * Remove the account in Settings: it is gone after the next relaunch.

Note anything unexpected before you submit. A build that crashes on launch is the most common rejection.

## 5. App Store Connect: still needed before *Submit for Review*

* **App Information:**
  * name and subtitle;
  * category (suggestion: *Productivity*);
  * content rights;
  * age rating questionnaire (no objectionable content; mail content is the user's own).
* **Pricing and Availability:** price (free?) and countries.
* **App Privacy:**
  * **Privacy Policy URL** (required). It should say that mail, passwords and settings stay on the device and go
    only to the user's own mail servers (and Microsoft for Microsoft accounts).
  * Data collection: **"Data Not Collected"**. This matches `PrivacyInfo.xcprivacy`, since HCG receives nothing.
    Confirm that this is true for HCG's policy before answering.
* **Version page (the version must equal the build's marketing version):**
  * Screenshots for **iPhone 6.9"** and **iPad 13"** (required because the app runs on iPad).
    `scripts/run-simulator.sh --preview --screenshot …` produces sample-mailbox screens on simulators of those sizes.
  * Description, keywords, support URL, copyright ("© 2026 Hakmi Capital Group").
  * Select the uploaded build.
* **Export compliance** (asked for each build, since `ITSAppUsesNonExemptEncryption` is not set): HCG Mail uses
  only the encryption built into iOS and .NET for TLS connections to mail servers, and DKIM signature checks. This is
  usually answered as exempt, but it is HCG's legal declaration to make. Once decided, the key can be added to
  `Info.plist` so the question stops appearing.
* **App Review Information:**
  * contact details;
  * **Sign-in required: yes**, and in the notes a **test mailbox** that Apple can use (IMAP host, port and security,
    SMTP host, port and security, username, password). Use a mailbox created for review, not a real one.
  * One sentence: "HCG Mail connects to the user's own existing mailbox over IMAP/SMTP; no HCG account is needed."
* **Review risk to decide first: Microsoft sign-in.** A build without a Microsoft client ID shows *Add Microsoft
  account*, but sign-in is "not set up". A reviewer may treat a visible, non-working feature as incomplete
  (Guideline 2.1). Before submitting, either:
  * register the app with Microsoft and build with `HCG_MICROSOFT_CLIENT_ID` (and test it); or
  * hide the Microsoft button in builds without a client ID. This is a small code change.
* Then **Add for Review → Submit**. This is manual, and yours to approve.

## What has been verified

| Check | Where | Result |
|---|---|---|
| `HCGMail.xcodeproj` opens from a fresh checkout (`xcodebuild -list`: targets, configurations, shared schemes) | GitHub Actions run [44864611757](https://github.com/F1r3srp3nt/HCG-Mail/actions/runs/44864611757), macOS 26, Xcode 26.3 (17C529) | ✅ |
| Scheme `HCG Mail`, Any iOS Device, **Release**: unsigned ios-arm64 AOT build | same | ✅ `** BUILD SUCCEEDED **` (15½ min) |
| That Release app checked by `verify-ios-build.py` | same; report and `Info.plist`, privacy manifest and asset list in the `ios-release-check` artifact | ✅ bundle `com.hakmicapitalgroup.hcgmail`, version 0.1.0 (1), iOS 16.0+, iPhone + iPad, **SDK iphoneos26.2**, Xcode 2630, AppIcon compiled (4 renditions, source 1024×1024 opaque), `LaunchScreen.storyboardc`, privacy manifest present, `msauth.` URL scheme |
| Privacy manifest against the binary | same | ⚠️→ fixed: the binary uses `NSUserDefaults` (from MSAL) and `activeInputModes` (from Avalonia's text input), which the manifest did not declare. Apple would have rejected the upload (ITMS-91053). Now declared (UserDefaults CA92.1, ActiveKeyboards 54BD.1), and the check fails device builds if this happens again. |
| Product › Archive from the scheme | same | ✅ refused with "Product › Archive is not supported… Run scripts/export-ipa.sh" |
| The build leaves the checkout clean (no `xcuserdata`, no generated files) | same | ✅ |
| Scheme `HCG Mail` builds the simulator app; scheme `HCG Mail Simulator` builds, installs and starts it | same, simulator job | see the PR's CI run |
| Signing preflight, signed `.ipa`, entitlements, embedded profile, `.xcarchive` | your Mac (`export-ipa.sh`) | **not yet run**: needs your certificate |
| Real-device test, upload, TestFlight, review | you | not yet done |
