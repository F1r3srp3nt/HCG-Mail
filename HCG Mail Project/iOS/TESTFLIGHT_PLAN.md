# HCG Mail 1.0.0 — internal TestFlight validation plan

Before the first upload: [TESTFLIGHT_READINESS.md](TESTFLIGHT_READINESS.md) (gate, your build number) and the physical-device
qualification [PHYSICAL_DEVICE_QUALIFICATION.md](PHYSICAL_DEVICE_QUALIFICATION.md).

**Status:** not started. No build has been uploaded and no device has been tested. Record results below as they
happen; do not mark a step as passed before it has been done on a physical device.

## Setup

* **Build:** the Transporter upload of `artifacts/ios/ipa/HCGMail.iOS.ipa` (its SHA-256 is in
  `HCGMail.iOS.ipa.sha256`). In App Store Connect → TestFlight, check that the build shows **1.0.0 (<the build number you chose>)**.
* **Testers:** internal testers only (App Store Connect users on HCG's team). Internal testing needs no Beta App
  Review. External testing would, and needs separate approval.
* **Devices:** at least one iPhone (ideally a 6.1″ and a 6.9″ model) and one iPad, on the newest iOS/iPadOS and, if
  available, an older supported version (iOS 16 or 17).
* **Mailboxes:**
  * Two **dedicated test mailboxes** on an IMAP/SMTP provider, for example two Domene.no test addresses. Use them only
    for testing. Never use a real HCG or customer mailbox.
  * Send test mail only between these addresses.
  * Prepare one HTML newsletter-style message with remote images, one plain-text message, and one message with a PDF
    and an image attached.
* **Record:** device, OS version, result (✅ / ❌ / ⚠️), and notes or a screenshot for anything unexpected.

## On each physical device, before the test cases

You do the upload and the TestFlight distribution yourself. Nothing in this repository does them.

- [ ] **Build in TestFlight:** App Store Connect → TestFlight shows **1.0.0 (<your build number>)**, *Ready to Test*, with export
      compliance answered.
- [ ] **Same binary as checked:** its SHA-256 matches `artifacts/ios/ipa/HCGMail.iOS.ipa.sha256` from your
      `export-ipa.sh` run.
- [ ] **Tester access:** the tester is in an internal group that has this build, with the *App Manager*,
      *Developer* or *Admin* role.
- [ ] **TestFlight app:** installed on the iPhone/iPad from the App Store, signed in with the tester's Apple ID, and
      the invitation e-mail accepted.
- [ ] **Old copies removed:** any development build of HCG Mail (from `install-device.sh`, DEVICE_INSTALL.md) is
      deleted. Otherwise the TestFlight install may fail with a signature mismatch, and you could test the wrong
      binary.
- [ ] **Install:** TestFlight → HCG Mail → **Install**. Confirm the version shown is 1.0.0 (<your build number>).
- [ ] **Record:** device model, iOS version, and whether a **real SIM or Wi-Fi only**. Test at least once on
      cellular data.
- [ ] **Test mailboxes:** two are ready (see *Setup*), and their passwords are in your password manager, not in
      notes on the device.
- [ ] **Not on the device:** Developer Mode is **not** needed for TestFlight. If it is on, testing is still valid.

## Test cases

| # | Area | Steps | Expected result | iPhone | iPad |
|---|---|---|---|---|---|
| 1 | Fresh installation | Delete any earlier HCG Mail. Install from TestFlight. | Installs. Home-screen icon is the HCG Mail icon with the name "HCG Mail". | | |
| 2 | App launch | Open the app. | HCG launch screen, then the welcome screen with **Add email account**. No sample data, no crash. No Microsoft or Google buttons (not configured in this build). | | |
| 3 | Account setup | Add email account → enter test mailbox 1 (IMAP 993 SSL/TLS, SMTP 465 or 587) → **Test connection**. Then enter a wrong password and test again. | All six steps pass: DNS, network, encryption, IMAP sign-in, folders, SMTP sign-in. With the wrong password: a clear sign-in error, and nothing saved. | | |
| 4 | Mailbox connection | Save the account. | The account shows as Connected. Folders appear: Inbox, Drafts, Sent, Archive, Spam/Junk, Trash and others. | | |
| 5 | Inbox synchronization | Send a message from mailbox 2 to mailbox 1, then pull to refresh. Mark one message read on another client, then refresh. | The new message appears with the correct sender, subject and time. Read state matches the server. "Last refreshed" updates. | | |
| 6 | Reading messages | Open the HTML message, the plain-text message, and a long message. Tap **Load remote images**. Try **Readable** and **Plain text**. Tap a link. | The layout is correct and nothing is cut off. Remote images stay blocked until tapped. The link shows its real address and opens in Safari only after confirming. | | |
| 7 | Sending messages | Compose to mailbox 2 and send. Reply, reply all, forward. Save a draft, reopen it, send it. | Each send is reported as accepted by the server, appears in Sent once, and arrives at mailbox 2. The draft disappears from Drafts after sending. | | |
| 8 | Attachments | Open the received PDF and image (share sheet). Attach a file from Files to a new message, send it, open it at mailbox 2. Open a message whose attachment can run code. | Attachments open and share. The sent file arrives intact. The executable-type attachment is clearly marked and opens only after you choose to. | | |
| 9 | Session persistence | Force-quit the app (swipe up), then reopen it. Restart the device and reopen. | The account is still there, with **no password prompt** (iOS Keychain). The Inbox loads. Settings are kept. | | |
| 10 | Sign-out and account removal | Settings → the account → **Remove**. Relaunch the app. | The account is gone and the welcome screen shows. No mail is deleted from the server; check from another client. | | |
| 11 | Background and foreground | With the Inbox open, go to the home screen for a few minutes, then return. Lock and unlock. | The app resumes where it was. No crash, no duplicated messages. No background fetching is claimed. | | |
| 12 | Network interruption | Turn on Airplane Mode, then refresh and try to send. Turn it off and refresh. | An offline notice appears. Loaded messages stay visible. Sending fails clearly, without losing the text. After reconnecting, refresh works without signing in again. | | |
| 13 | iPhone and iPad layouts | iPhone: portrait and landscape, largest text size (Settings → Accessibility → Display & Text Size). iPad: portrait, landscape, Split View and Slide Over with another app. | Nothing is clipped or hidden behind the notch, Dynamic Island or home indicator. iPad shows mailboxes, list and reader side by side where there is room. VoiceOver reads the buttons. | | |
| 14 | Crash and error handling | Enter a non-existent server name. Use a server with a certificate for another name, if one is available. Open Mail Health. After testing, check App Store Connect → TestFlight → Crashes. | Each error is explained in plain language and the app keeps running. Mail Health shows SPF/DKIM/DMARC results. No crash reports. | | |

## Exit criteria for submitting for review

* Every row is ✅ on at least one iPhone and one iPad.
* No crash in TestFlight → Crashes.
* Any ⚠️ is understood and accepted by the founder.
* The same build number is the one selected on the App Store version page.

Submitting for review is a separate, founder-approved step (`APP_STORE_CONNECT.md`, section 7).
