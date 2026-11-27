# HCG Mail

**A multi-platform email application by Hakmi Capital Group.**

HCG Mail connects to existing mailboxes over standard **IMAP and SMTP** (for example a Domene.no domain
mailbox). It needs no Outlook, Microsoft 365, Exchange or HCG account, encrypts every connection,
stores passwords only in the operating system's secure credential store, and blocks remote content in
messages by default.

## Repository layout

| Folder | Contents |
|---|---|
| [`HCG Mail Project/`](HCG%20Mail%20Project/) | All source code and documentation. |
| ├ [`Desktop/`](HCG%20Mail%20Project/Desktop/) | **The HCG Mail application and shared mail engine**: .NET 10 + Avalonia source, tests, assets, docs and build scripts. Builds the desktop app for Windows, macOS and Linux (x64 and arm64). |
| ├ [`iOS/`](HCG%20Mail%20Project/iOS/) | **HCG Mail for iPhone and iPad** (.NET for iOS + Avalonia): mobile interface, Keychain/MSAL/share-sheet code, app icons, Info.plist, entitlements, Xcode entry project, tests and build/export scripts. Reuses the engine and view models from `Desktop/src`. |
| └ [`docs/packaging/`](HCG%20Mail%20Project/docs/packaging/) | Packaging and store plans per platform (Microsoft Store, Mac App Store, macOS/Linux packaging, Android, Google Play, Linux stores, terminal edition) and the iOS direct-install and Executable Maker notes. |
| [`HCG Mail Distribution/`](HCG%20Mail%20Distribution/) | Local drop folders for built executables: `Windows/`, `macOS/` and `Linux/` (each `x64/` and `arm64/`) and `iOS/arm64/`. Only their READMEs are tracked; Git ignores whatever you copy in. |

There is exactly **one** implementation of the email client: the mail engine and view models in
`HCG Mail Project/Desktop/src/`. The iOS app in `HCG Mail Project/iOS/` is a second *head* on that same code: it adds
only the iPhone/iPad screens and the iOS platform code and references everything else. Builds for other platforms and
stores are produced from the application with HCG Executable Maker; the packaging notes hold only platform-specific
packaging, signing and store plans, not separate email clients.

## What is implemented today

Inside `HCG Mail Project/Desktop/` (details in its [README](HCG%20Mail%20Project/Desktop/README.md)):

* Receive, read, search, flag, archive, delete and refresh mail over IMAP (manual refresh — no background polling)
* Compose, reply, reply all, forward with attachments and server-side drafts over SMTP/IMAP
* Six-step connection test, Domene.no preset, secure credential storage (Windows Credential Manager, macOS Keychain)
* Mail Health deliverability diagnostics (SPF, DKIM, DMARC, send history) and message priority (Low / Normal / High)
* Microsoft accounts (Outlook.com, Hotmail, Microsoft 365) through Microsoft Graph — requires HCG's Microsoft app registration
* Google accounts (Gmail, Google Workspace) through the Gmail API on the desktop app — requires HCG's Google Cloud registration
  ([owner checklist](HCG%20Mail%20Project/Desktop/docs/OAUTH_ACTIVATION_CHECKLIST.md))
* Original / Readable / Plain-text message views with clean inbox previews
* Safe HTML rendering with remote content blocked by default
* HCG Future and HCG Classic skins with the official HCG logo and HCG Mail application icon
* Unit, headless UI and opt-in end-to-end tests against a disposable local mail server

The iPhone/iPad app (in `HCG Mail Project/iOS/`) offers the same features with touch navigation and adaptive
iPad layouts.

Not yet done: testing on real Windows hardware, a real Mac and a real iPhone/iPad, real Domene.no mailbox testing,
code signing, installers and any store submission — see the
[roadmap](HCG%20Mail%20Project/Desktop/docs/ROADMAP.md).

## iPhone and iPad

Built with .NET for iOS and Avalonia using Apple's toolchain (Xcode 27.2, iOS workload 27.2.10000, .NET 10, Avalonia 12) — not a
SwiftUI/Xcode project. On a Mac:

```bash
cd "HCG-Mail/HCG Mail Project/iOS"
scripts/install-workload.sh && scripts/doctor.sh
scripts/run-simulator.sh                  # build and start in the iPhone simulator
dotnet test tests/HCG.Mail.Mobile.Tests   # headless iPhone/iPad UI tests (any OS)
```

Device signing, the Release `.ipa` (never uploaded automatically), Microsoft sign-in setup and the current test
status are in the [iOS README](HCG%20Mail%20Project/iOS/README.md).

Build tools that import the whole repository (such as HCG Executable Maker) find the applications in
[`hcg-apps.json`](hcg-apps.json); the iOS specifics for Maker are in
[`HCG Mail Project/docs/packaging/ios-executable-hcg-executable-maker.md`](HCG%20Mail%20Project/docs/packaging/ios-executable-hcg-executable-maker.md).

## Build and run (desktop)

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (macOS: `brew install --cask dotnet-sdk`).

```bash
git clone https://github.com/F1r3srp3nt/HCG-Mail.git
cd "HCG-Mail/HCG Mail Project/Desktop"

dotnet restore
dotnet build
dotnet test
dotnet run --project src/HCG.Mail.App              # start HCG Mail (macOS, Windows or Linux)
```

Windows test build (single self-contained `HCGMail.exe`, unsigned), run from the same folder:

```bash
scripts/publish-windows.sh                         # macOS / Linux
# or on Windows:  powershell -ExecutionPolicy Bypass -File scripts\publish-windows.ps1
```

Output: `HCG Mail Project/Desktop/artifacts/HCGMail-<version>-win-x64.zip` (build output is never committed).

More: [setup](HCG%20Mail%20Project/Desktop/docs/SETUP.md) ·
[architecture](HCG%20Mail%20Project/Desktop/docs/ARCHITECTURE.md) ·
[security](HCG%20Mail%20Project/Desktop/docs/SECURITY.md) ·
[Windows build](HCG%20Mail%20Project/Desktop/docs/WINDOWS_BUILD.md) ·
[testing](HCG%20Mail%20Project/Desktop/docs/TESTING.md) ·
[deliverability](HCG%20Mail%20Project/Desktop/docs/DELIVERABILITY.md) ·
[Microsoft accounts](HCG%20Mail%20Project/Desktop/docs/MICROSOFT_ACCOUNTS.md) ·
[Google accounts](HCG%20Mail%20Project/Desktop/docs/GOOGLE_ACCOUNTS.md) ·
[message rendering](HCG%20Mail%20Project/Desktop/docs/MESSAGE_RENDERING.md) ·
[manual Domene.no checklist](HCG%20Mail%20Project/Desktop/docs/MANUAL_TEST_CHECKLIST.md)

## Repository rules

* No credentials, certificates, provisioning profiles, mailbox data or build output are committed.
* Signing, store submissions and HCG infrastructure changes require founder approval.
