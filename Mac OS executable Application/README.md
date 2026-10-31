# HCG Mail — Mac OS executable Application

**Status: reserved — not started.** No macOS application bundle, signing, notarization or installer
has been produced yet.

This folder is reserved for the **directly distributed macOS version** of HCG Mail (downloaded from
HCG, outside the Mac App Store).

## Planned responsibilities

| Area | Notes |
|---|---|
| macOS application packaging | Build an `HCG Mail.app` bundle (`Info.plist`, `Contents/MacOS`, `Contents/Resources`) from the existing app published with `dotnet publish -r osx-arm64` / `osx-x64` |
| Apple Silicon support | Native `osx-arm64` build (already compiles), plus `osx-x64` for Intel Macs or a universal binary |
| macOS application icons | `.icns` generated from the official icon master `../Windows-executable-Application/assets/icons/hcg-mail-1024.png` |
| Code signing | Developer ID Application certificate, hardened runtime, entitlements required by .NET |
| Developer ID distribution | Signed download for distribution outside the App Store |
| Apple notarization | `notarytool` submission and stapling so Gatekeeper accepts the app |
| DMG or PKG distribution | Branded DMG (drag to Applications) or a signed PKG installer |
| macOS-specific integrations | Keychain (already implemented, still to be verified on a real Mac), menu bar, Notification Center, Dock badge, launch at login |

## Relationship to the existing application

There is **one** HCG Mail implementation, in [`../Windows-executable-Application/`](../Windows-executable-Application/).
It is cross-platform .NET 10 / Avalonia and **already runs on macOS** for development:

```bash
cd HCG-Mail/Windows-executable-Application
dotnet run --project src/HCG.Mail.App
```

The macOS distribution must reuse that code. Only packaging, `Info.plist`, entitlements, icons and
signing/notarization scripts should live here. **No separate Swift application** is planned unless
explicitly authorised.

## Requirements before signing and distribution (founder approval)

* Apple Developer Program membership for Hakmi Capital Group
* Developer ID Application (and, for PKG, Developer ID Installer) certificates
* App-specific credentials for notarization — kept in the build machine's Keychain or CI secrets,
  **never** committed to this repository
