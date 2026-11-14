# HCG Mail for iPhone and iPad

This folder contains the **working iOS application** for HCG Mail: the Xcode-toolchain build of the app, its
iPhone/iPad interface, the iOS platform code, the app icon asset catalog, Info.plist, entitlements, tests, build
scripts and this setup guide. App Store Connect metadata (listing, screenshots, privacy answers) will also live here.

It is the same HCG Mail as the desktop app. The mail engine (IMAP/SMTP, Microsoft Graph, MIME, sanitizer, TLS
validation) and all view models are **referenced** from [`../Windows-executable-Application/src/`](../Windows-executable-Application/src/);
nothing is copied. [`../iOS Executable/`](../iOS%20Executable/) only describes direct installation of this same app.

## Status

| | State |
|---|---|
| **Implemented** | Account setup with connection diagnostics and Keychain storage · accounts, folders, message list with paging, pull-to-refresh and search · HTML and plain-text reading with inline (cid) images · compose, drafts, reply, reply all, forward, attachments (document picker / share sheet) · read/unread, flag, archive, delete · iPhone navigation and adaptive iPad layouts (split view, slide-over, rotation) · settings, Mail Health, banners and explicit failure states · safe area, keyboard, Dynamic Type and Reduce Motion · background suspend/resume · Microsoft accounts through the shared MSAL code with an iOS adapter |
| **Compiled** | **Simulator (Debug): yes** — built by the `iOS` GitHub Actions workflow on macOS 26 with Xcode 26.3, .NET SDK 10.0.403 and the iOS workload 26.2.10233 (0 errors; `Assets.car` with the app icon present). **Device (ios-arm64, Release, full AOT): yes** — the App Store configuration compiles without errors in its own CI job (about 20 minutes); it is built unsigned because CI holds no certificates. Mobile library and tests also build on Linux. |
| **Tested (headless)** | 44 tests render the real mobile views at iPhone, iPhone SE and iPad sizes (preview mailbox, no network): layouts, rotation, split view, text size (100/130/150 %), safe area, keyboard, nothing clipped on iPhone SE, 44-point touch targets, VoiceOver names on icon buttons, navigation, compose/send/draft, reply/forward, message actions, attachments → share sheet, link confirmation, account setup, Microsoft "pending" state, settings, suspend/resume, all 13 rendering fixtures at iPhone width |
| **Tested against a real mail server** | **Yes, on a local test server** (Dovecot IMAP + Postfix SMTP over TLS with a CRL-checked certificate, disposable test mailboxes `ola@`/`kari@hcgmail.test`; delivery is local only). With production services and the mobile UI: add an account through the setup sheet and its connection test → connect → folders and inbox → open an HTML message (scripts and tracker removed, remote content blocked) → compose and send → refresh shows it in Inbox and Sent → **relaunch** reconnects with the stored password → remove the account: confirmation, account, stored password and history gone, mail on the server untouched → relaunch shows the welcome screen. A second test cuts the connection mid-session: the list stays readable with an offline notice and Retry, then recovers. Log files are checked to contain neither the password nor the address. Run on Linux (headless UI) — **not yet on a simulator or a device, and not against a public provider (e.g. Domene.no)**. |
| **Simulator-tested** | **Yes, automatically, in CI** (iPhone 17 Pro simulator, iOS 26.5): the app installs and starts; the self-test passed — Keychain save/replace/read/delete, app data directory, settings and account list saved and reloaded as JSON, sanitizer, Microsoft configuration (reported as not set up), TLS revocation fallback. Preview-mode screenshots were taken on an iPhone and an iPad simulator (workflow artifact `ios-simulator-screenshots`). The first simulator run found and led to the fix of a Keychain bug (replacing an existing password). **Not yet used interactively by a person.** |
| **Device-tested** | **No.** Needs HCG's Apple Developer account, a signing certificate and a provisioning profile. |
| **Microsoft sign-in** | **Pending.** The code is in place; builds without a client ID show Microsoft sign-in as "not set up" (no fake login). Needs the Entra app registration to include an iOS redirect URI (below). |
| **App Store** | Not submitted. Nothing is uploaded automatically. |

## What this is (and is not)

HCG Mail for iOS is a **.NET for iOS application** that hosts the shared **Avalonia** user interface. It is compiled
with Apple's toolchain — the iOS SDK from Xcode, `actool` for the icon asset catalog, `ibtool` for the launch screen,
Apple's linker and `codesign` — driven by the .NET iOS workload (`dotnet build` / `dotnet publish`). It is **not** a
SwiftUI app and **not** an Xcode project: there is intentionally no `.xcodeproj` (an empty one would build nothing).
Xcode is still required on the Mac because it provides the SDK, simulators and signing tools.

```
iOS App Store Submission/
  HCG.Mail.iOS.sln                 shared projects + the three iOS-side projects
  Directory.Build.props            finds the shared code (HcgSharedRoot / HCG_SHARED_ROOT), imports the desktop settings
  Directory.Build.targets          build diagnostics HCG001–HCG004
  Directory.Packages.props         the desktop's pinned package versions + Avalonia.iOS
  toolchain.props                  required Xcode, pinned iOS workload, minimum iOS (read by scripts and CI)
  global.json                      .NET 10 SDK
  src/HCG.Mail.Mobile/             iPhone/iPad interface (net10.0, no iOS APIs — testable anywhere)
    MobileServices.cs                composition root (shared engine + mobile services)
    ViewModels/                      MobileShellViewModel (navigation, layout, insets, text size), overlays
    Views/                           mailboxes, message list, reader, compose, account setup, Microsoft,
                                     settings, Mail Health, welcome, shell; MobileMessageBodyView (HtmlLabel)
    Services/                        sheets & alerts (IDialogService), banners, link opener
    Platform/IMobilePlatform.cs      everything the UI needs from iOS
  src/HCG.Mail.iOS/                the iOS app (net10.0-ios)
    AppDelegate.cs, Main.cs          start-up, Microsoft redirect hand-off, self-test hook
    Platform/                        IosKeychainCredentialVault, IosPlatform (share sheet, browser, Dynamic Type,
                                     Reduce Motion, idiom), IosMicrosoftAuth (MSAL for iOS)
    Assets.xcassets/AppIcon.appiconset   the HCG Mail icon (1024 px, opaque)
    Info.plist, Entitlements.plist, PrivacyInfo.xcprivacy, LaunchScreen.storyboard
    SelfTest.cs                      simulator/device checks (HCG_MAIL_SELF_TEST=1)
  tests/HCG.Mail.Mobile.Tests/     headless tests of the mobile interface
  scripts/                         doctor (diagnostics), workload install, simulator build/run, IPA export, icon,
                                   verify-app-selection.py (one iOS app in the repository)
```

## Pinned versions

| Component | Version | Where it is pinned |
|---|---|---|
| .NET SDK | .NET 10 (`10.0.100` or newer: `global.json` rolls forward to the newest installed 10.0 SDK; CI used 10.0.403) | `global.json` |
| .NET iOS workload | `Microsoft.NET.Sdk.iOS` manifest **26.2.10233** (iOS 26.2 SDK bindings) | `HcgIosWorkloadManifest` in `toolchain.props`, installed by `scripts/install-workload.sh` |
| Xcode | **26.3** (the workload refuses to build with older Xcode) | `HcgRequiredXcode` in `toolchain.props`; CI selects `/Applications/Xcode_26.3.app` |
| Minimum iOS | 16.0 (iPhone and iPad) | `HcgMinimumIos` in `toolchain.props` → `SupportedOSPlatformVersion` |
| Avalonia / Avalonia.iOS | 11.3.22 | `../Windows-executable-Application/Directory.Packages.props` + `Directory.Packages.props` here |
| MailKit / MimeKit, HtmlSanitizer, MSAL.NET | 4.18.2, 9.2.1039, 4.91.1 | `../Windows-executable-Application/Directory.Packages.props` |

All package versions are central; the iOS projects cannot drift from the desktop app. When you move to a newer
Xcode, change `HcgRequiredXcode` and `HcgIosWorkloadManifest` together in `toolchain.props`.

## Build diagnostics

Each cause has its own message, so a missing workload is never mistaken for a signing problem.
`scripts/doctor.sh` checks all categories up front (it also runs on Linux and says what can be done there):

| Category | Reported as | Fix |
|---|---|---|
| Shared sources | `error HCG001` — the mail engine / view models in `Windows-executable-Application/` were not found | Build from a complete HCG-Mail clone, or pass `-p:HcgSharedRoot=<path>` / set `HCG_SHARED_ROOT` |
| Host | `error HCG002` — the iOS app is being built on Linux or Windows | Build on a Mac; elsewhere run `dotnet test tests/HCG.Mail.Mobile.Tests` |
| Deployment target | `error HCG003` — `SupportedOSPlatformVersion` below 16.0 | Keep `HcgMinimumIos` |
| Signing (devices only) | `HCG004` message, then the SDK's "No valid iOS code signing keys" if none is installed | Pass `CodesignKey`/`CodesignProvision` or use `scripts/export-ipa.sh`; simulators need none |
| .NET iOS workload | SDK `NETSDK1147` / doctor `[workload]` | `scripts/install-workload.sh` |
| Xcode | SDK "requires Xcode 26.3" / doctor `[xcode]` | `sudo xcode-select -s /Applications/Xcode_26.3.app` |

Paths are never machine-specific: the shared code is found relative to `Directory.Build.props`.

## Set up a Mac

```bash
# 1. Xcode 26.3 from the App Store or developer.apple.com, then once:
sudo xcode-select -s /Applications/Xcode_26.3.app/Contents/Developer   # or Xcode.app
xcodebuild -runFirstLaunch
# install an iOS 26 simulator runtime: Xcode → Settings → Components

# 2. .NET 10 SDK (https://dotnet.microsoft.com/download/dotnet/10.0 or: brew install --cask dotnet-sdk)

# 3. The pinned iOS workload, then check everything:
cd "HCG-Mail/iOS App Store Submission"
scripts/install-workload.sh
scripts/doctor.sh                          # sources, host, SDK, workload, Xcode, deployment, signing
```

## Build and run in the Simulator

No Apple account or certificate is needed for the Simulator.

```bash
scripts/build-simulator.sh                 # Debug build; prints the .app path (artifacts/ios/simulator/Debug)
scripts/run-simulator.sh                   # install and start on the newest iPhone simulator
scripts/run-simulator.sh --ipad            # … on an iPad simulator
scripts/run-simulator.sh "iPhone 17 Pro"   # … on a named simulator (xcrun simctl list devices)
scripts/run-simulator.sh --self-test       # Keychain round trip, sandbox, sanitizer, configuration → PASS/FAIL
scripts/run-simulator.sh --preview --screenshot iphone.png   # Debug only: sample mailbox, no network
```

Equivalent raw commands:

```bash
dotnet build src/HCG.Mail.iOS/HCG.Mail.iOS.csproj -c Debug -f net10.0-ios -r iossimulator-arm64
xcrun simctl boot "iPhone 17"; open -a Simulator
xcrun simctl install booted src/HCG.Mail.iOS/bin/Debug/net10.0-ios/iossimulator-arm64/HCGMail.iOS.app
xcrun simctl launch booted com.hakmicapitalgroup.hcgmail
```

Use `iossimulator-x64` on an Intel Mac. The shared code and the mobile UI tests also run without a Mac:

```bash
dotnet test tests/HCG.Mail.Mobile.Tests                       # Linux, macOS or Windows
HCG_MAIL_SCREENSHOT_DIR=/tmp/shots dotnet test tests/HCG.Mail.Mobile.Tests   # + iPhone/iPad PNGs
```

The complete workflow (add account → inbox → read → send → relaunch → remove) runs against a disposable local
Dovecot/Postfix server, never a real mailbox. On Linux, as root in a throw-away container or VM:

```bash
sudo apt-get install -y dovecot-imapd dovecot-lmtpd postfix
sudo ../Windows-executable-Application/tests/integration-server/setup-local-mail-server.sh start
HCG_MAIL_E2E=1 dotnet test tests/HCG.Mail.Mobile.Tests --filter "FullyQualifiedName~RealServer"
sudo ../Windows-executable-Application/tests/integration-server/setup-local-mail-server.sh cleanup
```

Testing with a real provider mailbox (for example a Domene.no test account) is a manual step for the account
owner; the tests and scripts never sign in to, or send from, a real account.

## Run on your own iPhone or iPad (development signing)

1. In the Apple Developer account of Hakmi Capital Group, register the App ID **`com.hakmicapitalgroup.hcgmail`**
   with the *Keychain Sharing* capability (used for the two Keychain groups in `Entitlements.plist`).
   If HCG chooses another bundle identifier, change `HcgBundleId` in `HCG.Mail.iOS.csproj` **and** the
   `msauth.…` URL scheme in `Info.plist`.
2. Create an *Apple Development* certificate and a *Development* provisioning profile that includes your device;
   install both on the Mac (Xcode → Settings → Accounts → Download Manual Profiles).
3. Connect the device (Developer Mode on), then:

```bash
dotnet build src/HCG.Mail.iOS/HCG.Mail.iOS.csproj -c Debug -f net10.0-ios -r ios-arm64 \
  -p:CodesignKey="Apple Development: <Name> (<TEAMID>)" -p:CodesignProvision="<profile name>" \
  -t:Run
```

## Release IPA (TestFlight / App Store) — built, never uploaded

```bash
export HCG_IOS_SIGNING_IDENTITY="Apple Distribution: Hakmi Capital Group (<TEAMID>)"
export HCG_IOS_PROVISIONING_PROFILE="<App Store profile name or UUID>"
export HCG_IOS_BUILD_NUMBER=1            # increase for every upload
export HCG_MICROSOFT_CLIENT_ID=<GUID>    # optional: enables Microsoft sign-in
scripts/export-ipa.sh                    # → artifacts/ios/ipa/HCGMail.iOS.ipa (+ an Xcode archive)
```

The script stops after producing and verifying the signed `.ipa`. Uploading (Transporter or Xcode Organizer) and
submitting for review are manual, founder-approved steps. Certificates, `.p12`/`.p8` files and provisioning profiles
must stay in the Mac's keychain / `~/Library` — `.gitignore` blocks them and build output from being committed.

## App icon

`src/HCG.Mail.iOS/Assets.xcassets/AppIcon.appiconset/AppIcon-1024.png` is a single 1024 × 1024 opaque, square,
unrounded icon (iOS applies the rounded mask). `Info.plist` points `XSAppIconAssets` at the catalog, the .NET iOS
build compiles it with `actool` into `Assets.car`, and `scripts/build-simulator.sh` fails if `Assets.car` is missing.

> **About `HCG-Mail-Xcode-Pack.zip`:** the request mentioned an attached Xcode pack, but no such file reached this
> work session. The icon was therefore generated from the official artwork already in the repository,
> `Windows-executable-Application/assets/branding/hcg-mail-app-icon-source.png` (the HCG Mail icon supplied on
> 2026-11-12), with `scripts/make-app-icon.py`. To use the pack instead, replace the contents of
> `AppIcon.appiconset/` with the pack's `AppIcon.appiconset` (keep `Contents.json` valid) and rebuild.

## Microsoft accounts (Outlook.com, Hotmail, Microsoft 365)

The iOS app uses the same MSAL code, scopes (`User.Read`, `Mail.ReadWrite`, `Mail.Send`) and error messages as the
desktop app; `IosMicrosoftAuth` only builds the iOS client: sign-in in the system authentication session
(ASWebAuthenticationSession — HCG Mail never sees the password), tokens in the iOS Keychain (group
`com.microsoft.adalcache`). It is unrelated to HCG Connect.

To enable it, in the existing Microsoft Entra app registration (see
[`docs/MICROSOFT_ACCOUNTS.md`](../Windows-executable-Application/docs/MICROSOFT_ACCOUNTS.md)) add the platform
**iOS / macOS** with bundle ID `com.hakmicapitalgroup.hcgmail`; Entra then lists the redirect URI
`msauth.com.hakmicapitalgroup.hcgmail://auth`. Build with `-p:HcgMicrosoftClientId=<GUID>`. Without a client ID, or
if Info.plist lacks the URL scheme, the app says Microsoft sign-in is *not set up* and offers no sign-in button.
Microsoft sign-in has not been tested on iOS yet.

## Security and privacy

* **Passwords:** iOS Keychain only (`IosKeychainCredentialVault`, Security framework `SecItem*`), generic-password
  items with `kSecAttrAccessibleWhenUnlockedThisDeviceOnly`, not synchronised to iCloud, not restored to other
  devices. No command-line tools, no plaintext files. Settings and the account list (no secrets, no mail) are in
  `Library/Application Support/HCG Mail`.
* **TLS:** the shared `MailClientFactory` — certificate chain, host name and revocation are always checked, never
  disabled. iOS uses the same Apple trust evaluation as macOS, so the signature-verified CRL fallback that is
  already used on macOS (see [`docs/TLS_REVOCATION.md`](../Windows-executable-Application/docs/TLS_REVOCATION.md))
  is enabled on iOS too; it can only confirm "not revoked", never relax a check.
* **Untrusted message content:** the shared sanitizer removes scripts, forms, event handlers, iframes/objects and
  dangerous URLs; HtmlRenderer has no script engine. `MessageHtmlBridge` (shared with the desktop) answers every
  load: `cid:`/`data:` images from the message, remote images only after *Load remote images*, never stylesheets
  or fonts; links open only after a confirmation showing the real host, http(s) only. A plain-text view and an
  automatic plain-text fallback exist for every message.
* **Attachments** are saved to the app's temporary folder and handed to the share sheet; HCG Mail never opens or
  runs them. Temporary files are deleted at the next start.
* **Entitlements:** only `keychain-access-groups` (the app's own group and MSAL's). No push, background modes,
  iCloud or app groups. **Privacy manifest:** no tracking, no collected data; required-reason API declarations for
  the .NET runtime.

## Background behaviour

iOS suspends apps shortly after they leave the screen, so HCG Mail **does not check for mail in the background and
sends no notifications**. Refreshing stays manual (pull down or the refresh button) unless the user chooses
automatic checking in Settings, which runs only while the app is on screen. On leaving the screen the optional timer
stops; on return the open folder is refreshed once only if automatic checking is on. Connections that iOS closed
are re-established by the next action (IMAP sessions reconnect automatically).

## Accessibility

Every tappable control is at least 44 × 44 points (checked by a test); icon-only buttons have accessible names;
the interface scales with the iOS text size (Dynamic Type, up to 160 %, switching to a simpler layout when needed);
Reduce Motion stops the only animation (the refresh spinner); the keyboard and safe areas never cover content.
VoiceOver has not been tested on a device yet.

## App Store notes (for the founder)

* Export compliance: the app only uses standard TLS from the operating system. Answer the encryption question in
  App Store Connect accordingly; `ITSAppUsesNonExemptEncryption` is deliberately not set without that decision.
* App privacy ("nutrition label"): no data collected by Hakmi Capital Group; mail and credentials stay on the device.
* Review notes need a test mailbox that Apple can use; do not give reviewers a real HCG mailbox.
