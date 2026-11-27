# Deliverability — investigation, fixes and diagnostics

**Status (2026-11-01):** sending defects found in HCG Mail are fixed; Mail Health diagnostics, message
priority and delivery states are implemented. The spam-placement **root cause is not yet verified**: it
depends on DNS records and provider behaviour that could not be observed from the development
environment (outbound DNS is blocked there). The Mail Health tools below establish it on the founder's
computer in a few minutes, without any configuration change.

Nothing in this work changes TLS certificate validation, revocation checking, authentication settings,
DNS or provider configuration.

## 1. What HCG Mail sends — inspected on the wire

Method: messages built by `MimeConverter` were transmitted by MailKit to a capturing SMTP endpoint (full
EHLO/MAIL/RCPT/DATA transcript) and to the local Postfix/Dovecot test server, then read back raw.

| Area | Before | Evidence | Now |
|---|---|---|---|
| **EHLO name** (provider-specific behaviour) | **Defect.** MailKit 4.18 sent the computer's bare host name (`EHLO vm`; on a Mac e.g. `EHLO Yasins-MacBook-Pro`). Not an FQDN or address literal (RFC 5321 §4.1.1.1). The provider copies it into the first `Received:` header of every message, exposing the computer's name, and filters can score a malformed HELO. | SMTP transcript | Address literal of the real connection, `EHLO [192.168.1.20]`, as Apple Mail and Thunderbird do. Verified end-to-end: Postfix records `Received: from [127.0.0.1]`. (`Smtp/SmtpConnector.cs`) |
| **Transfer encoding / DKIM preservation** | **Defect.** When the server offers 8BITMIME, Norwegian text was sent as raw 8-bit. The provider DKIM-signs after submission; a relay that must downgrade 8-bit to 7-bit changes the signed body and DKIM fails (RFC 6376 §5.3 recommends 7-bit content for signing). | SMTP transcript (`Content-Transfer-Encoding: 8bit`) | Every part is 7-bit clean (quoted-printable/base64) before submission. Verified: no byte ≥ 128 in the delivered message. |
| **Line endings** | **Defect (minor).** A lone CR in typed text was sent as `=0D` inside a line. | SMTP transcript | CR, LF and CRLF are normalised; output is canonical CRLF. |
| RFC 5322 header set | One `From`, `Date` (local offset), `Message-ID`, `MIME-Version`; no duplicated single-instance fields. | transcript, `MessageFormatTests` | unchanged (correct) |
| From / Sender / Reply-To / Return-Path | `From` = account display name + address. No `Sender` (single author). No `Reply-To`. `Return-Path` is written by the receiver from `MAIL FROM`, which equals the From address. | transcript | unchanged (correct) |
| Envelope vs visible From | `MAIL FROM:<post@domain>` = From address → SPF can align with From for DMARC. | transcript | unchanged |
| Message-ID | Random, on the From domain (`<…@hakmicapitalgroup.com>`); 20 000 generated without collision; a draft keeps its ID so the Sent copy matches. | `SendingTests` | unchanged |
| Bcc | Not transmitted (MailKit hides it); kept in the Sent copy. | transcript, E2E | unchanged |
| MIME structure | `text/plain; charset=utf-8`, or `multipart/mixed` with attachments; valid boundaries; attachment names RFC 2231-encoded (MimeKit picks ISO-8859-1 when sufficient — valid, decoded by Gmail/Outlook/Apple Mail). | transcript, `SendingTests` | unchanged |
| HTML vs plain text | HCG Mail sends plain text only (by design). Text-only mail is not a spam signal and avoids HTML/text mismatch rules. | — | unchanged |
| Tracking / receipts | No tracking pixels, no `Disposition-Notification-To` / `Return-Receipt-To`. | `SendingTests` | unchanged, now asserted by tests |

## 2. Root cause — what the evidence permits

* **Verified:** the two HCG Mail defects above (EHLO host name, 8-bit bodies). Both are real and fixed, but
  neither is known to cause spam placement on its own; at most they are contributing signals.
* **Not verified (cannot be from the development environment):** the factors that most often put mail
  from a small custom domain in spam —
  1. no DKIM signature, or a signature with the provider's domain instead of yours (DMARC then fails);
  2. no DMARC record;
  3. an SPF record that does not include the servers the provider actually delivers from;
  4. the reputation of the provider's sending IP addresses or of the domain.
  These are settings at the DNS host and at Domene.no. HCG Mail cannot change them, and does not claim to.

**How to verify on your computer (≈5 minutes, nothing is changed):**

1. Start HCG Mail → **Mail Health** (left sidebar) → **Run checks**. Note the SPF, DKIM and DMARC sections.
2. From HCG Mail, send a short message to a Gmail address you own. In Gmail open it → ⋮ → **Show original**
   → **Copy to clipboard**. In Mail Health → **Analyze a received message** → paste → **Analyze**.
3. Read the **Conclusion** row:
   * *Failed* → authentication is the cause. Each failed row says what to change and where.
   * *Verified* (SPF, DKIM, DMARC all pass) → authentication is not the cause; placement is the receiver's
     reputation/content decision. See the Reputation tab.
4. **Copy report** gives a text version to send to Domene.no support if a provider change is needed.

## 3. External configuration steps (only after verification; each needs founder approval)

| If Mail Health shows | Change | Where |
|---|---|---|
| SPF: no record / permerror | One TXT record on the domain, `v=spf1 include:<provider value> ~all` (or `-all`). Ask Domene.no for the exact include value. | DNS host (Domene.no DNS panel) |
| Received message: `dkim=none` | Ask Domene.no to enable DKIM signing for the domain; publish the key they give you at `<selector>._domainkey.<domain>`. | Domene.no support + DNS host |
| DKIM alignment failed (`d=` is the provider's domain) | Ask for custom-domain DKIM (signing with your own domain). | Domene.no support |
| DMARC: no record | TXT `_dmarc.<domain>` = `v=DMARC1; p=none; rua=mailto:<address you read>`; after reports show all legitimate mail passes, `p=quarantine`. | DNS host |
| Reverse DNS / blocklist problems of the sending server | Report to the provider; they own those IP addresses. | Domene.no support |
| All pass, still spam | Register the domain in Google Postmaster Tools; ask recipients to mark "Not spam"; avoid sending identical messages to many recipients at once. | Reputation tab |

## 4. Mail Health (implemented)

Open from the sidebar. Every result is labelled **Verified**, **Warning**, **Failed** or **Not tested**;
no scores or inbox-placement figures are computed, because HCG Mail has no data to compute them from.

| Section | What is checked | Source |
|---|---|---|
| Domain authentication | Summary of SPF, DKIM, DMARC and the last analyzed message | below |
| SPF | one record, syntax, `all` qualifier, `ptr`, macros, ≤ 10 DNS lookups (recursive), broken includes | DNS TXT; RFC 7208 evaluator |
| DKIM | key published per selector, revoked/empty key, RSA key length (< 1024 failed, 1024 warning), Ed25519, `t=y` testing | DNS TXT at `selector._domainkey`; selectors come from analyzed messages or are entered |
| DMARC policy and alignment | record, `p`, `pct`, `rua`, alignment modes; DKIM/SPF alignment from an analyzed message | DNS TXT `_dmarc`; message headers |
| Sending server | MX, outgoing server address, forward-confirmed reverse DNS, whether SPF authorizes the server you submit to (warning only: providers relay through other servers) | DNS |
| SMTP history | replies from your outgoing server to recent sends from this computer, with plain-language meaning of common codes (5.7.1, 5.7.26, 5.7.8, 4.7.x …) | local `delivery-history.json` |
| Message format | the message HCG Mail builds, validated as transmitted (headers, line length, CRLF, 7-bit, Bcc hidden, priority headers, EHLO, no receipts) | built locally, nothing sent |
| Sender reputation | links to Google Postmaster Tools, Microsoft SNDS / sender support, Spamhaus, Talos, MXToolbox — not queried by HCG Mail | — |

**Analyze a received message** accepts pasted headers or a saved `.eml` file:

* the recipient's own `Authentication-Results` (topmost header only; older ones can be forged and are
  only counted), shown verbatim and interpreted (spf/dkim/dmarc/compauth);
* DKIM and SPF alignment with the From domain (relaxed unless the DMARC record says strict; the
  organizational domain is approximated as the last two labels — no Public Suffix List);
* independent DKIM verification (MimeKit `DkimVerifier`, keys from DNS) when the whole message is
  supplied; pasted text may alter whitespace, so a failure is a warning and the recipient's result is
  authoritative;
* spam-filter headers the recipient wrote (`X-Spam-*`, Rspamd, Microsoft `X-Forefront-Antispam-Report`
  / SCL) reported as is;
* the route and the first hop's EHLO name.

Network use: DNS queries through the computer's resolver, only when the user runs checks or analyzes a
message. Nothing is uploaded. Reputation links open in the browser after the usual link confirmation.

## 5. Message priority (implemented)

Composer → **Priority**: Low, Normal (default), High. Headers, each written exactly once:

| Priority | Headers |
|---|---|
| High | `Importance: high`, `X-Priority: 1`, `Priority: urgent` |
| Normal | `Importance: normal`, `X-Priority: 3` |
| Low | `Importance: low`, `X-Priority: 5` |

Drafts keep their priority; priority set by other clients (`X-Priority: 1 (Highest)`, `Importance: High`,
`Priority: non-urgent`) is read correctly. The composer states that recipients' mail programs decide how
priority is shown and that it does not affect spam filtering or delivery.

**Interoperability:** the header values are verified byte-for-byte in unit and end-to-end tests. How
recipients display them is decided by their programs: Outlook (desktop/web) and Apple Mail show
importance markers; Gmail does not display priority. Display on real Outlook, Gmail and Apple Mail
clients has **not** been tested yet — see the manual checklist. Some spam filters record the presence of
`X-Priority` as a minor signal; use High for messages that need it, not by default.

## 6. Delivery states (implemented)

| State | Meaning | Shown when |
|---|---|---|
| Not sent | connection, TLS or sign-in failed before the SMTP transaction | error in the composer + history |
| Submitted — no reply | message handed over, connection lost before the final reply: **outcome unknown** | history |
| Accepted by outgoing server | 250 after the message data, with the server's reply (e.g. `2.0.0 Ok: queued as 4Xy9`) | success notice + history |
| Deferred | 4xx: temporarily refused, not sent (no automatic retry) | error in the composer + history |
| Rejected | 5xx: refused, not sent, with the server's reason | error in the composer + history |
| Delivery confirmed | **not available** — standard SMTP has no delivery confirmation and no provider integration is configured | stated in history |
| Inbox placement | **unknown** — only the recipient's system knows | stated in history |

A successful send is never described as "delivered". No read receipts or tracking pixels are added.
`delivery-history.json` (last 300 sends, private file in the settings folder) holds time, account,
server, state, reply code/text, Message-ID, recipient count and priority — no subject, body or
attachments; a rejection reply may quote the refused address.

## 7. Reproducible comparison: HCG Mail vs another client

Purpose: decide whether placement differs because of HCG Mail or because of the domain/provider.
Do this only with mailboxes you own; it sends a handful of personal test messages.

**Setup**

* Sender: the same mailbox, through the same SMTP server and port, in HCG Mail and in one other client
  (Apple Mail, Thunderbird or Domene.no webmail). Record the other client's name and version.
* Recipients: one Gmail and one Outlook.com mailbox you control. Do not mark anything as spam / not spam
  during the test (that trains the filters).
* Identical content for every message: subject `Deliverability test <A|B>-<n> <date>`, plain-text body:
  `Hei,\n\ndette er en testmelding for leveringskontroll. Ingen handling nødvendig.\n\nMvh\n<name>`,
  no links, no attachments. Optionally repeat the series later with one small PDF attached.

**Run**

1. Send A-1 from HCG Mail, wait 2 minutes, send B-1 from the other client. Repeat to A-3/B-3
   (alternating avoids time-of-day effects).
2. At each recipient, note the folder (Inbox / Spam / Other) for every message.
3. Save every message as `.eml` (Gmail: Show original → Download original; Outlook.com: View message
   source → save).
4. In HCG Mail → Mail Health → Analyze a received message → **Open .eml file…** for each; **Copy report**
   into a text file named after the message.

**Compare** (one row per message): folder · spf · dkim (and `d=`) · dmarc · first-hop EHLO · transfer
encoding · Message-ID domain · `X-Priority` · any spam-filter header. To see every header difference
between two messages: `diff <(sed '/^\r\{0,1\}$/q' A-1.eml) <(sed '/^\r\{0,1\}$/q' B-1.eml)`.

**Interpretation**

* Both clients in spam and authentication failing → DNS/provider configuration (section 3).
* Both pass SPF/DKIM/DMARC and both in spam → domain or provider reputation, or content; not client-specific.
* Only HCG Mail in spam with otherwise identical authentication → keep the `.eml` pairs and report them;
  the header diff shows what differs.
* Six messages show a pattern, not a rate: no inbox-placement percentage can be derived from this.

## 8. Tests

| Area | Tests |
|---|---|
| MIME formatting, header serialization, line endings, 7-bit, Bcc, multipart | `Email/SendingTests`, `Email/MessageFormatTests` |
| Priority header values, no duplicates, draft round-trip, reading other clients' forms | `Email/SendingTests`, `MailHealthUiTests` |
| Message-ID uniqueness and domain | `Email/SendingTests` |
| SMTP submission, EHLO literal on the wire, delivery states | `Email/SendingTests` (capturing server), E2E `High_priority_message_arrives_…`, `Rejected_send_is_recorded_…`, `Wrong_password_is_recorded_as_not_sent` |
| Credential handling | E2E wrong-password test asserts the password never appears in the stored record; existing `CredentialVaultTests` |
| Error reporting | `SendingHistoryTests` (codes and plain-language meaning), existing `ErrorTranslatorTests` |
| Domain authentication diagnostics | `SpfTests` (parser, evaluator, includes, redirect, 10-lookup limit, macros, CIDR), `PolicyRecordTests`, `DomainAuthenticationCheckerTests`, `ReceivedMessageAnalyzerTests` (incl. real DKIM signing + tamper detection) |
| UI | `MailHealthUiTests` (all eight sections, the four labels only, analysis, history, composer priority) |
