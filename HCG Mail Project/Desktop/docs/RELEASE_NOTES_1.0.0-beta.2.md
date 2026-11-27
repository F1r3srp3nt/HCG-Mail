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
- **Works better on school and other restricted Wi-Fi.**
  - *Network Doctor* in Mail Health finds out why mail does not work on the current network and says whether it is the
    network (closed mail ports, a Wi-Fi sign-in page, certificate interception), your password, the mail server or HCG Mail.
  - When a network blocks your outgoing port, it offers another port your provider documents, if that one works here.
  - HCG Mail reconnects by itself after the network drops, changes or the computer wakes up (5 seconds, then longer
    pauses up to 5 minutes), and tells you what it is doing instead of silently stopping. A refused password is never
    retried automatically.
  - Mail you have read stays readable offline. Messages and drafts written offline wait in the new Outbox and are sent
    when the connection returns; a message that may already have been sent is never sent twice without you.
  - Certificate checks and encryption are never relaxed on any network. See `docs/NETWORK_RECOVERY.md`.

## Upgrading from 1.0.0-beta.1

Run the new installer: it replaces the program and keeps your accounts, settings and saved passwords (tested).
Uninstalling HCG Mail never deletes your accounts' settings.

## Privacy

New in this version, HCG Mail keeps a copy of the mail you read (not attachments) for offline reading, and keeps unsent
messages in the Outbox. Both are encrypted on this PC with a separate key per account, kept only in Windows Credential
Manager, and never uploaded to HCG or anyone else. In Settings › Storage you choose how long copies are kept (7 days to a
year, 30 days by default) and how much space each account may use, see the space used, and delete one account's copy or
all of them. Removing an account deletes its copy, its Outbox and their keys from this PC; backups of the PC made earlier
can still contain older encrypted copies and keys. Details: `docs/OFFLINE_DATA_SECURITY.md`.

The Network Doctor contacts a few public hosts only while you run it; they are listed in the window. Choose Google, Apple
or Microsoft for these checks, or turn them off (only your own mail servers are then contacted), in Settings › Privacy &
security. Its findings say whether a cause is confirmed, likely or undetermined.

## Known limitations

- Not code-signed: Windows SmartScreen warns on first start (*More info › Run anyway*), only if the checksum matches.
- No automatic updates; English only.
- Display scaling was tested at 100 %, 125 %, 150 % and 175 % in both themes. **200 % has not been tested**: the test
  machines' displays do not offer it. Please report anything that looks wrong at 200 %.
