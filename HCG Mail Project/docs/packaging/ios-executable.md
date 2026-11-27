# HCG Mail — iOS Executable (direct installation)

> Packaging notes, formerly `iOS Executable/README.md` at the repository root. See the [index](README.md).

This note holds no source code. The iPhone/iPad app is in
[`../../iOS/`](../../iOS/) and there is only that one iOS app; this
folder describes how to install **that same app** outside the App Store (your own devices, ad-hoc, TestFlight).

## Install on your own device (development build)

Follow *Run on your own iPhone or iPad* in the [iOS README](../../iOS/README.md): an
*Apple Development* certificate and a *Development* provisioning profile for `com.hakmicapitalgroup.hcgmail`, then
`dotnet build … -r ios-arm64 -p:CodesignKey=… -p:CodesignProvision=… -t:Run`.

## Ad-hoc or TestFlight build

Use the same export script with an *Ad Hoc* (registered devices) or *App Store* (TestFlight) provisioning profile:

```bash
cd "../../iOS"
HCG_IOS_SIGNING_IDENTITY="Apple Distribution: Hakmi Capital Group (<TEAMID>)" \
HCG_IOS_PROVISIONING_PROFILE="<Ad Hoc or App Store profile>" \
HCG_IOS_BUILD_NUMBER=1 \
scripts/export-ipa.sh
```

The `.ipa` is written to `artifacts/ios/ipa/` (not committed). Ad-hoc builds are installed with Apple Configurator
or Xcode (Window → Devices and Simulators); TestFlight builds are uploaded manually with Transporter.
Nothing is uploaded or distributed automatically; every distribution requires founder approval.

## From Xcode

`../../iOS/HCGMail.xcodeproj` builds the same app from Xcode (simulator, or an unsigned compile for
devices). Signed builds — development, ad hoc or App Store — come from `scripts/export-ipa.sh`; see
[APP_STORE_SUBMISSION.md](../../iOS/APP_STORE_SUBMISSION.md).

## Building with HCG Executable Maker

See [ios-executable-hcg-executable-maker.md](ios-executable-hcg-executable-maker.md): which project Maker must select when the whole repository
is imported (`hcg-apps.json`), the commands to run on a Mac, and the diagnostics it should pass through.

## Not in this repository

Certificates, private keys (`.p12`, `.p8`), provisioning profiles and signed builds stay on the signing Mac.
