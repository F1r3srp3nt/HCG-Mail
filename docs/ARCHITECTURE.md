# HCG Mail — Architecture

## Goals that shaped the design

1. **Real email, no intermediaries** — talk IMAP/SMTP directly to the user's provider (e.g. Domene.no).
   No HCG backend, no Microsoft services, no licensing or account servers.
2. **Security first** — encrypted connections only, strict certificate validation, OS credential
   storage, no plaintext secrets, no message bodies on disk, untrusted HTML neutralised.
3. **Windows-first, built on a Mac** — one cross-platform codebase (C#/.NET 10 + Avalonia) that runs on
   macOS for development and publishes a self-contained Windows x64 build.
4. **Testable** — logic lives behind interfaces so it can be unit-tested without a mailbox, and an
   opt-in end-to-end suite runs against a disposable local mail server.

## Projects and dependencies

```
HCG.Mail.App  ──►  HCG.Mail.Email  ──►  HCG.Mail.Core  ◄──  HCG.Mail.Infrastructure
   (Avalonia)        (MailKit,           (no packages)        (OS vaults, JSON
    MVVM, DI)         MimeKit,                                 stores, logging)
                      HtmlSanitizer)
```

| Project | Responsibility | Key types |
|---|---|---|
| `HCG.Mail.Core` | Domain model and rules, free of UI/network/OS code | `MailAccount`, `AccountValidator`, `ProviderPresets`, `EmailAddressValidator`, `RecipientParser`, `FolderRoleMapper`, `ReplyBuilder`, `AttachmentFileNames`, contracts `IMailSession`, `IConnectionTester`, `ICredentialVault`, `IHtmlSanitizer` |
| `HCG.Mail.Email` | MailKit/MimeKit implementation | `MailKitMailSession`, `ConnectionTester`, `MailClientFactory`, `TlsValidationRecorder`, `MailErrorTranslator`, `MimeConverter`, `MailHtmlSanitizer`, `HtmlToTextConverter` |
| `HCG.Mail.Infrastructure` | Platform integration | `WindowsCredentialVault`, `MacKeychainCredentialVault`, `SessionCredentialVault`, `JsonAppSettingsStore`, `JsonAccountStore`, `FileLoggerProvider`, `AppPaths` |
| `HCG.Mail.App` | Avalonia desktop app | `MainWindowViewModel`, `AccountViewModel`, `MessageListViewModel`, `MessageReaderViewModel`, `ComposeViewModel`, `AccountSetupViewModel`, `SettingsViewModel`, `ThemeService`, `DialogService`, views, `PreviewMailSession` |

Core contains no MailKit types, so the UI and tests depend only on Core abstractions; MailKit could be
replaced without touching view models.

## Runtime composition

`AppServices.Build(AppMode)` is the composition root (Microsoft.Extensions.DependencyInjection):

* **Normal mode:** OS vault from `CredentialVaultFactory`, JSON stores in the per-user settings folder,
  `MailKitMailSessionFactory`, real `ConnectionTester`, file logging.
* **Developer preview (`--developer-preview`, Debug builds only):** in-memory stores, `PreviewMailSession` with labelled sample data,
  a connection tester that makes no network calls, no file logging. A permanent banner states that
  nothing is connected or sent.

## Mail session model

`IMailSession` = one account's live connection.

* **One IMAP connection per account**, guarded by a `SemaphoreSlim` (MailKit clients are not
  thread-safe). Every operation goes through `RunAsync`, which
  * connects and authenticates lazily (`EnsureConnectedAsync`),
  * retries **once** after a dropped connection for idempotent operations (fetch, flags, list) —
    never for APPEND or MOVE, which could duplicate or misplace mail,
  * translates every exception into a `MailOperationException` with a user-facing message.
* **Cancellation only applies before a command starts.** Cancelling an in-flight MailKit command tears
  down the connection; instead stale results are ignored by the view models (version counters).
* **SMTP** uses a short-lived connection per send. After a successful send the session (a) saves a copy
  to Sent unless the server already did (Gmail capability or a `Message-ID` search), (b) flags the
  original `\Answered`, (c) removes the draft. Failures after delivery are reported as warnings, never
  as "not sent".
* **Message reading** uses `BODY.PEEK[]`, so opening a message never changes `\Seen` implicitly; the UI
  marks it read explicitly (configurable).
* A small LRU cache (6 parsed messages, memory only) serves attachment saves and forwards without
  re-downloading.

### Folder roles

`FolderRoleMapper` assigns Inbox/Drafts/Sent/Archive/Junk/Trash:
1. `INBOX` by protocol definition;
2. server SPECIAL-USE / XLIST attributes (authoritative);
3. fallback name matching for servers without SPECIAL-USE, English and Norwegian
   (*Sendte elementer, Utkast, Papirkurv, Søppelpost, Arkiv…*), top-level, `INBOX.` children or
   `[Gmail]/` containers only. Each role is assigned once. Navigation shows standard labels for roles
   (with the server name in the tooltip) and the server's own names for every other folder.

### Delete semantics

* In a normal folder: **move to Trash** (MOVE, or COPY + `\Deleted` + UID EXPUNGE fallback).
* In Trash, or when the server has no Trash: **permanent delete after confirmation**
  (`\Deleted` + `UID EXPUNGE`; plain `EXPUNGE` only on servers without UIDPLUS).

## UI architecture (MVVM)

* CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`), compiled
  Avalonia bindings everywhere.
* `MainWindowViewModel` orchestrates; `MessageListViewModel` owns the centre pane (newest-first list,
  client-side search, minimal-diff refresh via `CollectionSync` so selection survives refreshes);
  `MessageReaderViewModel` owns the reader; `ComposeViewModel` runs in its own non-modal window.
* Commands are disabled when they cannot run (no "decorative" buttons): e.g. Archive is disabled when
  the server has no Archive folder; missing standard folders appear dimmed with an explanation.
* **No background synchronisation.** The mail server is contacted only for user actions: start-up
  connect (if enabled), opening a folder or message, Refresh (F5), sending, saving drafts and message
  actions. There is no refresh timer, no IMAP IDLE, no NOOP keep-alive and no push mechanism; the only
  timer in the UI is a 120 ms debounce before opening the clicked message. Refresh is a single
  cancellable command (`RefreshCommand` / `RefreshCancelCommand`) that cannot run twice at once; cancel
  stops the remaining steps, while an IMAP command already in progress completes first (aborting it
  would drop the connection). Refreshing a folder that is already open sends one IMAP `NOOP` so the
  server reports newly arrived mail — only as part of that user action, never on a timer.

### Skins (design system)

`Themes/Palette.axaml` defines all design tokens twice — once for **HCG Future** and once for
**HCG Classic** — as Avalonia *custom theme variants* (`HcgThemeVariants`, inheriting Dark/Light so
Fluent controls match). All views use `DynamicResource`, so switching skins is a live resource change:
no window is recreated and no connection is touched. The choice persists in `settings.json`.
`Themes/Controls.axaml` holds shared control styles; `Themes/Icons.axaml` holds original 24×24 line
icons rendered by `Controls/IconView`.

### Safe HTML rendering pipeline

```
raw HTML ─► MailHtmlSanitizer (allow-list, remote blocking, colour fix-up)
         ─► HtmlPanel (HtmlRenderer: managed layout engine, no script engine)
              ├─ ImageLoad      → only cid:/data: images, remote only after "Load images"
              ├─ StylesheetLoad → always blocked
              ├─ LinkClicked    → never opened directly; confirmation dialog shows real host + URL
              └─ context menu   → disabled (it could open links without confirmation)
```
HTML is shown on a light "paper" surface in both skins, because most HTML mail assumes white.

## Persistence

| Data | Where | Notes |
|---|---|---|
| Account server settings | `accounts.json` | No secrets. Versioned DTO, invalid entries skipped |
| App settings (skin, refresh, …) | `settings.json` | Normalised on load; corrupt files are moved aside |
| Passwords | Windows Credential Manager / macOS Keychain | Keyed `account:<id>`; session-memory fallback elsewhere |
| Logs | `logs/hcg-mail-YYYYMMDD.log` | 7-day retention; no secrets or message content |
| Message bodies, attachments | **not cached** | Fetched on demand, held in memory only |

Settings folder: `%LOCALAPPDATA%\HCG\HCG Mail` (Windows), `~/Library/Application Support/HCG Mail`
(macOS), `$XDG_CONFIG_HOME/hcg-mail` (Linux); override with `HCG_MAIL_DATA_DIR`.

SQLite was listed as an *optional* cache technology. It is intentionally **not used** in the MVP: the
only persistent data are small settings files, and message content must not be cached in plaintext.
An encrypted header cache is described in [ROADMAP.md](ROADMAP.md).

## HCG ecosystem boundary

`Core/Integrations/IHcgIntegration` is an extension point for future, separately approved HCG
integrations (HCG Connect, HCG ERP, …). No integration is registered, nothing is discovered or
downloaded at runtime, and the app is fully functional without any HCG system.

## Error handling

All network/protocol exceptions are converted by `MailErrorTranslator` into `MailOperationException`
(`Kind` + plain-language `UserMessage`): DNS, refused/timeout, TLS handshake vs. certificate problems
(name mismatch, untrusted issuer, expired, revoked, revocation unknown), authentication, server
rejections (quoted), lost connections, missing folders/messages. Unknown exceptions never leak their
message text to the UI. Unhandled UI exceptions are logged and shown as a notification instead of
crashing the app (unsent compose text is preserved).
