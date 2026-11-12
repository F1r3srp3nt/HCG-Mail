# HCG Mail — Testing

## Automated suites

| Suite | Command | Needs | Count |
|---|---|---|---|
| Unit tests (`tests/HCG.Mail.Tests`) | `dotnet test tests/HCG.Mail.Tests` | nothing | 398 |
| Headless UI tests (`tests/HCG.Mail.App.Tests`) | `dotnet test tests/HCG.Mail.App.Tests` | nothing (Skia renders off-screen) | 70 |
| End-to-end tests (opt-in) | `HCG_MAIL_E2E=1 dotnet test` | local test server (`tests/integration-server`) | 18 + 2 UI |

Plain `dotnet test` runs everything except the end-to-end tests, which report as *skipped*. They never
run against a real mailbox.

### Coverage by requirement

| Requirement | Where |
|---|---|
| Account configuration validation | `Core/AccountValidatorTests`, `AccountSetupViewModelTests` |
| IMAP folder mapping | `Core/FolderRoleMapperTests` (special-use, Norwegian names, `INBOX.` namespaces, Gmail containers, uniqueness, ordering) |
| MIME parsing | `Email/MimeConverterTests` (alternative bodies, inline `cid:` images, attachments, attached messages, charsets/encoded words, building outgoing messages, draft round-trip) |
| Email address validation | `Core/EmailAddressTests` (IDN, quoted names, separators, duplicates, invalid tokens, `mailto:` links) |
| Reply / Reply all recipients | `Core/ReplyBuilderTests` (Reply-To, self exclusion, de-duplication, replying from Sent, subject prefixes incl. SV/VS, References chain) |
| Attachment file names | `Core/AttachmentFileNameTests` (traversal, reserved names, bidi override, trailing dots, length, dangerous types, unique paths) |
| Theme persistence | `Infrastructure/StorageTests`, `MainWindowTests.Skin_switch_is_live_persisted_and_keeps_connections`, `SettingsTests` |
| Secure storage abstraction | `Infrastructure/CredentialVaultTests` (session vault, platform factory, real OS vault round-trip on Windows/macOS) |
| Error handling | `Email/ErrorTranslatorTests`, `Email/ConnectionTesterTests` (mocked DNS/TCP/TLS/auth failures) |
| HTML safety | `Email/HtmlSanitizerTests` (scripts, handlers, frames, forms, SVG, `javascript:`, remote images/CSS, `@import`, renderer colour compatibility) |
| Certificate revocation | `Email/CertificateValidationTests` (valid, expired, hostname mismatch, untrusted CA, revoked, unreachable CRL, CRL server error, combined problems, handshake timeout — real .NET chain building with a local CRL server) and `Email/CrlFallbackTests` (macOS CRL fallback: good/revoked CRLs, forged signer with the issuer's name, tampered, wrong issuer, issuer without `cRLSign`, stale/future/no `nextUpdate`, other partition, `onlySomeReasons`, delta, unknown critical extension, malformed/truncated input, caching, size limit, non-HTTP addresses, and that name/trust/date failures never reach the fallback) |
| Deliverability, priority, delivery states | `Email/SendingTests` (exact priority headers once each, MIME/7-bit/CRLF/Bcc, Message-ID uniqueness, EHLO address literal captured on a socket, SMTP reply → delivered/deferred/rejected/unknown, history file), `Email/DeliverabilityTests` (SPF parser/evaluator incl. 10-lookup limit, DMARC/DKIM records, DNS checks, received-message analysis incl. real DKIM signing and tamper detection, format validator, SMTP history), `MailHealthUiTests`, E2E priority/EHLO/delivery-state tests — see [DELIVERABILITY.md](DELIVERABILITY.md) |
| Message rendering | `Email/RenderingPipelineTests` and `ReaderAndRenderingTests` with the synthetic fixtures in `tests/fixtures/mail` (nested MIME, CID images, malformed HTML, Unicode, light/dark layouts, blocked images, empty HTML alternative, empty message, hostile content): body selection, sanitizer policy, Readable view, previews, every fixture × skin × mode visibly rendered, rapid switching, retry and render-failure fallbacks — see [MESSAGE_RENDERING.md](MESSAGE_RENDERING.md) |
| Microsoft accounts | `Email/GraphMailSessionTests` (recording fake of Graph: folders, paging, MIME bodies/attachments, flags, move/delete, send once without retry, throttling, unknown send outcome, drafts, token refresh and revoked consent, unsupported mailbox, account isolation, token never sent to other hosts, search), `Infrastructure/MicrosoftAuthTests` (MSAL error mapping, scopes, client ID configuration, account persistence), `MicrosoftAccountTests` (add, cancel, reconnect as another user refused, duplicate, unsupported mailbox, unconfigured build, reconnect state, removal, settings) — no real Microsoft account is contacted; see [MICROSOFT_ACCOUNTS.md](MICROSOFT_ACCOUNTS.md) |
| Security policy | `SecurityOptionsTests` (never plaintext/auto modes, strict certificate callback, TLS ≥ 1.2) |
| Real-account mode | `RealAccountModeTests` (normal start has no preview/sample data, existing `accounts.json`/`settings.json` loaded and left untouched, preview cannot be enabled implicitly, no periodic timer, refresh is single-flight, cancellable and reports success, official logo embedded with original proportions) |
| UI behaviour | `MainWindowTests` (load, read + mark read, sanitized HTML + remote images, search, delete/flag/read, skins), `ComposeViewModelTests`, `AccountSetupViewModelTests` |

### End-to-end tests (real Dovecot + Postfix, disposable)

`tests/HCG.Mail.Tests/Integration/LocalMailServerTests.cs` and
`tests/HCG.Mail.App.Tests/RealServerUiTests.cs` cover, against a real server with a trusted test CA
and a live CRL:
connection test with SSL/TLS and STARTTLS; wrong password; unknown host; certificate name mismatch;
security/port mismatch; send → receive → read → attachment bytes → single Sent copy; reply-all
threading and `\Answered`; flags; Trash and permanent delete; Archive; unread counts; server drafts
(save, replace, load, send removes draft); reconnection after the server kills the session
(`doveadm kick`); SMTP recipient rejection reported as a failed send; and the full application UI
loading the inbox, showing newly arrived mail only after a manual Refresh, and rendering an HTML message with an inline image and a blocked tracking pixel;
and the full application recovering from a simulated **network interruption** (IMAP traffic routed
through a relay that the test cuts and restores): error state while offline, list kept visible,
automatic recovery on refresh using the new connection. That test caught — and now guards against — a
bug where the message list kept using the closed connection after a reconnect.

## Last recorded run (development container, Ubuntu 24.04, .NET SDK 10.0.114, 2026-11-12)

```
dotnet build -c Release            → 0 warnings, 0 errors
HCG_MAIL_E2E=1 dotnet test
  HCG.Mail.Tests       Passed 416 / Failed 0   (398 unit + 18 end-to-end)
  HCG.Mail.App.Tests   Passed  72 / Failed 0   (70 UI + 2 end-to-end UI)
```

Additional manual verification performed in the container:

* App launched under X11 (Xvfb): preview mode, first run (welcome), saved account without stored
  password ("Password required" state).
* Windows `win-x64` single-file publish built; under Wine 9 the folder publish passed
  `--check-credential-store` (Windows Credential Manager) and rendered the main window with
  `--software-rendering`.
* No outbound connections from builds or the app other than to the configured mail servers
  (verified via the sandbox proxy log after disabling Avalonia build telemetry).

**Not yet tested:** macOS (Keychain), real Windows 10/11 hardware, a real Domene.no mailbox — see
[MANUAL_TEST_CHECKLIST.md](MANUAL_TEST_CHECKLIST.md) and [WINDOWS_BUILD.md](WINDOWS_BUILD.md).

## Screenshots

Set `HCG_MAIL_SCREENSHOT_DIR=/some/folder` when running the UI tests to save PNGs of the main window
(both skins), compose, account setup and settings.
