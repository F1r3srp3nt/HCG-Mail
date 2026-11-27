# Google accounts (Gmail, Google Workspace)

**Status:** implemented for the **desktop app** (macOS, Windows, Linux). It has been tested against a fake Google
sign-in server and a recording fake of the Gmail API. **It has not been tested against real Google accounts**: the
repository contains no Google OAuth client, because the Google Cloud registration below has not been made yet.
Without one, *Add Google account* says Google sign-in is not set up and offers no sign-in.

The **iPhone/iPad app does not offer Google sign-in yet.** It needs its own iOS OAuth client and redirect (see
[iOS, later](#ios-later)). Gmail can still be added there with IMAP and a Google app password, where the account allows
app passwords.

## How it works

| Part | Implementation |
|---|---|
| Sign-in | OAuth 2.0 authorization code flow with **PKCE (S256)** and a random `state` in the **system browser**. Redirect to a loopback listener on `http://127.0.0.1:<free port>/`, which answers once and then closes. HCG Mail never sees or stores the Google password. Sign-in is cancelled after 5 minutes, by the *Cancel sign-in* button, or when the user denies consent. |
| Client | Google Cloud OAuth client of type **Desktop app**. Google issues it a client secret. For installed apps Google states that this secret is not treated as confidential (anyone can read it from the program). HCG Mail sends it with PKCE. It is still **never committed** to the repository. |
| Accounts | Personal Gmail and Google Workspace accounts that have Gmail. |
| Permissions | `https://www.googleapis.com/auth/gmail.modify` gives read, compose, send, labels, Trash and drafts, but **no permanent deletion**. `openid email profile` show which Google account signed in. Nothing else is requested. If the user unticks the Gmail permission on Google's consent page, sign-in stops with an explanation, and the token is discarded. |
| Identity check | HCG Mail reads the ID token returned by Google's token endpoint over TLS. It checks the issuer (`accounts.google.com`), the audience (this client ID) and `email_verified`. The account key is the Google `sub`. |
| Tokens | The **refresh token** is stored in the same protected credential store as IMAP passwords: the macOS Keychain (service "HCG Mail") or Windows Credential Manager. On Linux there is no persistent store yet, so it lives in memory for the session only, as IMAP passwords do there. It is stored under `oauth:google:<sub>`, one entry per Google account. Access tokens stay in memory only. Tokens are never written to logs, settings or account files. |
| Mailbox | Gmail API v1 (`GmailMailSession`). Messages are fetched as raw MIME (`format=raw`). They go through the same parser, sanitizer, inline-image and attachment handling as IMAP mail. Send uses `messages.send` with MIME. Drafts use `drafts.create`/`drafts.update`. |
| HCG roles | Google authorization covers the connected mailbox only. It grants no roles or permissions in HCG Mail, HCG Connect or any HCG system. Mailbox OAuth is separate from the HCG Connect identity. |

### Behaviour

* **Folders:** Gmail labels are shown as folders. Inbox, Drafts, Sent, Spam and Trash are the standard folders.
  "All Mail" acts as the Archive. User labels are listed with their nesting (`Parent/Child`).
* **Messages:** newest first, page by page (Gmail page tokens). Unread, starred and draft state come from the labels.
* **Actions:**
  * Mark read/unread and flag (star) by changing labels.
  * Archive removes `INBOX`.
  * Move changes labels.
  * Delete moves the message to Trash. Gmail empties Trash after 30 days. Deleting from Trash is refused with that
    explanation, because the requested permission cannot delete permanently.
* **Sending:** through the Gmail API. Google saves the message in Sent. The send history records
  "gmail.googleapis.com (Gmail API)". A draft that was sent is removed from Drafts.
* **Sync:** HCG Mail keeps no local message store, so each list load asks Gmail for the current page. Automatic
  checking (Settings → Mail) is off by default.
* **Errors:**
  * An expired, revoked or invalid sign-in shows "Reconnect", after one forced token refresh.
  * Missing permissions also show "Reconnect".
  * Gmail API disabled in the Cloud project: explained as a setup problem.
  * Rate limits: reads retry up to 3 times (1/2/4 s, or `Retry-After` up to 10 s). **Writes are never retried
    automatically.**
  * Network loss: offline state; Refresh works again without a new sign-in.
* **Several accounts:** each Google account has its own refresh token, access-token cache and session. Removing or
  reconnecting one does not affect the others, or any Microsoft or IMAP account.
* **Reconnect:** signing in as a different Google account than the one being reconnected is refused, and the token
  that was just issued is discarded.
* **Removal:** removing the account deletes its token and send history from this computer. No mail is deleted. HCG
  Mail does **not** revoke the grant at Google, because that would also sign out HCG Mail on the user's other
  computers. To withdraw access everywhere: myaccount.google.com/permissions. If the Keychain cannot delete the token
  (for example, it is locked), HCG Mail retries at the next start.

## Google Cloud registration — to be done by HCG

Use an HCG-owned Google account, ideally in HCG's Google Workspace, so that ownership does not depend on one person.
Menu names below are from the Google Auth Platform pages of the Cloud console (console.cloud.google.com → *Google Auth
Platform*); older consoles call them *OAuth consent screen* and *Credentials*.

1. **Project:** create a project, for example `hcg-mail`. Add a second owner.
2. **Enable the API:** *APIs & Services → Library → Gmail API → Enable*. Without this, sign-in works but the mailbox
   cannot be opened, and HCG Mail reports the Gmail API as disabled.
3. **Branding:**
   * App name `HCG Mail`, user support e-mail, and logo `assets/icons/hcg-mail-1024.png` (Google reviews logos, and
     uploading one triggers brand verification).
   * Application home page, privacy policy and terms of service URLs on an HCG domain.
   * **Authorized domains:** the HCG domain of those pages. It must be verified in Google Search Console by an owner
     of the project.
   * Developer contact e-mail.
4. **Audience (user type):**
   * **External** for anyone with a Google account, which is needed for public distribution.
   * **Internal** only lets users of HCG's own Google Workspace sign in, and needs no Google verification. It is a
     good choice if HCG Mail is only used inside HCG for now.
   * External apps start in **Testing**. See [Testing restrictions](#testing-restrictions).
5. **Data access (scopes):** add exactly:
   * `.../auth/gmail.modify`, listed by Google as a **restricted** scope
   * `openid`
   * `.../auth/userinfo.email`
   * `.../auth/userinfo.profile`

   Do not add `https://mail.google.com/` (full access, including permanent deletion). Do not add other Google APIs.
6. **Clients → Create client:**
   * Application type **Desktop app**, name `HCG Mail desktop`.
   * No redirect URI needs to be entered: loopback redirects (`http://127.0.0.1:<port>/`) are allowed automatically
     for Desktop clients.
   * Copy the **client ID** (`<number>-<id>.apps.googleusercontent.com`) and the **client secret** (`GOCSPX-…`).
   * Do **not** create a *Web application* client for HCG Mail. Its secret is confidential and must not ship in an app.
7. **Test users** (while in Testing): add the Google accounts that will test, starting with a dedicated test Gmail
   account.

### Testing restrictions

While the app's publishing status is **Testing**:

* Only listed **test users** (at most 100) can sign in. Anyone else sees "Access blocked: HCG Mail has not completed
  the Google verification process".
* Google shows test users a warning page ("Google hasn't verified this app"). They continue with *Continue*.
* **Refresh tokens expire after 7 days.** HCG Mail then shows "Reconnect" and the dialog mentions the 7-day limit.
  This is expected, not a bug.

### Verification for public distribution

Before users outside the test list can sign in, the app must be set to **In production** and pass Google's review.
Because `gmail.modify` is a restricted scope, this involves:

1. **Brand verification:** app name, logo, home page and privacy policy on the verified domain.
2. **Restricted-scope verification:**
   * A justification for each scope.
   * A demo video showing the sign-in, the consent screen (with the client ID visible in the browser address bar) and
     how Gmail data is used.
   * A privacy policy that meets the Google API Services User Data Policy, including the **Limited Use** requirements:
     Gmail data is used only to show and manage the user's own mail, is not sold, is not used for advertising, and no
     human reads it except with the user's consent, for security, or by law.
3. **Security assessment (CASA):** Google can require an annual third-party Cloud Application Security Assessment for
   apps with restricted scopes.
   * HCG Mail sends Gmail data only between the user's device and Google. HCG has no server for it.
   * Describe this clearly in the review. Google decides whether an assessment is required and at which tier.
   * Check the current rules on Google's *Restricted scope verification* help page before submitting. They change.
4. Google Workspace administrators can still block or allow HCG Mail for their organisation (Admin console →
   Security → API controls). An organisation that blocks unconfigured third-party apps must mark HCG Mail as trusted.

Until verification is complete, keep the app in Testing. Do not distribute builds with the Google client to people who
are not test users.

### Putting the client values into a build

The client ID is public. The Desktop client secret is not confidential for installed apps, but it is **never
committed**: it comes only from the environment, the command line or the git-ignored local file. Every build reads
both values through [`oauth-clients.props`](../oauth-clients.props). The first source that has a value wins:

| Priority | Source | Use it for |
|---|---|---|
| 1 | `-p:HcgGoogleClientId=… -p:HcgGoogleClientSecret=…` | one-off builds |
| 2 | environment variables `HCG_GOOGLE_CLIENT_ID`, `HCG_GOOGLE_CLIENT_SECRET` | CI (secret as an Actions **secret**), HCG Executable Maker, `scripts/publish-windows.sh` |
| 3 | `oauth-clients.local.props` (git-ignored; copy `oauth-clients.local.props.example`) | a developer Mac |
| 4 | `HcgGoogleClientId` committed in `oauth-clients.props` (empty today; there is deliberately no slot for the secret) | the client ID, once HCG commits it |

* **The build output** reports `HCG020: … Google: client ID …, client secret present`. The secret itself is never
  printed. `HCG022` warns about a malformed client ID.
* **Partial configuration:** the app enables Google sign-in only when the client ID has the right form **and** a secret
  is present. Otherwise it explains what is missing.
* **Local test without rebuilding:** the desktop app also reads `HCG_MAIL_GOOGLE_CLIENT_ID` and
  `HCG_MAIL_GOOGLE_CLIENT_SECRET` at start-up.
* **GitHub release workflow:**
  * repository **variable** `HCG_GOOGLE_CLIENT_ID`
  * repository **secret** `HCG_GOOGLE_CLIENT_SECRET`
* **macOS desktop:**
  * `oauth-clients.local.props`, then `dotnet run --project src/HCG.Mail.App`
  * or `HCG_MAIL_GOOGLE_CLIENT_ID=… HCG_MAIL_GOOGLE_CLIENT_SECRET=… dotnet run --project src/HCG.Mail.App`

## iOS, later

This is not implemented. Google sign-in on iPhone/iPad needs:

* an OAuth client of type **iOS** with bundle ID `com.hakmicapitalgroup.hcgmail` (no secret)
* the reversed-client-ID URL scheme `com.googleusercontent.apps.<id>:/oauthredirect` in Info.plist
* an `ASWebAuthenticationSession` adapter like the Microsoft one

The Gmail session and view models are shared and would be reused.

## Testing with real accounts (founder, manual)

Nothing in the automated tests contacts Google.

* Use a **dedicated test Gmail account** that is listed as a test user, and a Workspace test user if one is available.
* Send test mail only to your own test addresses.
* Do not use a mailbox whose contacts could receive test mail.

1. *Add Google account* → browser opens → cancel → "Sign-in was cancelled. No account was added."
2. Sign in again but untick the Gmail permission → explained, nothing added.
3. Sign in fully → folders (Inbox, Drafts, Sent, All Mail as Archive, Spam, Trash, your labels), unread counts.
4. Read HTML mail with inline images and attachments. Check that remote images stay blocked until allowed.
5. Mark read/unread, star, archive (disappears from Inbox, visible in All Mail), move to a label, and delete (to
   Trash). Delete in Trash → refused with the 30-day explanation.
6. Save a draft, edit it, send it to the test address (the draft disappears and the message appears in Sent). Reply,
   reply-all and forward between test addresses only.
7. Revoke access at myaccount.google.com/permissions → "Reconnect" → Reconnect works.
8. Reconnect while signed in to a different Google account in the browser → refused, account unchanged.
9. Add a second Google account and an IMAP account → each shows only its own mail. Remove one Google account → the
   others keep working. Keychain Access no longer has `oauth:google:…` for the removed account.
10. Turn off the network → offline state. Turn it on → Refresh works without signing in again.
11. In Testing, after 7 days → "Reconnect" mentioning the testing limit.
