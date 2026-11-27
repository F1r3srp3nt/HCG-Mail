# Packaging and distribution notes

These notes describe how HCG Mail is, or may later be, packaged, signed and distributed per platform and store.
They used to be separate top-level folders. None of them contains source code: the one HCG Mail implementation is in
[`../../Desktop/`](../../Desktop/) (mail engine, view models, desktop app) and [`../../iOS/`](../../iOS/) (iPhone and
iPad app on the same engine).

Built executables are not kept here. The drop folders for the targets that are actually built today are in
[`HCG Mail Distribution/`](../../../HCG%20Mail%20Distribution/) at the repository root.

| Note | Target | Status |
|---|---|---|
| [macos-executable.md](macos-executable.md) | macOS outside the App Store: `.app`, Developer ID signing, notarization, DMG/PKG | Plan. The app builds for `osx-x64` and `osx-arm64`; bundle, signing and notarization are not implemented |
| [linux-ui.md](linux-ui.md) | Graphical Linux desktop: AppImage, .deb, desktop integration | Plan. The app builds for `linux-x64` and `linux-arm64`; packaging is not implemented |
| [ios-executable.md](ios-executable.md) | Installing the iOS app outside the App Store (development, ad hoc, TestFlight) | Instructions |
| [ios-executable-hcg-executable-maker.md](ios-executable-hcg-executable-maker.md) | Building the iOS app with HCG Executable Maker | Handoff notes |
| [windows-store-app.md](windows-store-app.md) | Microsoft Store (MSIX) | Not started |
| [mac-app-store-submission.md](mac-app-store-submission.md) | Mac App Store | Not started |
| [linux-store-submission.md](linux-store-submission.md) | Flathub / Snap Store | Not started |
| [linux-terminal-only.md](linux-terminal-only.md) | Text-mode Linux edition | Not started |
| [android-executable.md](android-executable.md) | Android APK/AAB outside Google Play | Not started (no Android app exists) |
| [google-play-submission.md](google-play-submission.md) | Google Play | Not started (no Android app exists) |

The Windows installer and portable ZIP are produced by HCG Executable Maker in CI
(`.github/workflows/windows-prerelease.yml`); see [`../../Desktop/docs/WINDOWS_BUILD.md`](../../Desktop/docs/WINDOWS_BUILD.md).
