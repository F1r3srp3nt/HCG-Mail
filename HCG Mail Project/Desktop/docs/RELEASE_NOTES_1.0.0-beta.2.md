# HCG Mail 1.0 Public Beta (1.0.0-beta.2) for Windows

Version **1.0.0-beta.2**, build **4**. A new public beta for Windows, after testing 1.0.0-beta.1 on a physical PC. It does
not replace the beta.1 files: they keep their names and checksums.

## What's new

- **Accounts: IMAP and SMTP.** The welcome screen and account setup are about adding your mailbox with IMAP and SMTP.
  Unfinished sign-in methods are not offered anywhere in the Public Beta. Outlook.com and Hotmail addresses get a clear
  note that they cannot be added in this beta.
- **One HCG Mail wordmark.** The title bar no longer draws its own "HCG MAIL" title, which could overlap the system's
  title text at some Windows versions and display scalings. The wordmark is shown once, in the sidebar, and scales
  down instead of being cut off when the sidebar is narrow.
- **The official HCG logo with a transparent background.** No dark rectangle behind the logo in the light theme. The logo
  keeps its geometry, metal and blue light.
- **A cleaner welcome screen** with the logo and one clear action: Add email account.

## Upgrading from 1.0.0-beta.1

Run the new installer: it replaces the program and keeps your accounts, settings and saved passwords (tested).
Uninstalling HCG Mail never deletes your accounts' settings.

## Known limitations

- Not code-signed: Windows SmartScreen warns on first start (*More info › Run anyway*), only if the checksum matches.
- No automatic updates; English only.
