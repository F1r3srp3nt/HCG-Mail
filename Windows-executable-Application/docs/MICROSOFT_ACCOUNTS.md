# Microsoft accounts (Outlook.com, Hotmail, Microsoft 365)

**Status:** implemented and tested against a recording fake of Microsoft Graph and a fake sign-in. **Not yet
tested against real Microsoft accounts**: this build has no Microsoft application (client) ID, because the app
registration below has not been made yet. Without it, "Add Microsoft account" explains that sign-in is not set
up and does nothing else.

## How it works

| Part | Implementation |
|---|---|
| Sign-in | MSAL.NET (`Microsoft.Identity.Client` 4.91.1) as a **public client**: authorization code flow with **PKCE** in the **system browser**, loopback redirect `http://localhost`. No client secret exists; HCG Mail never sees or stores the Microsoft password. |
| Accounts | Authority `common`: personal Microsoft accounts and work/school accounts in any Microsoft Entra tenant. |
| Permissions (delegated) | `User.Read` (name and address), `Mail.ReadWrite` (list, read, flag, move, delete, drafts), `Mail.Send` (send). MSAL adds `openid`, `profile`, `offline_access`. Nothing else is requested. |
| Tokens | MSAL token cache, persisted by `Microsoft.Identity.Client.Extensions.Msal`: DPAPI (current user) on Windows, the login Keychain on macOS, Secret Service on Linux. If that storage is unavailable the tokens stay in memory only — never in a plaintext file. Windows Credential Manager is not used for tokens because its 2.5 KB entry limit is smaller than a token cache. |
| Mailbox | Microsoft Graph v1.0 (`GraphMailSession`). Message bodies are fetched as MIME (`/messages/{id}/$value`) and go through the same parser, body selection, sanitizer, inline images and attachment handling as IMAP mail. Sending (`/me/sendMail`) and drafts (`POST /me/messages`) also use MIME. |
| HCG roles | Microsoft authorization grants access to the connected mailbox only. It grants no roles or permissions in HCG Mail, HCG Connect or any HCG system; HCG Connect remains the HCG identity and permissions system. |

### Behaviour

* **Folders:** Inbox, Drafts, Sent Items, Deleted Items, Junk Email and Archive (when present) are found by their
  well-known names; other folders, including subfolders (3 levels), are listed by name.
* **Messages:** newest first, 150 per page, older pages on request; previews come from Graph's `bodyPreview`, cleaned.
* **Actions:** read/unread and flag (`PATCH`), move/archive (`/move`), delete (to Deleted Items; permanently when
  already there), compose, reply, reply-all, forward, drafts, attachments and inline images.
* **Search:** "Search the whole folder" uses Graph `$search` (up to 100 results). Typing in the search box only
  filters the messages already loaded; the list says which of the two you are looking at.
* **Sending account:** shown in the composer's From field; each account has its own session and token.
* **Errors:** expired or revoked sign-in → "Reconnect" (one silent forced token refresh is tried first);
  throttling (429/503) → reads wait for `Retry-After` (at most 10 s, 2 times); **writes are never retried
  automatically** (a lost reply would otherwise send or move twice). A send whose connection drops after the request
  left is recorded as "Submitted — no reply" (outcome unknown), not as failed.
* **Sync:** HCG Mail keeps no local message store, so Graph delta queries have nothing to keep in sync; each list
  load asks Graph for the current page. Automatic checking (Settings → Mail) is off by default.
* **Removal:** removing the account deletes its tokens and send history from the computer; no mail is deleted. To
  also withdraw HCG Mail's access at Microsoft: account.microsoft.com → Privacy → Apps and services (personal) or
  myapps.microsoft.com (work or school).

### Not supported (explained in the app)

Mailboxes on on-premises Exchange servers (without Exchange Online), shared mailboxes, accounts without an Exchange
Online mailbox/licence (Graph returns `MailboxNotEnabledForRESTAPI`). Tenants that block user consent need an
administrator to grant consent (AADSTS65001/90094). Conditional Access blocks are reported as such. Microsoft always
saves sent mail in Sent Items for these accounts. The "answered" marker is not written to Microsoft mailboxes.

## App registration (Microsoft Entra) — to be done by HCG

1. Sign in to the [Microsoft Entra admin center](https://entra.microsoft.com) with an HCG account allowed to register
   applications → **Applications → App registrations → New registration**.
2. **Name:** `HCG Mail`.
3. **Supported account types:** *Accounts in any organizational directory (Any Microsoft Entra ID tenant —
   Multitenant) and personal Microsoft accounts (e.g. Skype, Xbox)*.
4. **Redirect URI:** platform **Public client/native (mobile & desktop)**, value exactly `http://localhost`
   (MSAL picks a free port at sign-in; Entra accepts any port for `localhost`). Do **not** add a Web or SPA platform.
5. **Certificates & secrets:** add **nothing**. HCG Mail is a public client and must not contain a secret.
6. **Authentication → Advanced settings → Allow public client flows:** leave **No** (only needed for device-code or
   password flows, which HCG Mail does not use).
7. **API permissions → Add a permission → Microsoft Graph → Delegated permissions:** `User.Read`,
   `Mail.ReadWrite`, `Mail.Send` (and optionally `offline_access`). Do **not** add application permissions. Do not
   grant tenant-wide admin consent unless HCG's own tenant wants it for its users.
8. **Branding & properties:** publisher domain, logo (`assets/icons/hcg-mail-1024.png`), privacy statement URL and
   terms of service URL — shown on Microsoft's consent page.
9. **Publisher verification** (Branding & properties → verify with a Microsoft AI Cloud Partner Program ID):
   recommended before users outside HCG sign in; many organisations only allow user consent to verified publishers.
10. Copy the **Application (client) ID** (a GUID). The directory (tenant) ID is not needed.

### Putting the client ID into a build

The client ID is public information, not a secret.

| Where | How |
|---|---|
| Local development | environment variable `HCG_MAIL_MICROSOFT_CLIENT_ID=<GUID>` before `dotnet run` |
| Local publish | `HCG_MICROSOFT_CLIENT_ID=<GUID> scripts/publish-windows.sh` (PowerShell: `$env:HCG_MICROSOFT_CLIENT_ID="<GUID>"`) |
| GitHub release workflow | repository **variable** `HCG_MICROSOFT_CLIENT_ID` (Settings → Secrets and variables → Actions → Variables) |

The value is compiled into the app as assembly metadata (`-p:HcgMicrosoftClientId=<GUID>`). A value that is not a
GUID is ignored and the app reports sign-in as not set up.

## Testing with real accounts (founder, manual)

Real mailbox writes and test sends are deliberately left for you; nothing in the automated tests contacts Microsoft.
Use a test mailbox where possible.

1. Add a personal Outlook.com account and a Microsoft 365 work account; cancel once in the browser (nothing added).
2. Check folders, paging (Load older), reading HTML mail with inline images, attachments, remote-image blocking.
3. Mark read/unread, flag, move to Archive, delete (to Deleted Items, then permanently).
4. Compose and send to yourself; save a draft, edit it, send it (draft disappears); reply, reply-all, forward.
5. "Search the whole folder" for a word in an old message.
6. Revoke access at account.microsoft.com → the account shows "Sign in to Microsoft again" → Reconnect.
7. Reconnect while signed in as a different Microsoft user → refused, account unchanged.
8. Remove the account → tokens gone (Keychain Access / no "HCG Mail Microsoft sign-in" item), mail untouched.
