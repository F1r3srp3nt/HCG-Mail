# HCG Mail — Security

Email carries private and sensitive information. These are the controls implemented in the MVP, how
they are enforced in code, and the known limitations.

## Threat model (summary)

| Threat | Mitigation |
|---|---|
| Network eavesdropping / credential theft | TLS-only connections; STARTTLS is *required* when chosen; no plaintext or "TLS when available" modes exist |
| Man-in-the-middle with a forged certificate | Strict certificate validation (no policy errors accepted), revocation checking on, hostname must match |
| Protocol downgrade | `SecurityOptions.ToSecureSocketOptions` only returns `SslOnConnect` or `StartTls`; a missing STARTTLS fails the connection; `IsSecure` is checked before credentials are sent |
| Passwords on disk | Stored only in Windows Credential Manager / macOS Keychain; never in `accounts.json`, logs or exceptions |
| Malicious HTML (scripts, tracking, phishing links) | Allow-list sanitizer + script-less renderer + intercepted loads + link confirmation |
| Tracking pixels / remote content | Blocked by default; user opts in per message ("Load images"); even then remote CSS/fonts/backgrounds stay blocked |
| Malicious attachments | Never opened or executed; file names sanitized; dangerous types flagged and confirmed before saving |
| Path traversal via attachment names | Last path segment only, invalid/control/bidi characters removed, reserved Windows names prefixed, final path verified to be inside the download folder |
| Header injection | CR/LF rejected in display name, username, password; subjects are flattened to one line |
| Telemetry / data leaving the device | No telemetry, analytics or HCG endpoints. Avalonia *build* telemetry is disabled in `Directory.Build.targets` |

## Transport security

* `MailClientFactory` configures every MailKit client with:
  * `ServerCertificateValidationCallback` → `TlsValidationRecorder.Validate`, which returns **true only
    when `SslPolicyErrors == None`** (identical strictness to the .NET default) and records why a
    certificate was rejected so the UI can explain it;
  * `CheckCertificateRevocation = true` (hard-fail: an unreachable CRL/OCSP responder fails the
    connection rather than being ignored);
  * TLS 1.2 + 1.3 only (TLS 1.3 is not requested on Windows 10 builds whose TLS stack lacks client
    support); TLS 1.0/1.1 are never offered;
  * 45-second network timeouts.
* MailKit's protocol logger is **never enabled** — IMAP/SMTP transcripts include authentication data.
* The connection test never sends a message; it signs in to SMTP and disconnects.

## Credential storage

`ICredentialVault` abstracts the OS store:

| Platform | Implementation | Notes |
|---|---|---|
| Windows 10/11 | `WindowsCredentialVault` — Credential Manager generic credentials (`CredWriteW/CredReadW/CredDeleteW`), target `HCG Mail/account:<id>`, persisted per user on this machine (not roamed) | Unmanaged buffers are zeroed after use. Verified with `--check-credential-store` under Wine; run it once on real Windows |
| macOS | `MacKeychainCredentialVault` — Security.framework generic-password items, service `HCG Mail` | **Not yet executed on a Mac** — run `--check-credential-store` |
| Other (Linux dev) | `SessionCredentialVault` — memory only | `IsPersistent = false`; UI states the password must be re-entered after restart |

If the OS store fails, the error is shown and nothing is written insecurely. Removing an account deletes
its stored password. Passwords are cleared from the account form after saving.

## Untrusted content

### HTML
1. **Sanitizer** (`MailHtmlSanitizer`, based on HtmlSanitizer/AngleSharp): explicit allow-list of
   formatting tags/attributes; removes `script`, `iframe`, `object`, `embed`, `form`/inputs, `svg`,
   `math`, `meta`, `base`, `link`, event-handler attributes, `javascript:`/`vbscript:`/`data:text`
   links; drops the *content* of script-like elements; only `@media` at-rules survive (no `@import`,
   no `@font-face`); remote `url()` in styles is removed; links are limited to `http(s)`, `mailto`, `#`.
2. **Renderer**: HtmlRenderer is a managed layout engine with **no JavaScript engine**.
3. **Load interception** (`MessageBodyView`): images only from `cid:` parts or `data:image` (no SVG,
   ≤ 10 MB); remote images only after the user's click, via `RemoteImageLoader` (no cookies, image
   content types only, ≤ 5 MB, 12 s timeout); external stylesheets never.
4. **Links**: clicks are intercepted; a dialog shows the real host and full URL before the system
   browser is opened (can be turned off in Settings → Links). The renderer's own context menu is
   disabled because it could open links without that confirmation.
5. A *Show plain text* toggle renders only the text version.

### Attachments
Saved only on explicit request, written to `<name>.hcgpart` then renamed, never opened. Executable or
macro-capable types (`.exe .js .vbs .ps1 .lnk .docm …`) show a warning and require confirmation.

## Data at rest

* `accounts.json` and `settings.json` contain no secrets; on macOS/Linux they are created `0600` in a
  `0700` folder (Windows relies on the per-user profile ACLs).
* Message bodies and attachments are **not cached on disk**; parsed messages live in memory only (LRU
  of 6).
* Logs contain operation names, hosts, account ids, counts and error types — never passwords,
  subjects, bodies or recipient lists. Retention: 7 days.

### Future encrypted cache — key management (not implemented)
If an offline cache is added it must be encrypted: a random 256-bit data key per installation, wrapped
by the OS (Windows DPAPI `CurrentUser` / macOS Keychain item), AES-GCM per record, cache deletable from
Settings, and headers-only by default. See ROADMAP.

## Known limitations

* Not code-signed (Windows SmartScreen warns; macOS Gatekeeper requires right-click → Open). Signing
  requires certificates and founder approval.
* No S/MIME or PGP; no OAuth2 (password authentication only — Domene.no uses passwords).
* No phishing heuristics beyond showing the real link target and sender address.
* Without UIDPLUS (rare), permanent delete uses `EXPUNGE`, which also removes other messages already
  marked `\Deleted` in that folder.
* HTML rendering uses a CSS2-level engine: complex newsletters may look simpler than in a browser
  (a deliberate trade-off for not embedding a browser engine).

## Reporting

Security issues should be reported privately to the HCG Mail maintainers (founder), not in public
issues.
