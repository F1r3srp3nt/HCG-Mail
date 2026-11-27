# HCG Mail 1.0 Public Beta (1.0.0-beta.1)

Version **1.0.0-beta.1**, build **3**. A public beta: free during testing, for feedback, not a final release.

HCG Mail is a private email app for your own mailbox over **IMAP and SMTP**. No HCG account is needed, and HCG runs no
server that your mail passes through.

## What's new since 0.1.0-beta.2

**Reading**
- Verification codes, amounts and buttons in transactional email no longer go missing. Codes inside form controls,
  white text on backgrounds the reader could not draw, and content in flex/grid layouts were affected. Stylesheets in
  messages now apply, and dark-mode rules no longer turn light designs dark. Original, Readable and Plain text views
  all keep the content.

**Sending**
- A message is never sent twice by accident. When the connection drops after the server started receiving it, HCG
  Mail says it may already have been sent and asks before sending again.
- Drafts save themselves to the server every minute while you write.
- Signatures per account (Settings › Signatures).

**Accounts**
- Guided setup with the published settings for Gmail, iCloud Mail, Yahoo Mail and Fastmail, and app-password
  instructions for each.
- Clear notices for Outlook.com/Hotmail and Proton Mail addresses, which cannot be added with a password here.
- An optional account description, such as "Work" or "Personal".

**Mail Health**
- A new Connection page showing sign-in, encryption and certificate, folders, last synchronization, last send, the
  last error and local data. Each result is checked, not assumed from the configuration.
- *Check connection* signs in without sending anything.
- *Copy diagnostics* shares the results without passwords, tokens, message content or full addresses.

**Settings**
- Organized into General, Appearance, Accounts, Reading, Composing, Signatures, Privacy & security, Mail Health, Storage
  and About. Only settings that work are shown.

**Platforms**
- Windows (x64, ARM64): installer and portable ZIP.
- New: macOS (Apple silicon, Intel) as a disk image.
- New: Linux (x64, ARM64) as a portable ZIP.

## Not in this beta

Microsoft (Outlook.com, Microsoft 365) and Google sign-in are not part of the Public Beta. Add mailboxes with IMAP
and SMTP. Existing Microsoft or Google accounts from earlier builds are kept unchanged; see `RELEASE_CAPABILITIES.md`.

## Downloads and checksums

Every file is listed with its SHA-256 in `SHA256SUMS.txt` and the release manifest. Compare before opening:
- Windows: `Get-FileHash <file>`
- macOS: `shasum -a 256 <file>`
- Linux: `sha256sum <file>`

## Known limitations

- **Not code-signed.**
  - Windows SmartScreen warns on first start (*More info › Run anyway*).
  - macOS Gatekeeper blocks the first start (right-click › *Open*).
  - Only do either if the checksum matches.
- **Linux:** no secure password store integration yet. Passwords are kept only while HCG Mail runs. The ZIP needs an
  X11 or XWayland desktop.
- No automatic updates; English only.
- Not every provider allows IMAP/SMTP with a password. Many need an app password, and some (Outlook.com, Proton Mail
  without Bridge) cannot be used in this beta.
