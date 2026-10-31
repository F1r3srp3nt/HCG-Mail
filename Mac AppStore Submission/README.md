# HCG Mail — Mac AppStore Submission

**Status: reserved — not started.** Nothing has been built for, uploaded to or submitted to the Mac App
Store. No certificates, provisioning profiles or App Store Connect records exist for HCG Mail.

This folder is reserved for the **Mac App Store distribution** of HCG Mail.

## Planned responsibilities

| Area | Notes |
|---|---|
| Mac App Store packaging | App bundle built from the existing application, packaged as a signed `.pkg` for App Store upload |
| App Sandbox compatibility | Required for the Mac App Store; HCG Mail must keep working inside the sandbox (network access, user-selected download folder, container-relative settings folder) |
| Required entitlements | `com.apple.security.app-sandbox`, `com.apple.security.network.client` (IMAP/SMTP), `com.apple.security.files.user-selected.read-write` (attachments), Keychain access group |
| App Store signing and provisioning | Apple Distribution / Mac Installer Distribution certificates and a Mac App Store provisioning profile |
| App Store Connect preparation | App record, bundle ID, pricing and availability, age rating |
| Privacy disclosures | App Privacy "nutrition label": email content and credentials stay on the device and the user's own mail servers; no tracking, no telemetry |
| App review requirements | Review notes, a demo account if Apple requests one (never real HCG credentials), compliance with App Review Guidelines |
| Release metadata and screenshots | Description, keywords, support and privacy URLs, Mac screenshots in the required sizes |

## Relationship to the existing application

There is **one** HCG Mail implementation, in [`../Windows executable Application/`](../Windows%20executable%20Application/).
The App Store build must reuse it; only sandbox entitlements, store packaging, metadata and
submission material should live here. It shares packaging work with
[`../Mac OS executable Application/`](../Mac%20OS%20executable%20Application/) — that direct-download
version should come first, because it does not require the sandbox.

Sandbox points to verify before submission: the Keychain implementation inside the sandbox, the
download folder (user-selected only), and the settings folder location (inside the app container).

## Requirements before work can start (founder approval)

* Apple Developer Program membership for Hakmi Capital Group
* App Store Connect access and a reserved app name / bundle identifier
* Privacy policy URL

No credentials, certificates or provisioning profiles may ever be committed to this repository.
