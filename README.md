# HCG Mail

**A multi-platform email application by Hakmi Capital Group.**

HCG Mail connects to existing mailboxes over standard **IMAP and SMTP** (for example a Domene.no domain
mailbox). It needs no Outlook, Microsoft 365, Exchange or HCG account, encrypts every connection,
stores passwords only in the operating system's secure credential store, and blocks remote content in
messages by default.

## Repository layout

| Directory | Purpose | Status |
|---|---|---|
| [`Windows-executable-Application/`](Windows-executable-Application/) | **The HCG Mail application**: .NET 10 + Avalonia source, tests, assets, docs and build scripts. Published as a self-contained Windows x64 executable; the same code builds and runs on macOS for development. | ✅ **Implemented** — MVP 0.1.0 |
| [`Windows Store app/`](Windows%20Store%20app/) | Future Microsoft Store distribution (MSIX packaging, Store identity, assets, certification). | 🕓 Reserved — not started |
| [`Mac OS executable Application/`](Mac%20OS%20executable%20Application/) | Future directly distributed macOS version (`.app` bundle, Apple Silicon, Developer ID signing, notarization, DMG/PKG). | 🕓 Reserved — not started |
| [`Mac AppStore Submission/`](Mac%20AppStore%20Submission/) | Future Mac App Store distribution (App Sandbox, entitlements, App Store Connect, privacy disclosures). | 🕓 Reserved — not started |
| [`iOS App Store Submission/`](iOS%20App%20Store%20Submission/) | Future App Store distribution for iPhone/iPad (App Store Connect, review, privacy labels). | 🕓 Reserved — not started |
| [`iOS Executable/`](iOS%20Executable/) | Future directly installed iOS build (TestFlight / ad-hoc). | 🕓 Reserved — not started |
| [`Google Play Submission/`](Google%20Play%20Submission/) | Future Google Play distribution (Play Console, Data safety, signing). | 🕓 Reserved — not started |
| [`Android Executable/`](Android%20Executable/) | Future directly installed Android build (APK/AAB). | 🕓 Reserved — not started |
| [`Linux Store Submission/`](Linux%20Store%20Submission/) | Future Linux store distribution (Flathub / Snap Store). | 🕓 Reserved — not started |
| [`Linux Terminal Only/`](Linux%20Terminal%20Only/) | Future terminal (text-mode) edition reusing the same mail libraries. | 🕓 Reserved — not started |
| [`Linux UI/`](Linux%20UI/) | Future graphical Linux desktop build (AppImage / .deb). | 🕓 Reserved — not started |

There is exactly **one** implementation of the email client, in `Windows-executable-Application/`.
The other folders are reserved for distributions on other platforms and stores. Those builds are
produced from the Windows application with HCG Executable Maker; the folders hold only
platform-specific packaging, signing and store configuration, not separate email clients.

## What is implemented today

Inside `Windows-executable-Application/` (details in its [README](Windows-executable-Application/README.md)):

* Receive, read, search, flag, archive, delete and refresh mail over IMAP (manual refresh — no background polling)
* Compose, reply, reply all, forward with attachments and server-side drafts over SMTP/IMAP
* Six-step connection test, Domene.no preset, secure credential storage (Windows Credential Manager, macOS Keychain)
* Mail Health deliverability diagnostics (SPF, DKIM, DMARC, send history) and message priority (Low / Normal / High)
* Microsoft accounts (Outlook.com, Hotmail, Microsoft 365) through Microsoft Graph — requires HCG's Microsoft app registration
* Original / Readable / Plain-text message views with clean inbox previews
* Safe HTML rendering with remote content blocked by default
* HCG Future and HCG Classic skins with the official HCG logo and HCG Mail application icon
* Unit, headless UI and opt-in end-to-end tests against a disposable local mail server

Not yet done: testing on real Windows hardware and a real Mac, real Domene.no mailbox testing, code
signing, installers and any store submission — see the
[roadmap](Windows-executable-Application/docs/ROADMAP.md).

## Build and run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (macOS: `brew install --cask dotnet-sdk`).

```bash
git clone https://github.com/F1r3srp3nt/HCG-Mail.git
cd HCG-Mail/Windows-executable-Application

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

Output: `Windows-executable-Application/artifacts/HCGMail-<version>-win-x64.zip` (build output is never committed).

More: [setup](Windows-executable-Application/docs/SETUP.md) ·
[architecture](Windows-executable-Application/docs/ARCHITECTURE.md) ·
[security](Windows-executable-Application/docs/SECURITY.md) ·
[Windows build](Windows-executable-Application/docs/WINDOWS_BUILD.md) ·
[testing](Windows-executable-Application/docs/TESTING.md) ·
[deliverability](Windows-executable-Application/docs/DELIVERABILITY.md) ·
[Microsoft accounts](Windows-executable-Application/docs/MICROSOFT_ACCOUNTS.md) ·
[message rendering](Windows-executable-Application/docs/MESSAGE_RENDERING.md) ·
[manual Domene.no checklist](Windows-executable-Application/docs/MANUAL_TEST_CHECKLIST.md)

## Repository rules

* No credentials, certificates, provisioning profiles, mailbox data or build output are committed.
* Signing, store submissions and HCG infrastructure changes require founder approval.
