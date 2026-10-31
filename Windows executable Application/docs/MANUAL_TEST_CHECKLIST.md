# HCG Mail — Manual test checklist (Domene.no mailbox)

Use a **real Domene.no mailbox you control**. Record results in the *Result* column (✅ / ❌ + note).
Do not send test mail to people outside the test. Nothing in this checklist is automated.

**Before you start**

* Find your **incoming (IMAP) and outgoing (SMTP) server names** in the Domene.no control panel or
  welcome email. HCG Mail does not guess them. Typical security settings: IMAP **993 SSL/TLS**,
  SMTP **465 SSL/TLS** (the Domene.no preset fills these in).
* Have a second mailbox (any provider) available to send to / receive from.
* Platform: ☐ Windows 11 ☐ Windows 10 ☐ macOS — build/version: ________

| # | Scenario | Steps | Expected result | Result |
|---|---|---|---|---|
| 1 | **Successful connection** | Add email account → Provider *Domene.no* → email, display name, password, IMAP/SMTP server names → **Test connection** | All six steps green: DNS, TCP, TLS (shows TLS version + certificate), IMAP authentication, folder access (lists Sent/Drafts/Trash…), SMTP ("No test email was sent") | |
| 2 | Save account | Click **Save account** | Account card shows *Connected*; Inbox opens; standard folders listed; unread badges match webmail | |
| 3 | Password storage | Quit and restart HCG Mail | Connects without asking for the password (Windows Credential Manager / macOS Keychain). Confirm an *HCG Mail/account:…* entry exists in Credential Manager / Keychain Access, and that `accounts.json` contains no password | |
| 4 | **Invalid password** | Account settings → enter a wrong password → Test connection | TLS passes; *IMAP authentication* and *SMTP* fail with "rejected the username or password"; nothing else claims success | |
| 5 | **Invalid hostname** | Change IMAP server to `imap.does-not-exist.invalid` → Test | *DNS resolution* fails: "could not be found"; later IMAP steps skipped | |
| 6 | **TLS failure** | (a) set IMAP security to *SSL/TLS* with port **143**; (b) set the server to the provider's IP address instead of its name | (a) TLS step fails, suggesting the port/security mismatch; (b) fails with "certificate … does not match"; in both cases **no password is sent** and no plaintext fallback happens | |
| 7 | Restore settings | Put the correct values back → Test → Save | All green again | |
| 8 | **Inbox loading** | Select Inbox; press F5 | Newest messages first; sender, subject, preview, time, attachment and flag icons correct; count line matches the server | |
| 9 | Large folder | Folder with > 150 messages → *Load older messages* | Older messages are appended; no duplicates | |
| 10 | Search | Type a sender name / subject word | List filters instantly (loaded messages only); clear (×) restores | |
| 11 | **Reading messages** | Open a plain-text message and an HTML newsletter | Text readable; HTML on white card; "remote images blocked" banner; *Load images* shows them; *Show plain text* works; message becomes read (also in webmail) | |
| 12 | Links | Click a link in an HTML message | Confirmation shows the real host + URL; *Open link* opens the browser; *Cancel* does nothing | |
| 13 | Read/unread & flag | Toolbar *Mark unread*, *Flag*; Ctrl+U | State changes immediately and is visible in webmail after refresh | |
| 14 | **Attachment download** | Message with PDF/image → download icon | Saved to the Downloads folder (or chosen folder); notification "click to show" reveals it; file opens correctly in its own app; existing names get " (2)" | |
| 15 | Dangerous attachment | Message with a `.exe`/`.js`/`.docm` attachment (send yourself a renamed harmless file) | Warning icon; confirmation before saving; HCG Mail never opens it | |
| 16 | **Sending email** | Compose → To = second mailbox, Cc, subject, body, attach a file → Send (Ctrl+Enter) | "Message sent"; second mailbox receives it with attachment, correct From name/address and Norwegian characters (æøå) intact | |
| 17 | Validation | Compose with an invalid address / no recipient / no subject | Clear error for invalid or missing recipients; confirmation for empty subject; nothing sent | |
| 18 | **Sent folder synchronisation** | Open *Sent* after test 16 | Exactly **one** copy of the sent message (no duplicate), marked read, with attachment. Check webmail's Sent folder too | |
| 19 | Reply / Reply all / Forward | From the second mailbox send a message with Cc to a third address → Reply all in HCG Mail; Forward a message with attachment | Reply all: original sender in To, Cc kept, your own address excluded; subject "Re:"; quoted text; recipient's client threads it. Forward: "Fwd:", attachments included | |
| 20 | Drafts | Compose → *Save draft* → close → open Drafts → *Edit draft* → change → *Save draft* → Send | Draft appears in Drafts (and webmail); re-save replaces it (only one); after sending it disappears from Drafts | |
| 21 | Close with unsaved changes | Type in a new compose window and close it | Asks *Save draft / Discard / Cancel* | |
| 22 | Delete | Delete a test message (Delete key) → open Trash → delete again | First moves to Trash; second asks for confirmation and deletes permanently | |
| 23 | Archive | Archive a message (if the account has an Archive folder) | Moved to Archive; button disabled if no Archive folder exists | |
| 24 | **Reconnection after network interruption** | With HCG Mail open: disconnect Wi-Fi/Ethernet → press F5 → reconnect network → press F5 | While offline: clear "could not connect / connection lost" message, app stays responsive, no crash; after reconnecting: list refreshes and status returns to *Connected* without restarting | |
| 25 | Sleep / resume | Put the computer to sleep for > 10 min, wake, open a message | Reconnects automatically on the next action | |
| 26 | **Manual refresh only** | Send yourself a message from the second mailbox and wait 10 minutes without touching HCG Mail; then press Refresh (F5) | Nothing changes while you wait (no background checking). After Refresh: spinner + *Cancel* while running, the new message appears, a notification says "1 new message", status bar shows "Last refreshed HH:mm". Pressing F5 repeatedly never starts a second refresh at the same time | |
| 27 | Skins | Settings → Appearance → HCG Classic / HCG Future | Switches instantly; connection stays up; choice remembered after restart | |
| 28 | Remove account | Account settings → Remove account | Confirmation; account and stored password removed; no mail deleted on the server | |

**Sign-off**

Tester: ______________ Date: __________ Overall: ☐ Pass ☐ Pass with issues ☐ Fail

Attach `logs/hcg-mail-*.log` from the settings folder when reporting problems (logs contain no
passwords or message content).
