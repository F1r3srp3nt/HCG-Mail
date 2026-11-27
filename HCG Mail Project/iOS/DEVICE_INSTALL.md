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

1. **Xcode 27.2**, selected (`xcode-select -p` shows it), with its licence accepted once:
   ```bash
   sudo xcodebuild -license accept
   ```
   That one Xcode builds the app, opens the project, runs Simulator and installs on the iPhone. An Xcode 26.3 that
   you installed next to it for earlier HCG Mail builds is no longer used and can be deleted.
2. **.NET 10 SDK** for Arm64 from <https://dotnet.microsoft.com/download/dotnet/10.0>. `global.json` accepts
   10.0.100 or newer.
3. **The repository**, at the commit you want on the phone:
   ```bash
   git clone https://github.com/F1r3srp3nt/HCG-Mail.git   # or: cd HCG-Mail && git switch main && git pull
   cd "HCG-Mail/HCG Mail Project/iOS"
   scripts/install-workload.sh      # the pinned .NET iOS workload 27.2.10000 (asks for your password)
   scripts/doctor.sh                # "[xcode] OK Xcode 27.2 at …" means ready
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

## 3. Signing, once: let Xcode create it

You need an Apple Development certificate and a development profile for `com.hakmicapitalgroup.hcgmail` that
includes this iPhone. Xcode creates both:

1. Xcode › Settings › Accounts: sign in with the Apple ID of HCG's developer team.
2. Xcode › File › New › Project › iOS › **App**:
   * Product Name `hcgmail`;
   * Organization Identifier `com.hakmicapitalgroup`, so the Bundle Identifier reads exactly
     `com.hakmicapitalgroup.hcgmail`;
   * Team: HCG's team.
3. Pick your iPhone as the run destination at the top of the window and press **Run** (⌘R). Xcode registers the
   iPhone and creates the certificate and profile (*Automatically manage signing*). Close that project; it is only
   needed this once.

`install-device.sh` then finds the certificate and profile by itself. If it cannot, it says why: no certificate,
the profile does not include this iPhone, it has expired, or it was made for another certificate.

## 4. Build, check, install and start

```bash
cd "HCG-Mail/HCG Mail Project/iOS"
scripts/install-device.sh        # the one connected iPhone; or: scripts/install-device.sh "<name or UDID>"
```

What it does, and where it stops if something is wrong:

1. **Device:**
   * It must be paired and run iOS 16.0 or later.
   * Without a name, the only connected device is used.
2. **Signing setup, before building:** it finds your Apple Development certificate and a profile that includes this
   iPhone, then checks that:
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
   * minimum iOS, iPhone + iPad, the iOS 27 SDK and its scene manifest;
   * the compiled icon and the launch screen;
   * the privacy manifest;
   * a valid signature;
   * entitlements limited to the app identifier, team and Keychain groups;
   * the embedded profile.
5. **Install and start** with `xcrun devicectl`, by cable or over the same Wi-Fi.

To reinstall the last build without rebuilding, run `scripts/install-device.sh --no-build`.

**From Xcode instead:** open `HCGMail.xcodeproj` in your Xcode, choose the **HCG Mail** scheme and your connected
iPhone as the destination, and press **Build** (⌘B). It runs the same script: build, check, install and start.
Xcode's Run (⌘R) is not available for this kind of project; Build does the install.

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
| `no usable development profile … does not include this device` | Run the throwaway Xcode project once more on this iPhone (§3). Xcode adds the device to the profile. |
| `no Apple Development certificate` / `made for another certificate` | The certificate's private key is not on this Mac. Run §3 on this Mac; Xcode creates a certificate here and a matching profile. |
| `error: … requires Xcode 27.2` | Select Xcode 27.2 (§1.1). |
| Install fails: *Developer Mode disabled* | §2.3. |
| Install fails: *unable to verify app* / *untrusted* | Trust the developer profile on the phone (§4). Check the phone's date and time are set automatically. |
| Install fails: *a different signature* | A TestFlight or older build of `com.hakmicapitalgroup.hcgmail` is installed. Delete it from the phone first. Its local data (account list, settings) is removed with it; mail on the server is not. |
| The app starts, then closes immediately | Collect the crash: Xcode › Window › Devices and Simulators → the iPhone → *Open Recent Logs*, or Settings › Privacy & Security › Analytics & Improvements › Analytics Data → `HCGMail.iOS-…`. Send it along with the commit (`git rev-parse --short HEAD`). |

A development build stays installable while the profile and certificate are valid, usually one year. A TestFlight
build of the same app replaces it on the phone, and the reverse is also true.
