# HCG Mail for Windows 0.1.0-beta.2 (public testing prerelease)

Version **0.1.0-beta.2**, build **2**. Free during public testing, temporarily: a prerelease for testing and feedback,
not a final release.

beta.2 replaces nothing: 0.1.0-beta.1 (build 1) stays available with its own files and checksums. beta.2 is a new
build with new files and new checksums.

## Changes since 0.1.0-beta.1

- **Window title:** the title is shown once. Windows' drawn title bar no longer repeats "HCG Mail" over the app's own
  header; the minimise, maximise and close buttons are unchanged.
- **Welcome card:** the text and the sign-in button labels wrap inside the card instead of being cut off, and the card
  scrolls when the window is short. The smallest window width is now 1040 pixels (was 1000), so the three panes always
  fit.
- **Microsoft and Google sign-in:** the buttons are hidden when that sign-in is not set up in the build, as in this one.
  The welcome text then mentions only IMAP/SMTP.

## What it is

A private email app for your own mailbox over **IMAP and SMTP**: for example privately hosted domain email such as
Domene.no, or any provider that offers IMAP/SMTP with a password or app password. No HCG account is needed, and HCG
runs no server that your mail passes through.

- Read, write, reply, reply-all and forward. Attachments, drafts on the server, folders, search, and flags.
- Encrypted connections only (TLS). HCG Mail checks the server's certificate, including revocation, and refuses to
  connect when the check fails.
- Passwords are kept in **Windows Credential Manager**, never in a file.
- Mail Health shows SPF, DKIM and DMARC for messages you received.
- Remote images in messages load only when you ask.

## Downloads

| File | For |
|---|---|
| `HCGMail-0.1.0-beta.2-win-x64-Setup.exe` | Installer, Intel/AMD 64-bit PCs |
| `HCGMail-0.1.0-beta.2-win-x64.zip` | Portable (no installation), Intel/AMD 64-bit PCs |
| `HCGMail-0.1.0-beta.2-win-arm64-Setup.exe` | Installer, Windows on ARM (e.g. Snapdragon) |
| `HCGMail-0.1.0-beta.2-win-arm64.zip` | Portable, Windows on ARM |

All of them are self-contained: no .NET installation is needed. Compare the SHA-256 checksum of your download with the
one published next to it (PowerShell: `Get-FileHash .\<file>`).

## Supported Windows versions

- **x64:** Windows 10 (version 1607 or later) and Windows 11.
- **ARM64:** Windows 11.

These are the Windows versions .NET 10 supports. This build was tested on Windows build 26100 (x64) and Windows 11
build 26200 (ARM64). Other versions in these ranges are expected to work but were not tested.

## Install

**Installer:** run the Setup file. It installs for your Windows user only (no administrator rights), adds HCG Mail to the
Start menu and optionally to the desktop.

**Portable:** right-click the ZIP › Properties › tick **Unblock** if shown, extract it to a folder of your choice, and run
`HCGMail.exe`.

**SmartScreen:** these files are **not code-signed**, so Windows shows "Windows protected your PC". Choose **More info ›
Run anyway** only if the file's SHA-256 matches the published checksum.

## Remove

- **Installed:** Settings › Apps › Installed apps › **HCG Mail** › Uninstall.
- **Portable:** delete the folder.
- Your settings and log stay in `%LOCALAPPDATA%\HCG\HCG Mail`; delete that folder too if you want them gone. Saved
  passwords are under Control Panel › Credential Manager › Windows Credentials, as `HCG Mail/account:…` entries. Mail on
  your server is never affected.

## Known limitations

- **Not code-signed** (no Authenticode signature): SmartScreen warns on download and first start.
- **Microsoft (Outlook.com, Microsoft 365) and Google (Gmail) sign-in are not available in this build**, so their buttons
  are hidden. Use IMAP/SMTP with an app password where your provider allows it.
- No automatic updates: a new version is a new download, and the installer upgrades in place.
- English only.
- Prerelease: please report problems with the steps that led to them. HCG Mail's log (in the folder above) contains no
  passwords or message content.
