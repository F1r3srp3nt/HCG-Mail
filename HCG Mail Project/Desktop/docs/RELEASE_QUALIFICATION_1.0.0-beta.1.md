# HCG Mail 1.0 Public Beta (1.0.0-beta.1) — release qualification report

Status: **prepared for the owner's review. Not published** (no HCG Store, HCG Repo or TestFlight submission, no tag
or release). Source: branch `release/1.0.0-beta.1`, PR #26. Every result below names its evidence. "Not run" means
what it says, not "assumed to work".

## Summary by platform and evidence level

| Evidence level | Windows x64 | Windows ARM64 | macOS arm64 | macOS x64 | Linux x64 | Linux arm64 | iOS / iPadOS |
|---|---|---|---|---|---|---|---|
| Source compilation | @WIN | @WIN | @MAC | @MAC | @LIN | @LIN | @IOS_BUILD |
| Automated tests | @TESTS | @TESTS | @TESTS | @TESTS | @TESTS | @TESTS | @IOS_TESTS |
| Native application launch | @WIN_X64_RT | @WIN_ARM_RT | @MAC_ARM_RT | @MAC_X64_RT | @LIN_X64_RT | @LIN_ARM_RT | @IOS_SIM |
| Genuine IMAP authentication (real provider) | not run¹ | not run¹ | not run¹ | not run¹ | not run¹ | not run¹ | not run¹ |
| Genuine SMTP delivery (real provider) | not run¹ | not run¹ | not run¹ | not run¹ | not run¹ | not run¹ | not run¹ |
| IMAP/SMTP against a real local server (TLS) | not run | not run | not run | not run | 25/25 passed² | not run | not run |
| Physical-device validation | not run | not run | not run | not run | not run | not run | not run³ |
| Signing / notarization | unsigned | unsigned | ad-hoc only, not notarized | ad-hoc only, not notarized | not signed (n/a) | not signed (n/a) | not signed⁴ |
| Distribution readiness | @WIN_DIST | @WIN_DIST | @MAC_DIST | @MAC_DIST | @LIN_DIST | @LIN_DIST | not ready⁴ |

¹ The dedicated-mailbox job (`windows-prerelease.yml` › mailbox) is ready. It runs on x64 and ARM64 with a connection
test, a wrong-password test, a send-to-itself and receive, and a credential-store restart test. It needs the
repository secrets `HCG_TEST_MAILBOX_*` (`docs/DEDICATED_TEST_MAILBOX.md`), which are not configured, so it reports
"not run".
² The disposable Dovecot/Postfix server, using the app's own mail engine with full TLS certificate validation:
`tests/HCG.Mail.Tests/Integration`. This was run in the Linux x64 development environment (Ubuntu 24.04). The mail
engine is the same code on every platform, but this suite has not run on the other platforms. The run is listed below.
³ iOS standard edition: no signed device build, because no Apple certificate or provisioning profile is in this
environment. HCG Mail Unleashed (jailbreak, via HCG Repo) is a separate track. Its physical-device launch failure
from 0.1.0~beta1 is still under investigation and is not part of this release.
⁴ App Store / TestFlight need the owner's Apple Distribution identity, and are never uploaded automatically.

## Artifacts

@ARTIFACTS

## Automated tests

@TEST_DETAILS

## IMAP and SMTP qualification (local server)

The server is the disposable Dovecot/Postfix server from `tests/integration-server/setup-local-mail-server.sh`: real
IMAP and SMTP over TLS 1.3, with the server certificate validated (a certificate for another name is refused). Run
on 2026-11-27 with `HCG_MAIL_E2E=1`. **25 passed, 0 failed.** The 4 skipped tests are the dedicated-mailbox tests,
which need the secrets.

| Area | Scenario(s) | Result |
|---|---|---|
| Account authentication | connection test (SSL/TLS, STARTTLS), wrong password (IMAP and SMTP), wrong password recorded as not sent | passed |
| TLS and certificates | certificate for another name refused, security mode not matching the port fails safely | passed |
| Initial and incremental sync, new mail | `Incremental_sync_shows_a_new_incoming_message_and_updates_unread_counts` | passed |
| Send, reply, reply all, forward | send/receive with attachment and Sent copy without duplicates; reply-all threading and \Answered; forward with attachment | passed |
| Drafts | stored on the server, replaced on re-save, removed on send | passed |
| Read/unread, flag | persisted on the server across a restart | passed |
| Archive, Trash, Junk, custom folders, moving | flags/trash/delete/archive; Junk → custom folder → Inbox | passed |
| Multiple accounts | two accounts sending to each other at the same time, no state mixed | passed |
| Connection loss and recovery | the session reconnects after the server drops the connection | passed |
| Offline | an unreachable server fails quickly with a transient (offline) error | passed |
| Large mailboxes | 1,500 messages: consistent pages, newest first, server search reaches the oldest | passed |
| Attachments | send, receive, byte-identical download; forward | passed |
| Search | server search beyond the loaded page | passed |
| Rejections | rejected recipient and rejected send reported with the server reply | passed |

## Rendering qualification

These 18 constructs are regression-tested in Original and Readable view and in plain-text extraction
(`TransactionalContentRenderingTests`). 13 of them lost the verification code before this release:
- form controls;
- `background:` shorthand on a cell or in a style rule, and gradients;
- a link styled as a button, and a gradient link button;
- a remote `background=` attribute;
- white text without a background;
- flex digit boxes, inline and from a style rule.

The other 13 fixture messages render in 2 skins × 3 views. The original NOR-SHIPPING message was not available, so its
specific cause is unconfirmed (see `MESSAGE_RENDERING.md`).

## Privacy and security review

- **Passwords:** Windows Credential Manager and the macOS Keychain; on Linux kept only while the app runs, and the app
  says so. Never in a file (tested).
- **TLS:** always required, with certificate and revocation validation. Never weakened to get a connection working.
- **Email content runs nothing:**
  - there are no scripts, forms, handlers or frames;
  - form controls become inert text;
  - remote images are blocked by default;
  - remote CSS, fonts and backgrounds are never loaded.
- **No transmission elsewhere:** no telemetry, and mail is not sent to HCG, AI services or anyone other than the
  user's own servers.
- **Diagnostics and logs:** redacted. No passwords or tokens, and addresses keep only their domain (tested).
- **Screenshots and fixtures:** only invented data (`[Sample]`, example.com, `482913`). No real addresses or codes.
- **Microsoft and Google sign-in:** hidden in the Public Beta by the release capability flag (`RELEASE_CAPABILITIES.md`).

## Remaining release blockers and open items

@BLOCKERS

## Screenshots

In `docs/images/1.0.0-beta.1/`, from the sanitized demonstration mailbox: inbox, reading (HCG Dark and HCG Light),
compose, settings, Mail Health › Connection, account setup (Gmail preset), and a verification-code rendering.
