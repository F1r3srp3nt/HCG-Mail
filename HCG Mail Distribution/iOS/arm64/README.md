# HCG Mail for iOS (arm64)

Drop folder for **iPhone and iPad (iOS 16.0 or later)** builds (`ios-arm64`).

**Put here:** a signed `.ipa`, a device `.app` bundle, or the HCG Mail Unleashed rootless `.deb`.

**How it is built:** On a Mac, from `HCG Mail Project/iOS/`: `scripts/export-ipa.sh` for a signed `.ipa`, or the device build in `hcg-apps.json`. The Unleashed `.deb` comes from `.github/workflows/unleashed.yml`.

**What has been verified:** The Release device build (`ios-arm64`, full AOT, unsigned) compiles in CI (`.github/workflows/ios.yml`, job *device-aot*) and the app starts in iOS simulators. On a physical jailbroken iPhone, Unleashed 0.1.0~beta1 installed but failed to start; that investigation is open. Signing needs your Apple certificate and provisioning profile.

Git ignores everything in this folder except this README, so executables, app bundles and portable folders copied
here are never committed. Before you share a file, compare its SHA-256 checksum with the one recorded for that build.
