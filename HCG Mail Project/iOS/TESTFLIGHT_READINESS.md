# HCG Mail for iOS — TestFlight readiness

Readiness only. **Nothing here uploads, submits or distributes**: you build, number, upload (Transporter) and
distribute TestFlight builds yourself. The steps and checks themselves are in
[APP_STORE_SUBMISSION.md](APP_STORE_SUBMISSION.md); this page says what must be true before you start them and what
is still open.

## Gate

| # | Before the first TestFlight upload | State |
|---|---|---|
| 1 | Native device build qualified on a physical iPhone ([PHYSICAL_DEVICE_QUALIFICATION.md](PHYSICAL_DEVICE_QUALIFICATION.md), exit criteria) | **Open**, not run yet |
| 2 | CI on the commit you ship. iOS workflow: Linux tests, Release simulator launch, device compile, native archive + IPA. The iPad screenshot step is being fixed separately; see below | Green on `main` except that step |
| 3 | Apple setup, once: App ID `com.hakmicapitalgroup.hcgmail`, App Store Connect app record, Apple Distribution certificate on your Mac, App Store provisioning profile (APP_STORE_SUBMISSION.md §2a) | Yours; not verifiable from here |
| 4 | Export compliance answered for each build ([APP_STORE_CONNECT.md](APP_STORE_CONNECT.md) §5, now including the AES-256-GCM encryption at rest added in 1.0.0-beta.2) | Your decision |
| 5 | App Privacy answers unchanged: offline copies, Outbox and keys stay on the device and are not collected. The connectivity checks of Network Doctor are not in the iOS build, so no third-party service is contacted | Re-confirm in App Store Connect |
| 6 | Internal testers only for the first build (no Beta App Review); external testing is a separate decision | Yours |

## Version and build number: yours

- **Marketing version:** `1.0.0` from `<Version>` in `HCG Mail Project/Desktop/Directory.Build.props`. Override it
  with `HCG_IOS_MARKETING_VERSION` if App Store Connect expects another value. Apple allows only numbers, so the iOS
  build shows 1.0.0, not 1.0.0-beta.2.
- **Build number (`CFBundleVersion`):** set with `HCG_IOS_BUILD_NUMBER` for `scripts/export-ipa.sh`, or
  `-p:ApplicationVersion=` for `dotnet publish`.
  - It defaults to 1 when you set nothing. Nothing in this repository or in CI increases it or records which numbers
    you used.
  - It must be higher than every build already uploaded for that version.
- The build that goes to TestFlight should be a **new** distribution-signed build of the same commit that passed the
  physical qualification. The development-signed IPA from P3 cannot be uploaded.

## Build for upload (summary)

```bash
cd "HCG Mail Project/iOS"
export HCG_IOS_SIGNING_IDENTITY="Apple Distribution: <Name> (<TEAMID>)"
export HCG_IOS_PROVISIONING_PROFILE="<App Store profile name or UUID>"
export HCG_IOS_BUILD_NUMBER=<your number>
scripts/export-ipa.sh
```

- **Output, at fixed paths:**
  - `artifacts/ios/ipa/HCGMail.iOS.ipa` and its `.sha256`;
  - `artifacts/ios/archive/HCGMail.iOS.xcarchive`;
  - `artifacts/ios/verification.txt`, where every line must read OK. That covers Apple Distribution, App Store
    profile, `get-task-allow` false, SDK ≥ 26, icon, privacy manifest and entitlements.
- The script stops before uploading. Upload the `.ipa` yourself with Transporter.
- HCG Executable Maker does not upload to App Store Connect either.

## Notes for the first testers (known for this build)

- **Network Doctor is desktop-only.** On iPhone, connection problems are diagnosed with Settings › account › Edit and
  test › Test connection.
- **No new-mail checks in the background and no notifications.** The app reconnects when it is opened again.
- **No Outbox list and no setting for offline copies on iPhone.** A message whose send result was unknown stays held,
  because only the desktop app can review it.
- **Microsoft sign-in** appears only in a build configured with a Microsoft client ID. Without one it is hidden.

## Related work in progress

- **iPad screenshot step in CI.** The app install on the iPad simulator times out on overloaded GitHub runners, also
  on `main`. It is being fixed in its own PR and does not affect device builds.
- **Executable Maker's Mobile Build Studio iOS Archive/IPA.** Fixed in Maker by its own session (the `ArchiveDir`
  cause; DEVICE_INSTALL.md §6). Until then, use HCG Mail's scripts or HCG Impactor with the IPA from `dotnet publish`.
