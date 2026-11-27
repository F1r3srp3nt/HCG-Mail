# HCG Mail 1.0 Public Beta (1.0.0-beta.1) — release qualification report

Status: **prepared for the owner's review. Not published** (no HCG Store, HCG Repo or TestFlight submission, no tag
or release). Source: branch `release/1.0.0-beta.1`, PR #26. **Release candidate: commit `0b88b94`** — every artifact below was built from it (Windows run [45935461816](https://github.com/F1r3srp3nt/HCG-Mail/actions/runs/45935461816), macOS/Linux run [45935461901](https://github.com/F1r3srp3nt/HCG-Mail/actions/runs/45935461901), iOS run [45935466911](https://github.com/F1r3srp3nt/HCG-Mail/actions/runs/45935466911)). Later commits on the branch change only documentation and do not rebuild. Every result below names its evidence. "Not run" means
what it says, not "assumed to work".

## Summary by platform and evidence level

| Evidence level | Windows x64 | Windows ARM64 | macOS arm64 | macOS x64 | Linux x64 | Linux arm64 | iOS / iPadOS |
|---|---|---|---|---|---|---|---|
| Source compilation | passed | passed | passed | passed | passed | passed | passed (simulator Debug and Release/AOT; device ios-arm64 Release/AOT, unsigned) |
| Automated tests | 619 passed, 0 failed | not run on ARM64⁵ | not run on macOS⁵ | not run on macOS⁵ | 619 passed, 0 failed | not run on arm64⁵ | 58 passed, 0 failed (mobile UI, headless); simulator self-test passed |
| Native application launch | passed: 18/18 checks, installer verified (Windows Server 2025, build 26100) | passed: 18/18 checks, installer verified (Windows 11 ARM64, build 26200) | passed: DMG installed, seal OK, 14/14 checks (macOS 15.8.2) | passed: DMG installed, seal OK, 14/14 checks (macOS 15.7.9, Intel) | passed: 13/13 checks (Ubuntu 24.04.5, Xvfb) | passed: 13/13 checks (Ubuntu 24.04.5 arm64, Xvfb) | simulator only: Debug self-test and Release (AOT) launch passed |
| Genuine IMAP authentication (real provider) | not run¹ | not run¹ | not run¹ | not run¹ | not run¹ | not run¹ | not run¹ |
| Genuine SMTP delivery (real provider) | not run¹ | not run¹ | not run¹ | not run¹ | not run¹ | not run¹ | not run¹ |
| IMAP/SMTP against a real local server (TLS) | not run | not run | not run | not run | 25/25 passed² | not run | not run |
| Physical-device validation | not run | not run | not run | not run | not run | not run | not run³ |
| Signing / notarization | unsigned | unsigned | ad-hoc only, not notarized | ad-hoc only, not notarized | not signed (n/a) | not signed (n/a) | not signed⁴ |
| Distribution readiness | candidate: installer + ZIP, Store draft and passports prepared; unsigned (SmartScreen warns) | same | candidate: DMG and passport prepared; not notarized (Gatekeeper › Open Anyway) | same | candidate: portable ZIP and passport; no secure password store | same | not ready⁴ |

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
⁵ The desktop and shared test suites ran on Windows x64 (run 45935461816) and Linux x64 (run 45935466911), with identical counts. They are the same code on every platform; the platform-specific parts (credential store, TLS validation, start-up) are covered by the native runtime checks in the row below.

## Artifacts

All files are from commit `0b88b94`, version 1.0.0-beta.1, build 3. Sizes in bytes.

| File | Platform | Arch | Size | SHA-256 | Signing |
|---|---|---|---|---|---|
| `HCGMail-1.0.0-beta.1-win-x64-Setup.exe` | Windows | x64 | 57,906,536 | `a4ffe0add9138feddbca34bbae69775470471ed2b45f1f78f07fcaff39260f9d` | unsigned |
| `HCGMail-1.0.0-beta.1-win-x64.zip` | Windows | x64 | 83,828,665 | `9a51e24e0ea296255a8c24210f63cfefbd86cb57ef57dc7305c636e2e0de2ead` | unsigned |
| `HCGMail-1.0.0-beta.1-win-arm64-Setup.exe` | Windows | ARM64 | 54,716,977 | `d3f3daa61c4c2d87faf86d0824b25a35ab6848697baad337e3c2d57dee818338` | unsigned |
| `HCGMail-1.0.0-beta.1-win-arm64.zip` | Windows | ARM64 | 81,309,724 | `23f25096019c7695e78a36046af28ada582d0f36d0fa24b94498092175263826` | unsigned |
| `HCGMail-1.0.0-beta.1-osx-arm64.dmg` | macOS | arm64 | 53,375,494 | `dfc0870384d4faee661b89c37f7f7d24df41297975de9cbbf21ac54e68f63739` | ad-hoc, sealed; no Developer ID, not notarized |
| `HCGMail-1.0.0-beta.1-osx-x64.dmg` | macOS | x64 | 55,564,996 | `5859bcaa58dff5fff70f89b41fbdeca759260b232eaf16c1d75faecd57b91c42` | ad-hoc, sealed; no Developer ID, not notarized |
| `HCGMail-1.0.0-beta.1-linux-x64.zip` | Linux | x64 | 53,961,319 | `97daff3b56bde8ab0c4dda38c2b79be5acc7ba44e6f0bbad6497ddea424c6a9c` | not signed (n/a) |
| `HCGMail-1.0.0-beta.1-linux-arm64.zip` | Linux | arm64 | 51,470,504 | `95e55e843e059d5ddd24c236bec286dbba208f15062bd3cfff7fdba280e2cdb7` | not signed (n/a) |

Each file has an HCG Release Passport (v1 schema, validated) and the release manifest records the validation results
and known limitations: `hcg-mail-windows-store-draft` (run 45935461816) and `hcg-mail-desktop-release-manifest` (run 45935461901).

**macOS bundle seal.** HCG Executable Maker (pinned `595e989`) leaves the app bundle unsealed: `codesign --verify
--deep --strict` reports "code has no resources but signature indicates they must be present", which makes macOS call a
downloaded copy damaged. The workflow therefore signs every file in `Contents/MacOS` and then the bundle (ad-hoc),
verifies strictly and rebuilds the DMG with Maker's layout. Maker's own DMGs, recorded in `seal.log`, were
`e0f3cee6…fa02` (arm64) and `a5c30f08…5f4b` (x64). On the test Macs the installed copy verified (`valid on disk`,
`satisfies its Designated Requirement`) and Gatekeeper answered a plain `rejected`, the expected result for an app that
is not notarized: users allow it once under System Settings › Privacy & Security › Open Anyway.

iOS produces no distributable file in this release: the device build is compiled and checked but unsigned.

## Automated tests

| Suite | Where | Passed | Failed | Skipped |
|---|---|---|---|---|
| HCG.Mail.Tests (engine, sanitizer, rendering, SMTP, onboarding, signatures) | Windows x64 (run 45935461816); Linux x64 (run 45935466911) | 460 | 0 | 29 |
| HCG.Mail.App.Tests (desktop UI, headless: rendering, compose, Mail Health, settings, mailbox) | Windows x64; Linux x64 | 159 | 0 | 2 |
| HCG.Mail.Mobile.Tests (iPhone and iPad layouts, flows, rendering) | Linux x64 (run 45935466911) | 58 | 0 | 2 |
| Local mail server E2E (`HCG_MAIL_E2E=1`) | Linux x64 development environment | 25 | 0 | 4 |
| Native runtime checks | each of the 6 desktop runners | 18/18, 18/18, 14/14, 14/14, 13/13, 13/13 | 0 | — |
| iOS simulator self-test (Keychain, sandbox, sanitizer, configuration) | iPhone simulator, Xcode 27 | passed | — | — |

The 29 skipped engine tests are the local-server and dedicated-mailbox tests, which need `HCG_MAIL_E2E=1` or the
mailbox secrets; the local-server ones are the E2E row. The mailbox job ran on x64 and ARM64 and reported "not
configured" (no `HCG_TEST_MAILBOX_*` secrets), so it ran no test.

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

Release blockers (must be resolved, or accepted by the owner, before publication):
1. **Genuine provider IMAP/SMTP not run.** The dedicated test mailbox secrets `HCG_TEST_MAILBOX_*` are not configured
   (`DEDICATED_TEST_MAILBOX.md`). Until then only the local TLS server proves sign-in, sync and delivery.
2. **No code signing.** Windows Authenticode, Apple Developer ID and notarization, and the iOS distribution identity
   are the owner's. The prerelease is usable unsigned (SmartScreen, Gatekeeper › Open Anyway), and says so.
3. **No physical-device validation.** All launches were on GitHub-hosted runners and the iOS simulator.
4. **HCG Store publishing server not deployed.** The Store draft body is prepared but not sent.

Open items (known limitations, documented in the release notes):
- **Executable Maker defect:** macOS bundles are not sealed (above). The workflow works around it; a fix in Maker needs
  the owner's authorization.
- **Linux:** no secure password store yet (passwords kept only while the app runs); portable ZIP only.
- **Window size:** the desktop window needs a display at least 1040 points wide.
- **Not done in this release (P1):** a system-appearance theme option, a unified inbox, sender avatars and sync progress.
- **iOS:** App Store screenshot job on run 45935466911 was cancelled by the runner while the iPad simulator booted (619 s);
  re-run requested. HCG Mail Unleashed's physical-device launch failure is a separate track awaiting a crash report.

## Screenshots

In `docs/images/1.0.0-beta.1/`, from the sanitized demonstration mailbox: inbox, reading (HCG Dark and HCG Light),
compose, settings, Mail Health › Connection, account setup (Gmail preset), and a verification-code rendering.
