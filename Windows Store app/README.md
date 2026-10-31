# HCG Mail — Windows Store app

**Status: reserved — not started.** Nothing in this folder has been built, packaged, submitted or
certified. There is no Microsoft Store listing for HCG Mail yet.

This folder is reserved for the **Microsoft Store distribution** of HCG Mail.

## Planned responsibilities

| Area | Notes |
|---|---|
| Store-compatible packaging | Package the existing application as MSIX (Windows Application Packaging or `dotnet publish` + MSIX tooling) |
| MSIX manifests | `Package.appxmanifest`: identity, capabilities (`internetClient` for IMAP/SMTP), visual elements, minimum Windows version |
| Store identity | Package name, publisher and publisher display name **from Partner Center** — they must be reserved there by HCG, never invented |
| Store assets and screenshots | Square/wide tiles, splash screen and Store logos generated from the official HCG Mail icon (`../Windows executable Application/assets/icons/hcg-mail-1024.png`), plus Store screenshots |
| Versioning | Four-part MSIX version kept in step with the application version (`Directory.Build.props`) |
| Release preparation | Release notes, age rating, privacy policy URL, Store description and keywords |
| Store certification | Meet Microsoft Store policies (privacy disclosure for email data, no unexpected network behaviour, Windows App Certification Kit) |

## Relationship to the existing application

There is **one** HCG Mail implementation, in [`../Windows executable Application/`](../Windows%20executable%20Application/).
The Store package must reuse that code (core, email, infrastructure and UI projects) — only
packaging, manifests, assets and Store-specific settings should live here.

Points to resolve before packaging:

* Credential storage: Windows Credential Manager is available to packaged (MSIX) desktop apps.
* Settings folder: MSIX virtualises `%LOCALAPPDATA%`; the current per-user folder keeps working but
  is per-package.
* Updates come from the Store; the Store build must not include any other update mechanism.

## Requirements before work can start (founder approval)

* Microsoft Partner Center developer account for Hakmi Capital Group
* Reserved app name and the resulting package identity
* Privacy policy URL
