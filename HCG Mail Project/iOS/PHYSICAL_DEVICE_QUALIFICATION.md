# HCG Mail on a physical iPhone — qualification procedure

**Status: not run yet.** No HCG Mail build has been installed on a physical iPhone and recorded here. Mark a step
passed only after doing it on the device. Feature work on iOS is frozen until this passes. Fixes for failures found
here are the only iOS changes.

The procedure qualifies the **native .NET device build**:
- built on your Mac;
- signed with your Apple Development identity;
- installed by cable;
- tested against two real, dedicated mailboxes.

It does not upload anything. TestFlight comes afterwards ([TESTFLIGHT_READINESS.md](TESTFLIGHT_READINESS.md)).

## What this build has on iPhone, and what it does not

This list was checked against the source on `main`. The procedure tests only what exists; each gap is recorded, not
worked around.

| Area | On iPhone |
|---|---|
| IMAP/SMTP accounts, Keychain passwords | Yes. iOS Keychain, `WhenUnlockedThisDeviceOnly`, not synchronised to iCloud. |
| Offline copies of opened mail (encrypted, per-account key in the Keychain) | Yes, always on with the defaults: 30 days, 250 MB. **No setting on iPhone** to change them, turn them off or delete them; the desktop app has these settings. |
| Outbox: messages sent without a connection wait and go out when it returns | Yes. The composer shows the status. **No Outbox list on iPhone.** A message whose send result is unknown, because the connection broke during sending, waits for review. That review exists only in the desktop app, so on iPhone such a message stays held (see P10). |
| Automatic reconnection (backoff 5 s → 5 min; immediately after a long suspension) | Yes. The message list shows an offline notice with **Retry**. |
| **Network Doctor** | **Not on iPhone.** Mail Health on iPhone has Checks, Analyze, History and Reputation. The connection test is in Settings › account › **Edit and test** › Test connection. |
| Checking for mail in the background, notifications | **No.** The app has no background modes. iOS suspends it in the background, and it reconnects when you return. |
| Attachments | Receiving: saved through the iOS share sheet. Sending: **Attach** in the composer (Files). |

## 0. What you need

| | |
|---|---|
| Mac | Xcode **27.2** selected, .NET 10 SDK, iOS workload **27.2.10000**. `scripts/doctor.sh` must pass, apart from the simulator runtime. |
| iPhone | iOS **16.0 or later**, with a SIM or eSIM that has mobile data (for P12), a USB cable, and a passcode. Record the model and iOS version. |
| Apple Developer | An **Apple Development** certificate with its private key in this Mac's login keychain. A **development provisioning profile** for `com.hakmicapitalgroup.hcgmail` that includes this iPhone's UDID. |
| Mailboxes | Two **dedicated test mailboxes** (A and B) on an IMAP/SMTP provider, with app passwords where the provider requires them. Never use a personal, HCG or customer mailbox. Test mail goes only between A and B. |
| Test messages, sent to A beforehand from B | One plain-text message, one HTML newsletter with remote images, and one message with a **PDF** and a **JPEG** attached. |
| Source | HCG Mail `main` at a recorded commit (`git rev-parse HEAD`) with a clean checkout (`git status --short` prints nothing). |

Screenshots must not show real addresses or message content other than the test mailboxes'. Crop or blur anything
else.

## 1. Signing (P1–P2)

**P1 — Apple Development identity.**
```bash
security find-identity -v -p codesigning | grep "Apple Development"
```
Expected: exactly the identity you will use, marked valid. Write its name, *Apple Development: <Name> (<TEAMID>)*,
into the untracked `signing.local.props` (copy `signing.local.example.props`). Never commit it; git ignores it.

**P2 — compatible provisioning profile.**
```bash
cd "HCG Mail Project/iOS"
scripts/install-device.sh --list                                  # this iPhone's identifier (UDID)
python3 -I scripts/find-dev-signing.py com.hakmicapitalgroup.hcgmail <UDID>   # identity + development profile that include it
python3 -I scripts/verify-ios-build.py profile --profile "<profile name or UUID>" --identity "Apple Development: <Name> (<TEAMID>)"
```
Expected:
- the profile is a *development* profile for `com.hakmicapitalgroup.hcgmail`, or a wildcard that covers it;
- it is not expired;
- it contains this identity's certificate and this iPhone's UDID;
- its team matches the identity.

Put the profile name or UUID into `signing.local.props`. If no profile exists, follow DEVICE_INSTALL.md §3, where
Xcode creates one in two minutes.

## 2. Build, install and launch (P3–P5)

On the iPhone, once:
- Settings › Privacy & Security › **Developer Mode** → On (the phone restarts);
- connect it by cable, unlock it, and tap **Trust**.

**P3 — build the development-signed device app, archive and IPA.** From `HCG Mail Project/iOS`, choose the build
number yourself:
```bash
A="$(cd ../.. && pwd)/artifacts/ios"; rm -rf "$A/archive/HCGMail.iOS.xcarchive"
dotnet publish src/HCG.Mail.iOS/HCG.Mail.iOS.csproj -c Release -f net10.0-ios -r ios-arm64 -p:ApplicationVersion=<build> \
  -p:ArchiveOnBuild=true -p:ArchiveDir="$A/archive/HCGMail.iOS.xcarchive" \
  -p:BuildIpa=true -p:IpaPackagePath="$A/ipa/HCGMail.iOS.ipa"
python3 -I scripts/verify-ios-build.py app "$A/archive/HCGMail.iOS.xcarchive/Products/Applications/HCGMail.iOS.app" --kind device --build-number <build>
shasum -a 256 "$A/ipa/HCGMail.iOS.ipa"
```
Expected:
- `Created the package: …/HCGMail.iOS.ipa`;
- the archive at the fixed path;
- the verifier passes: bundle ID, version 1.0.0 (<build>), iOS 16.0+, SDK `iphoneos27.2`, signed by *Apple
  Development*, `application-identifier` `<TEAMID>.com.hakmicapitalgroup.hcgmail`, the two Keychain groups, and the
  profile embedded.

Record the SHA-256.

**P4 — install.** Use one route and record which. Route A is the reference.
- **A. HCG Mail's script:** `scripts/install-device.sh` (DEVICE_INSTALL.md §4). It builds, checks, installs with
  `devicectl` and starts the app. It also finds the signing itself.
- **B. HCG Impactor**, which installs the IPA from P3 without re-signing it. This is also Impactor's first
  physical-device validation:
  ```bash
  M="/Applications/HCG Executable Maker.app/Contents/MacOS/HCGExecutableMaker"
  "$M" --impactor devices                      # this iPhone: USB · Paired · Developer Mode on · install yes
  "$M" --impactor profiles --ipa "$A/ipa/HCGMail.iOS.ipa"
  "$M" --impactor plan "$A/ipa/HCGMail.iOS.ipa" --device usb --report ~/Desktop/impactor-reports
  "$M" --impactor install "$A/ipa/HCGMail.iOS.ipa" --device usb --launch --report ~/Desktop/impactor-reports
  ```
  In the app this is HCG Impactor › drop the IPA › Device Manager (tick the iPhone) › Deployment Queue › **Check**,
  then **Install**. Record `report.md`.
- Not available yet: Executable Maker's *Mobile Build Studio* iOS **Archive/IPA** outputs. They stop with "created no
  .xcarchive" until Maker's `ArchiveDir` fix is merged (cause and evidence: DEVICE_INSTALL.md §6).

Expected: the install succeeds. A failure is explained. Record the message verbatim and the matching row of
DEVICE_INSTALL.md › Troubleshooting.

**P5 — launch.** Start HCG Mail from the home screen, with the cable unplugged.
Expected:
- the welcome screen appears within about 5 s;
- no crash;
- the notch, Dynamic Island and home indicator do not cover content;
- the app follows light and dark mode;
- Settings › About shows the version (record it; it must match 1.0.0 and <build>) and *Password storage: iOS
  Keychain (this device only)*.

If it closes at once, collect the crash: Xcode › Devices and Simulators › the iPhone › *Open Recent Logs*. Send it
with the commit.

## 3. Mailbox (P6–P9)

**P6 — real mailbox authentication.** Add mailbox A with **Add email account**.
- Expected:
  - the provider's IMAP/SMTP settings are found or entered;
  - the connection test passes every stage;
  - the inbox loads.
- Then the **wrong-password check**, one attempt only so the provider doesn't lock the mailbox. In Settings › account
  › **Edit and test**, enter a wrong password and tap Test connection.
  - Expected: an authentication error that names the password, not "network".
  - The app does not retry it on its own: watch for 2 minutes.
  - Restore the right password.

**P7 — IMAP and SMTP connectivity.** Edit and test › **Test connection** with the right password.
Expected: DNS, network, TLS (certificate valid), IMAP sign-in, folders and SMTP sign-in each pass. Record the server
names and ports. Mail Health › **Checks** with the default selectors: the results are shown as Verified, Warning,
Failed or Not tested.

**P8 — sending and receiving.** Add mailbox B on the same iPhone, or use B on another device.
1. From A, send to B a plain-text message with a unique subject (`HCG-P8-<time>`).
   - Expected: "Sent", or the provider's reply.
   - The copy appears in A's **Sent**.
   - Mail Health › **History** shows *Accepted* with the server's reply.
2. On B: it arrives once. Reply from B.
3. On A: pull to refresh.
   - Expected: the reply arrives.
   - It is unread, then read after opening (if *Mark as read when opened* is on).
4. Open the HTML newsletter.
   - Expected: remote images are blocked until **Load remote images**.
   - A tapped link shows its real destination first.

**P9 — attachments.**
- **Receiving.** Open the message with the PDF and JPEG.
  - Both are listed with their sizes.
  - Share › **Save to Files** saves each one, and Files opens them.
  - HCG Mail never opens or runs them itself.
- **Sending.** Compose to B, tap **Attach**, and pick a PDF from Files.
  - The attachment is listed and can be removed.
  - Send. On B it arrives with the same name and size.
  - Its SHA-256 matches, compared on the Mac with `shasum -a 256`.

## 4. Persistence and offline (P10–P11)

**P10 — Keychain persistence.**
1. Force-quit HCG Mail (swipe up in the app switcher) and start it again.
   - Expected: the accounts are there and connect without asking for a password.
2. Restart the iPhone, unlock it and start HCG Mail.
   - Expected: the same.
3. Remove mailbox B in Settings › **Remove**, then add it again.
   - Expected: it asks for the password, so the old one was deleted.
4. Record this; don't fix it during the run. On iOS, Keychain items can outlive an app that is deleted.
   - Delete HCG Mail, reinstall it (P4), and start it.
   - Expected: the account list is empty, since app data was deleted with the app.
   - Add A again and record whether it asks for the password. Either answer is recorded as observed.

**P11 — offline copy and Outbox recovery.**
1. Online, open three messages in A's inbox.
2. **Airplane Mode** on, with Wi-Fi off as well.
   - Expected:
     - the inbox stays visible;
     - the offline notice says the list may be out of date and offers **Retry**;
     - the three opened messages still open, from the encrypted offline copy;
     - an unopened message cannot be loaded: record the exact text shown. It must be an error, not a blank or wrong message.
3. Force-quit and start HCG Mail, still in Airplane Mode.
   - Expected: the inbox and the three opened messages are shown from the offline copy, with the offline notice.
   - No password prompt.
4. Still offline, compose to B: subject `HCG-P11-<time>`, with one small attachment.
   - Expected before sending: the composer says *"… is offline. Sending puts the message in the Outbox; it goes out
     automatically when the account reconnects."*
   - After **Send**: *"Saved in the Outbox"*. It is not shown as sent.
5. Force-quit HCG Mail. Start it again, still offline.
6. Airplane Mode **off**.
   - Expected, within about a minute and without any tap: the message is sent **once**.
   - B receives exactly one copy with the attachment.
   - A's Sent has one copy.
   - Mail Health › History shows it.
7. Record, don't force: if a message ever reports that its result is unknown, it is held for review. Note the time,
   and whether B received it, and stop there. The iPhone build cannot release it (desktop only).

## 5. Networks (P12–P13)

**P12 — Wi-Fi and cellular.** Record for each row whether the app recovered without a tap, and how long it took.

| # | Do | Expected |
|---|---|---|
| a | Wi-Fi only (mobile data off): refresh, send A→B | Works |
| b | Mobile data only (Wi-Fi off): refresh, send A→B | Works. If it fails, check Settings › Mobile Data › HCG Mail is allowed |
| c | App open on the inbox; switch Wi-Fi → mobile data (Wi-Fi off) | The next refresh works. Record whether the offline notice appeared and how long recovery took |
| d | Switch back mobile data → Wi-Fi | The same |
| e | Start sending a message with a 5 MB attachment, then turn Wi-Fi off during sending (mobile data on) | It is either sent or kept in the Outbox and sent later: **never two copies at B**. If its result is unknown, record it as in P11 step 7 |
| f | Low Data Mode on (Settings › Wi-Fi › (i) / Mobile Data Options) | Works; note anything unusual |
| g | Optional: a network with a sign-in page (hotel or café Wi-Fi) before signing in | A clear connection error, no certificate bypass, no password sent. After signing in to the network, Retry works |

**P13 — connection diagnostics** (Network Doctor is not on iPhone). In each network state of P12, if a refresh fails,
open Edit and test › **Test connection** and record the stage that fails and its message. The message must name the
network, the certificate, the password or the server, not "unknown error". On the Mac, the desktop app's Network
Doctor can be run on the same network for comparison: record its verdict and confidence.

## 6. Restart and background (P14)

| # | Do | Expected |
|---|---|---|
| a | Home screen for 30 s, then back | The inbox is as left. The next refresh works |
| b | Home screen for 5 min, then back | It reconnects on its own within a few seconds (suspension detected). The refresh works with no password prompt |
| c | Lock the iPhone for 10 min, unlock, open HCG Mail | The same |
| d | While in the background, send B→A | **Nothing in the background:** no notification, no new mail until you return and refresh. This is expected for this build (no background modes) |
| e | Compose with an attachment, switch to Files and back | The draft and the attachment are still there |
| f | Incoming call or Siri while HCG Mail is open | It returns to the same screen |
| g | Open 10 large apps, then return (iOS may end HCG Mail) | It starts again and the accounts are there. Record whether a draft that was open survived |
| h | Rotate the phone; Text Size at the largest setting | Text is readable and nothing overlaps (record screenshots) |

## Results

Fill in one copy of this table per device and keep it with the release record (not in the repository if it contains
personal data).

| | |
|---|---|
| Date, tester | |
| Commit, build number, IPA SHA-256 | |
| Mac, macOS, Xcode | |
| iPhone model, iOS version, carrier/network | |
| Install route (A script / B Impactor), report | |
| Mailbox provider (no addresses) | |

| Step | Result (✅ / ❌ / ⚠️) | Notes, times, messages |
|---|---|---|
| P1 identity | | |
| P2 profile | | |
| P3 build, archive, IPA | | |
| P4 install | | |
| P5 launch | | |
| P6 authentication (+ wrong password once) | | |
| P7 IMAP/SMTP connection test | | |
| P8 send/receive, Sent, History | | |
| P9 attachments in and out | | |
| P10 Keychain: quit, reboot, remove, reinstall | | |
| P11 offline copy, Outbox, single delivery | | |
| P12 a–g Wi-Fi / mobile data | | |
| P13 connection diagnostics | | |
| P14 a–h background and restart | | |

**The native device build is qualified when:**
- P1–P11 and P12 a–e and P14 a–g pass on at least one iPhone with the newest iOS;
- nothing at B ever arrives twice;
- no step asks for a password it already has, or sends one to a server it should not.

⚠️ rows are allowed only when their notes explain why the behaviour is acceptable for a first TestFlight. Record a
❌ with the exact message, a crash log if any, and the commit. Fix it before TestFlight.
