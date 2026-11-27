# HCG Mail — Roadmap

## Delivered in MVP 0.1.0

Phases A–D (foundation, IMAP client, SMTP & composition, usability & security) are implemented and
covered by automated tests, including end-to-end tests against a real IMAP/SMTP server. Phase E
(Windows) is prepared: the Windows build exists and was smoke-tested under Wine; testing on real
Windows hardware is pending.

## Next — finish Phase E (needs the founder's hardware)

- [ ] Run [WINDOWS_BUILD.md](WINDOWS_BUILD.md) checks W1–W8 on Windows 11 and Windows 10.
- [ ] Run the [Domene.no checklist](MANUAL_TEST_CHECKLIST.md) on Windows and on the Mac.
- [ ] Run `--check-credential-store` on macOS (Keychain implementation not yet executed on a Mac).
- [ ] Fix any Windows-specific issues found (title bar, DPI, GPU rendering).
- [x] Application icon supplied and integrated.
- [x] Official HCG logo supplied and shown in the sidebar header.

## Short term

- Multi-select in the message list (bulk read/flag/delete/move).
- *Move to folder…* and drag-and-drop to folders; folder create/rename/delete.
- Server-side search (IMAP `SEARCH`) in addition to searching loaded messages.
- Optional IMAP IDLE for instant new-mail notification — only as an explicit, visible opt-in (HCG Mail currently has no background synchronisation of any kind).
- Windows/macOS system notifications (toast / Notification Center).
- HTML (rich-text) composer and signatures; quote HTML originals in replies.
- Attachment preview for images/PDF inside a sandboxed viewer (never executing content).
- "Always load images from this sender" allow-list.
- Separate SMTP username/password and multiple identities/aliases.
- Startup options: launch at login, start minimised to tray.

## Medium term

- **Optional encrypted local cache** (SQLite) for message headers → instant startup and offline
  browsing. Key management: per-installation random 256-bit key, wrapped by Windows DPAPI
  (CurrentUser) or the macOS Keychain; AES-GCM per record; headers-only by default; bodies only if the
  user opts in; "Clear local cache" in Settings; cache excluded from cloud-synced folders.
- Conversation (thread) view using `References` / `In-Reply-To`.
- Accessibility pass (screen-reader names, keyboard focus order, high-contrast skin).
- Localisation: Norwegian (nb-NO) UI.
- Code signing + MSIX/Inno Setup installer + opt-in update channel (requires approval and certificate
  purchase).
- OAuth2 (XOAUTH2) for providers that require it (Microsoft 365, Gmail) — not needed for Domene.no.
- S/MIME signing/encryption.

## HCG ecosystem (only with explicit founder approval)

Future integrations (HCG Connect, HCG ERP, HCG AI Centre) would plug in through the
`IHcgIntegration` extension point. Principles: opt-in per user, no credentials or message content
shared unless the user explicitly invokes an action, no changes to central HCG security, identity or
permission systems without approval, and HCG Mail must keep working fully without them.
