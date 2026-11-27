# HCG Mail 1.0 Public Beta for macOS (1.0.0-beta.2)

Version **1.0.0-beta.2**, build **4**, for macOS only. It supersedes the macOS files of 1.0.0-beta.1, which were not
signed with a Developer ID and should not be distributed. Windows and Linux stay at 1.0.0-beta.1.

## What's new

- **Signed and notarized by Apple.**
  - HCG Mail is signed with HCG's Developer ID, and the installer with its Developer ID Installer certificate.
  - Apple's notary service has checked every file, and the result is stapled to it.
  - macOS opens HCG Mail after the usual one-time question about an app downloaded from the Internet. There is no "damaged" or "unidentified developer" message.
- **Three ways to install:**
  - an installer package (`.pkg`) for `/Applications`;
  - a ZIP with the app;
  - a disk image (`.dmg`).

  Each comes for Apple silicon and for Intel Macs.
- **Hardened runtime.** HCG Mail runs with the protections Apple requires for notarized apps, with a single exception: the .NET runtime may compile code just in time.
- The app itself is unchanged from 1.0.0-beta.1. See `RELEASE_NOTES_1.0.0-beta.1.md`.

## If you used an earlier macOS build

The first time HCG Mail needs a saved password, macOS asks whether HCG Mail may use it in your Keychain. Choose
**Always Allow**. Your accounts, settings and messages are kept.

## Checksums

Every file is listed with its SHA-256 in `SHA256SUMS.txt` and the release manifest. Check a file with
`shasum -a 256 <file>`.

## Known limitations

- The window needs a display at least 1040 points wide.
- IMAP and SMTP only. Microsoft and Google sign-in are not part of the Public Beta.
- No automatic updates; English only.
