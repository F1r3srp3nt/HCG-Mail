# Authenticated IMAP/SMTP testing with a dedicated test mailbox

Before a Windows prerelease is made public, HCG Mail signs in to a real mailbox with IMAP and SMTP on both
architectures. The workflow `.github/workflows/windows-prerelease.yml`, job **mailbox**, does this on a GitHub-hosted
x64 runner and an ARM64 runner. It uses a **dedicated test mailbox**: one created only for this purpose. Never use a
personal, company or customer mailbox.

## What the test does

The tests are in `tests/HCG.Mail.Tests/Integration/DedicatedMailboxTests.cs`. They use the app's own mail, TLS and
credential-store code from the same source commit as the packaged app, in a Release build.

1. **Connection test:** DNS, TCP, TLS with normal certificate validation, IMAP sign-in, folder access and SMTP sign-in.
   All six steps must pass.
2. **Wrong password:** one IMAP and one SMTP sign-in attempt with a wrong password. Both must be refused, with TLS
   still passing. There is only one attempt per protocol so that the mailbox is not locked.
3. **Send and receive:** sends one plain-text message from the mailbox to itself over SMTP. The subject is
   `HCG Mail Windows test hcg-e2e-<random>`. The test waits up to 2 minutes for the message over IMAP, then reads it and
   checks the content and sender. Afterwards it deletes the message permanently, along with its copy in Sent.
4. **Credential Manager and restart:** stores the password in Windows Credential Manager and the account in a temporary
   data folder. New store instances then read both back (a simulated restart), and the test signs in with what was
   read. It also checks that the password is not in the account file. The Credential Manager entry and the folder are
   deleted afterwards.

Per run that is at most 2 messages sent to the mailbox itself (one per architecture) and 4 failed sign-ins (IMAP and
SMTP, per architecture). The two architectures run one after the other.

The result goes into `mailbox-e2e.json` (artifact `hcg-mail-windows-mailbox-<arch>`) and from there into the Store
draft and the Release Passports:

| Result | Release Passport `runtime_verification` |
|---|---|
| All 4 tests passed, none skipped | `passed` |
| No mailbox configured | `partial`, with "not run (no dedicated test mailbox configured)" |
| Any test failed or skipped | `failed`; the job fails |

## Setting it up (owner)

1. Create a new mailbox used only for testing, for example `hcg-mail-test@<your domain>`, at a provider with IMAP and
   SMTP. If the provider requires it, create an **app password** for it.
2. In GitHub, open **F1r3srp3nt/HCG-Mail › Settings › Secrets and variables › Actions › New repository secret** and add:

   | Secret | Value | Required |
   |---|---|---|
   | `HCG_TEST_MAILBOX_ADDRESS` | the test mailbox's email address | yes |
   | `HCG_TEST_MAILBOX_PASSWORD` | its password or app password | yes |
   | `HCG_TEST_MAILBOX_IMAP_HOST` | IMAP server host name | yes |
   | `HCG_TEST_MAILBOX_SMTP_HOST` | SMTP server host name | yes |
   | `HCG_TEST_MAILBOX_USERNAME` | sign-in name, if it is not the address | no |
   | `HCG_TEST_MAILBOX_IMAP_PORT` | default 993 (SslTls) or 143 (StartTls) | no |
   | `HCG_TEST_MAILBOX_IMAP_SECURITY` | `SslTls` (default) or `StartTls` | no |
   | `HCG_TEST_MAILBOX_SMTP_PORT` | default 465 (SslTls) or 587 (StartTls) | no |
   | `HCG_TEST_MAILBOX_SMTP_SECURITY` | `SslTls` (default) or `StartTls` | no |

3. Run **Actions › Windows prerelease › Run workflow** on the branch to be tested, or push to it.

The secrets go only into the **mailbox** job's environment. GitHub masks them in logs, and the tests never print the
password. They are not available to workflows from forks.

## Running it locally

On a Windows PC, set the same variables in a PowerShell window, then run:

```powershell
dotnet test tests/HCG.Mail.Tests -c Release --filter "FullyQualifiedName~DedicatedMailboxTests"
```

Without the variables, the four tests report as skipped.
