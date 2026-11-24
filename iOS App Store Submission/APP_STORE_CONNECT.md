# HCG Mail 0.1.0 (1) — App Store Connect submission package

**Status:** draft for the founder's review.

* Nothing here has been entered into App Store Connect.
* Nothing has been uploaded, distributed through TestFlight, or submitted for review.
* Values marked **⟨HCG to supply⟩** must come from Hakmi Capital Group. They are not invented here.

| Field | Value |
|---|---|
| App name | **HCG Mail** |
| Bundle ID | `com.hakmicapitalgroup.hcgmail`. It must match the app record (App Information → General → Bundle ID). |
| SKU | suggestion: `hcg-mail-ios` (internal; never shown to users) |
| Version | **0.1.0**, build **1**. These are the build's `CFBundleShortVersionString` and `CFBundleVersion`; the App Store Connect version must be exactly `0.1.0`. |
| Platforms / devices | iPhone and iPad, iOS 16.0 or later |
| Primary language | English (U.S.) — the interface is in English only |

## 1. Store text (draft — check before use)

Apple's limits are noted. Nothing here mentions Microsoft or Google sign-in. Those are not available in this build,
so their buttons are hidden and the store text must not advertise them.

**Subtitle** (30 characters max): `Private email for your mailbox` (30)

**Promotional text** (170 max, can be changed without a new build):
> Connect the mailbox you already have. Encrypted connections only, passwords in the iOS Keychain, remote images blocked until you allow them — and no HCG account needed.

**Description** (4,000 max):
> HCG Mail is a focused email client for the mailbox you already have. Connect any standard IMAP/SMTP account — for example your company's domain email — and read, write and organise your mail on iPhone and iPad.
>
> PRIVATE BY DESIGN
> • Encrypted connections only, with full certificate checks.
> • Your password is stored in the iOS Keychain on your device.
> • HCG Mail talks only to your own mail provider's servers. There is no HCG server, no analytics, no advertising and no tracking.
> • Remote images in messages are blocked until you choose to load them, so senders can't see when you open their email.
> • Links show their real destination before they open.
>
> EVERYTHING YOU NEED FOR DAILY EMAIL
> • Inbox, folders, drafts, sent mail, archive, spam and trash.
> • Read HTML and plain-text messages, with a Readable view for hard-to-read layouts.
> • Compose, reply, reply all and forward, with attachments.
> • Flag, mark as read or unread, archive, move and delete.
> • Search loaded messages, or search the whole folder on the server.
> • Open and share attachments through the iOS share sheet; files that can run code are clearly marked.
>
> MADE FOR IPHONE AND IPAD
> • iPad split view, Slide Over and every orientation.
> • Dynamic Type, VoiceOver labels and Reduce Motion are respected.
> • Two looks: HCG Future (dark) and HCG Classic (light).
>
> SET UP IN MINUTES
> A step-by-step connection test checks the server name, network, encryption and sign-in for incoming and outgoing mail, and explains any problem in plain language. No HCG account or HCG Connect is required.
>
> MAIL HEALTH
> Check your domain's SPF, DKIM and DMARC records and see the history of what your outgoing server accepted.
>
> HCG Mail checks for new mail when you open it or pull to refresh; it does not run in the background.

Each claim above corresponds to a feature in this build: the iOS views, the shared mail engine, and the CI-verified
privacy manifest. Remove any line you do not want to commit to.

**Keywords** (100 characters max, comma-separated, no spaces):
`email,mail,imap,smtp,inbox,secure,private,domain,business,dkim,spf,dmarc,client,keychain` (88 characters)

**Category:**
* Primary: **Productivity**
* Secondary (optional): **Business**

**URLs:**

| Field | Value |
|---|---|
| Privacy Policy URL (required) | **⟨HCG to supply⟩**. It must be public, and should state what section 3 states. |
| Support URL (required) | **⟨HCG to supply⟩**. A page with a way to contact HCG. |
| Marketing URL (optional) | **⟨HCG to supply⟩** or leave empty |

**Copyright:** `© 2026 Hakmi Capital Group`. Confirm the exact legal entity name.

## 2. Screenshots

All screenshots must be real captures of HCG Mail.

**Where they come from:** CI job **"App Store screenshots"** in `.github/workflows/ios.yml`, workflow artifact
`app-store-screenshots`, kept for 90 days.

**How they are made:**
* Simulators: the Debug build in preview mode, on **iPhone 17 Pro Max** (6.9″, 1320×2868) and **iPad Pro 13-inch
  (M5)** (2064×2752). These are the sizes App Store Connect requires for an app that runs on iPhone and iPad.
* Screens: inbox, an open message, and the composer.
* Status bar: Apple's standard clean status bar (9:41, full signal), set with `simctl status_bar`.
* The job checks every image's pixel size.

**What they show:** the app's built-in **sample mailbox**, which is labelled "[Sample]" and has example senders.
It is the real interface with demonstration content, not a mock-up. The composer screenshots show the on-screen
keyboard, because the To field has focus, as it does when a user opens the composer. If you prefer more natural content, take the same
three screens from the TestFlight build with your dedicated test mailbox: Side button + Volume Up on an iPhone 17 Pro
Max / iPad Pro 13″. Never use a real customer mailbox.

**Upload order:**

| Order | iPhone 6.9″ | iPad 13″ |
|---|---|---|
| 1 | `iPhone17ProMax-inbox.png` | `iPadPro13inchM5-inbox.png` |
| 2 | `iPhone17ProMax-reader.png` | `iPadPro13inchM5-reader.png` |
| 3 | `iPhone17ProMax-compose.png` | `iPadPro13inchM5-compose.png` |

Optional: an App Preview video. None is prepared.

## 3. App Privacy ("nutrition label") — verified data flows

Apple asks whether **you (HCG) or third-party partners** collect data, meaning transmit it off the device in a way
HCG or a partner can access. The following is what the iOS build does, read from the code (`SignInPresentation`,
`MobileServices`, `HCG.Mail.Email`, `HCG.Mail.Infrastructure`) and the CI-checked privacy manifest:

| Data | Where it goes | Collected by HCG? |
|---|---|---|
| Email address, username, server names | Stored on the device (`accounts.json` in the app container); sent only to the mail servers the user enters | No |
| Mail password | iOS Keychain on the device; sent only to the user's IMAP/SMTP servers over TLS | No |
| Messages, attachments, contacts in messages | Exchanged only with the user's IMAP/SMTP servers; bodies are not stored on the device | No |
| Send history | On the device only: account address, server, Message-ID, number of recipients, server reply. No recipient addresses or content. | No |
| App log | On the device only (7-day retention): no passwords, no message content | No |
| DNS lookups (connection test, Mail Health) | The device's own DNS resolver | No |
| Certificate revocation lists | Fetched from the certificate authority named in the mail server's certificate | No |
| Remote images in a message | Fetched from the sender's image server only after the user taps *Load remote images* (or turns on automatic loading). No cookies are sent. | No (the sender's server sees the request, as in any mail client) |
| Microsoft / Google sign-in | **Not available in this build** (hidden, no network traffic) | — |
| Analytics, crash reporting, advertising, tracking SDKs | **None** in the app | — |

**Recommended answer:** *"No, we do not collect data from this app"*, which gives "Data Not Collected".

This is accurate for this build only if all of the following are true:
* HCG operates no server the app talks to (true for the code).
* HCG does not obtain users' data some other way through the app (for example, support e-mails are outside the app).
* You do not count the user's own mail provider as HCG's "third-party partner". Apple's definition covers partners
  whose code or services **you** integrated, such as analytics or ad SDKs. The user's chosen mail server is not one.

**Reconfirm the answer** if any of these is later added: Microsoft or Google sign-in, crash reporting, analytics, or
a support form inside the app.

**Privacy manifest** (`PrivacyInfo.xcprivacy`, verified in the Release binary by `verify-ios-build.py`):
* No tracking, no tracking domains, no collected data types.
* Required-reason APIs:

| API category | Reason | Used by |
|---|---|---|
| FileTimestamp | C617.1 | .NET runtime |
| SystemBootTime | 35F9.1 | .NET runtime |
| DiskSpace | E174.1 | .NET runtime |
| UserDefaults | CA92.1 | MSAL |
| ActiveKeyboards | 54BD.1 | Avalonia text input |

## 4. Age rating — guidance

Answer Apple's questionnaire truthfully:
* HCG Mail contains no violence, sexual content, gambling, contests, medical advice or similar content of its own.
* It displays **email from any sender the user receives mail from**, unfiltered, like any mail client.
* It does not browse the web: links open in Safari after confirmation.
* It has no user accounts with HCG, no social features and no in-app purchases.

Where the questionnaire asks about user-generated content, messaging or unrestricted web access, answer for those
facts. Apple then computes the rating. It is not predicted here.

## 5. Export compliance — checklist

Apple asks this for every build, because `ITSAppUsesNonExemptEncryption` is not set in `Info.plist`.

* **Uses encryption: yes.**
  * TLS for IMAP, SMTP and HTTPS through the platform and .NET implementations (`SslStream`).
  * Certificate validation.
  * RSA/SHA signature *verification* for DKIM (authentication only).
  * Keychain storage by iOS.
* **Proprietary or non-standard cryptography: none.** No encryption of user data beyond what TLS and iOS provide.

These facts usually qualify for the exemption for standard encryption in mass-market software (Category 5, Part 2).
Choosing the exemption, and whether a French encryption declaration is needed for distribution in France, is **HCG's
legal decision**. Once it is decided, `ITSAppUsesNonExemptEncryption` can be added to `Info.plist` in a later build so
the question stops appearing.

## 6. App Review information

| Field | Value |
|---|---|
| Sign-in required | **Yes**: the app needs an existing mailbox. |
| Contact | **⟨HCG to supply⟩**: name, phone and e-mail of the person Apple may contact |
| Demo account | **⟨HCG to supply⟩**. Use a **dedicated review mailbox** created only for Apple, never a real HCG or customer mailbox. See the field list after this table. |

Demo account fields:
* e-mail address and password;
* IMAP host, port 993, SSL/TLS;
* SMTP host, port 465 (SSL/TLS) or 587 (STARTTLS);
* a few messages already in it, including one HTML message and one with an attachment, so the reviewer sees content.

**Notes for the reviewer** (draft):
> HCG Mail is an email client for an existing IMAP/SMTP mailbox; it has no sign-up and no HCG account. To review: tap *Add email account*, enter the test mailbox details below (or tap *Test connection* first to see the six-step check), then open the Inbox. You can read the sample messages, open the attachment, and send a message to the same test address. Remote images are blocked until *Load remote images* is tapped. Mail Health (in the mailbox list) shows the domain's SPF/DKIM/DMARC records. The app does not run in the background and sends no data to Hakmi Capital Group.

## 7. Before *Submit for Review*: checklist

- [ ] App record exists with bundle ID `com.hakmicapitalgroup.hcgmail`.
- [ ] Version `0.1.0` created; build `0.1.0 (1)` uploaded with Transporter, processed, and tested in TestFlight
      (`TESTFLIGHT_PLAN.md`).
- [ ] Subtitle, description, keywords, category, copyright filled in.
- [ ] Privacy Policy URL and Support URL live.
- [ ] App Privacy answered (section 3) and published.
- [ ] Age rating questionnaire answered.
- [ ] Export compliance answered (section 5).
- [ ] Screenshots: 6.9″ iPhone and 13″ iPad.
- [ ] App Review contact, demo mailbox and notes entered.
- [ ] Pricing and availability (free?) and countries chosen.
- [ ] Release option chosen: manual release after approval is recommended for the first version.

Each of these, and *Submit for Review*, is done by HCG in App Store Connect.
