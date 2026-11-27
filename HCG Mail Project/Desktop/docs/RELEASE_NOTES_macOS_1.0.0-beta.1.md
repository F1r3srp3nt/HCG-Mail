# HCG Mail 1.0 Public Beta for Mac (1.0.0-beta.1)

Version **1.0.0-beta.1**, build **4**, for Macs with Apple silicon. The same app version as HCG Mail 1.0.0-beta.1 for
Windows. It supersedes the earlier macOS files of 1.0.0-beta.1 (build 3, disk images), which were not signed with a
Developer ID and should not be distributed.

## Downloads

- `HCG Mail-1.0.0-beta.1-osx-arm64.pkg`: the installer. It puts HCG Mail in `/Applications` and runs no scripts.
- `HCG Mail.app.zip`: the app itself. Open the ZIP and drag HCG Mail to Applications.

Both are signed with HCG's Developer ID (the installer with its Developer ID Installer certificate) and notarized by
Apple, with the notarization ticket stapled. macOS opens HCG Mail after its usual one-time question about an app
downloaded from the Internet. No security setting has to be changed.

## What it is

HCG Mail is a private email app for your own mailbox over IMAP and SMTP. Passwords are kept in the macOS Keychain.
Connections always use TLS with full certificate validation. What's in this version: `RELEASE_NOTES_1.0.0-beta.1.md`.

## If you used an earlier macOS build

The first time HCG Mail needs a saved password, macOS asks whether HCG Mail may use it in your Keychain. Choose
**Always Allow**. Your accounts, settings and messages are kept.

## Checksums

Every file is listed with its SHA-256 in `SHA256SUMS.txt` and the release manifest. Check a file with
`shasum -a 256 <file>`.

## Known limitations

- Apple silicon only in this release.
- The window needs a display at least 1040 points wide.
- IMAP and SMTP only. Microsoft and Google sign-in are not part of the Public Beta.
- No automatic updates; English only.
