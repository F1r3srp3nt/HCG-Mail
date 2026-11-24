# HCG Mail on your own iPhone

**Goal:** HCG Mail installed, signed in to a mailbox and working on a physical iPhone. This uses your Mac (an Apple
silicon Mac such as the M4 Max) and a USB-C cable.

There are two ways to get the app onto the phone. Both build the same Release configuration from the same commit.

| | A. Direct install (this guide) | B. TestFlight |
|---|---|---|
| Signed with | your **Apple Development** certificate | HCG's **Apple Distribution** certificate |
| Built by | `scripts/install-device.sh` | `scripts/export-ipa.sh` (APP_STORE_SUBMISSION.md, step 2) |
| Reaches the phone | cable or the same Wi-Fi, in a few minutes | upload with Transporter, processing, then the TestFlight app |
| Needs | the iPhone's UDID in a Development profile | an App Store Connect app record; **you** upload it |
| Use it for | first install, daily testing while developing | testing the exact build you will submit (TESTFLIGHT_PLAN.md) |

Start with **A**. It gets the app working on your iPhone today, and nothing leaves your Mac.

## 1. The Mac, once

1. **Xcode 26.3** (the pinned toolchain; `toolchain.props` → `HcgRequiredXcode`):
   * If `xcodebuild -version` already says 26.3, skip this.
   * Otherwise download Xcode 26.3 from <https://developer.apple.com/download/all/> and keep it next to any newer
     Xcode as `/Applications/Xcode_26.3.app`.
   * Then:
     ```bash
     sudo xcode-select -s /Applications/Xcode_26.3.app/Contents/Developer
     sudo xcodebuild -license accept
     xcodebuild -runFirstLaunch
     ```
   * The pinned .NET iOS workload requires exactly this Xcode. A newer Xcode stops the build with a clear error.
2. **.NET 10 SDK** for Arm64 from <https://dotnet.microsoft.com/download/dotnet/10.0>. `global.json` accepts
   10.0.100 or newer.
3. **The repository**, at the commit you want on the phone:
   ```bash
   git clone https://github.com/F1r3srp3nt/HCG-Mail.git   # or: cd HCG-Mail && git switch main && git pull
   cd "HCG-Mail/iOS App Store Submission"
   scripts/install-workload.sh      # the pinned .NET iOS workload 26.2.10233 (asks for your password)
   scripts/doctor.sh                # must end with "Ready"
   ```

## 2. The iPhone, once

1. **iOS 16.0 or later** (Settings › General › About).
2. Connect it to the Mac with the cable, unlock it, and tap **Trust** and enter its passcode.
3. **Developer Mode:**
   * Turn it on in Settings › Privacy & Security › Developer Mode, then restart when asked.
   * After the restart, confirm **Turn On**.
   * If the switch is missing, connect the phone to the Mac once with Xcode open (Window › Devices and Simulators).
     The option then appears.
4. Find its name and **UDID**:
   ```bash
   scripts/install-device.sh --list
   ```

## 3. Apple Developer account, once

Use HCG's team on <https://developer.apple.com/account>. Never commit certificates, profiles or their passwords.

1. **App ID:** Identifiers → `com.hakmicapitalgroup.hcgmail`, with **Keychain Sharing** enabled. It is the same App
   ID as for the App Store; create it once.
2. **Device:** Devices → **+** → the iPhone's name and the UDID from `--list`.
3. **Apple Development certificate:**
   * Xcode › Settings › Accounts → HCG's team → Manage Certificates → **+** → *Apple Development*.
   * The private key is created in your login keychain.
   * Check with `security find-identity -v -p codesigning`. It lists `"Apple Development: <Your Name> (<TEAMID>)"`.
4. **Development profile:**
   * Profiles → **+** → *iOS App Development*.
   * App ID `com.hakmicapitalgroup.hcgmail`, the certificate from step 3, and the iPhone.
   * Name it **`HCG Mail Development`**.
   * Install it: Xcode › Settings › Accounts → *Download Manual Profiles*, or double-click the downloaded file.
   * Add another device later by editing the profile and downloading it again.

## 4. Build, check, install and start

```bash
cd "HCG-Mail/iOS App Store Submission"
export HCG_IOS_DEV_IDENTITY="Apple Development: <Your Name> (<TEAMID>)"   # exactly as find-identity prints it
export HCG_IOS_DEV_PROFILE="HCG Mail Development"
scripts/install-device.sh                      # the one connected iPhone; or: scripts/install-device.sh "<name or UDID>"
```

What it does, and where it stops if something is wrong:

1. **Device:**
   * It must be paired and run iOS 16.0 or later.
   * Without a name, the only connected device is used.
2. **Signing setup, before building:**
   * the identity is in the keychain with its private key;
   * the profile is for `<TEAMID>.com.hakmicapitalgroup.hcgmail`, from the same team, not expired, allows the
     Keychain group, and contains your certificate;
   * **the iPhone's UDID is in the profile**.
3. **Build:**
   * Release, `ios-arm64`, full AOT, the configuration that ships, signed for development.
   * The first build takes several minutes.
   * Output: `artifacts/ios/device-signed/Release/HCGMail.iOS.app`.
   * For quicker builds while iterating, use `HCG_IOS_CONFIGURATION=Debug`.
4. **Check** (`verify-ios-build.py`), the same checks as the App Store build, except those that only apply to
   distribution:
   * bundle ID and build number;
   * minimum iOS, iPhone + iPad, the iOS 26 SDK;
   * the compiled icon and the launch screen;
   * the privacy manifest;
   * a valid signature;
   * entitlements limited to the app identifier, team and Keychain groups;
   * the embedded profile.
5. **Install and start** with `xcrun devicectl`, by cable or over the same Wi-Fi.

To reinstall the last build without rebuilding, run `scripts/install-device.sh --no-build`.

The first time, iOS may say **"Untrusted Developer"**. Open Settings › General › VPN & Device Management, tap your
Apple Development profile and choose **Trust**, then open HCG Mail.

## 5. First run: is it working?

Use a **dedicated test mailbox**, never a customer mailbox. Record anything unexpected with a screenshot (Side
button + Volume Up).

- [ ] The home-screen icon is the HCG Mail icon, named "HCG Mail". It opens to the HCG launch screen, then the
      welcome screen with **Add email account**. No crash, no sample data.
- [ ] No Microsoft or Google button. They are hidden until those sign-ins are configured.
- [ ] **Add email account** → your IMAP/SMTP details → **Test connection**. All six steps pass: DNS, network,
      encryption, IMAP sign-in, folders, SMTP sign-in. A wrong password gives a clear error and saves nothing.
- [ ] Save. The Inbox loads with the right senders, subjects and times, and folders are listed.
- [ ] Open an HTML message and a plain message. Remote images stay blocked until you tap **Load remote images**.
      A link shows its address and opens in Safari only after you confirm.
- [ ] Send a message to yourself. It is accepted, appears once in Sent, and arrives.
- [ ] Open an attachment through the share sheet. Attach a file from Files and send it.
- [ ] Swipe the app away and reopen it, then restart the iPhone and reopen it again. The account is still there with
      **no password prompt** (iOS Keychain).
- [ ] Nothing is hidden behind the Dynamic Island or the home indicator, in portrait or landscape, and at the largest
      text size.
- [ ] With Airplane Mode on, refresh shows an offline notice and loaded mail stays visible. With it off again,
      refresh works.

The full 14-area plan, which is also what TestFlight testers follow, is **[TESTFLIGHT_PLAN.md](TESTFLIGHT_PLAN.md)**.

## Troubleshooting

| Message | Cause and fix |
|---|---|
| `the device is not paired with this Mac` | Connect it by cable, unlock it, tap **Trust**. |
| `FAIL device in profile` | The UDID is not in the profile. Add the device in the developer account (Devices), edit the profile to include it, download it again. |
| `FAIL identity` / `certificate` | The certificate's private key is not on this Mac, or the profile was made for another certificate. Create the certificate on this Mac (§3.3) and regenerate the profile with it. |
| `error: … requires Xcode 26.3` | Select Xcode 26.3 (§1.1). |
| Install fails: *Developer Mode disabled* | §2.3. |
| Install fails: *unable to verify app* / *untrusted* | Trust the developer profile on the phone (§4). Check the phone's date and time are set automatically. |
| Install fails: *a different signature* | A TestFlight or older build of `com.hakmicapitalgroup.hcgmail` is installed. Delete it from the phone first. Its local data (account list, settings) is removed with it; mail on the server is not. |
| The app starts, then closes immediately | Collect the crash: Xcode › Window › Devices and Simulators → the iPhone → *Open Recent Logs*, or Settings › Privacy & Security › Analytics & Improvements › Analytics Data → `HCGMail.iOS-…`. Send it along with the commit (`git rev-parse --short HEAD`). |

A development build stays installable while the profile and certificate are valid, usually one year. A TestFlight
build of the same app replaces it on the phone, and the reverse is also true.
