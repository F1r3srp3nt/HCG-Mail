# TLS certificate revocation — investigation and decision record

**Status:** diagnosis and reporting improvements implemented; **security policy unchanged**
(an undeterminable revocation status still blocks the connection). A policy change needs founder
approval — see *Options* below.

## Reported problem

On macOS, connecting to a Domene.no mailbox (IMAP 993 / SMTP 465, SSL/TLS) fails after DNS and TCP
succeed, with: *"the certificate's revocation status could not be checked because the certificate
authority could not be reached."*

## Where it fails

HCG Mail sets `CheckCertificateRevocation = true` on every MailKit client and accepts a certificate only
when the platform reports `SslPolicyErrors.None` (`TlsValidationRecorder.Validate`). The error
translator checks problems in a fixed order — no certificate → name mismatch → revoked → expired →
untrusted/incomplete chain → revocation unknown. Reaching the last branch means .NET found the
**hostname correct, the dates valid and the chain anchored in a trusted root**; the only chain status
was `RevocationStatusUnknown` / `OfflineRevocation`. The certificate is **not** known to be revoked.

## Evidence

| Evidence | Result |
|---|---|
| Error text and translator order | Only the revocation step failed (see above) |
| Direct handshake with Domene.no from the development container | Not possible: the container blocks outbound DNS/IMAP and certificate-transparency sites |
| Local reproduction (Dovecot, certificate with **CRL only, no OCSP**) on Linux | CRL reachable → passes. CRL unreachable → exactly the reported failure; `--diagnose-tls` shows chain otherwise valid |
| Platform behaviour (documented, not reproducible on Linux) | On macOS, .NET delegates revocation checking to Apple's Security framework, which checks revocation through **OCSP** and does not download CRLs. On Windows and Linux .NET fetches CRLs itself |
| Industry change | Let's Encrypt stopped OCSP in 2025 (certificates carry only a CRL); the CA/Browser Forum made OCSP optional for all public CAs |

### Most likely root cause (to be confirmed on the Mac)

Domene.no's mail certificate probably publishes a **CRL but no OCSP responder**. macOS cannot obtain a
positive revocation answer for such a certificate, so .NET reports "revocation status unknown" even on
a healthy network. The same certificate would pass on Windows and Linux. Alternative causes: the
OCSP/CRL server is blocked by a firewall, VPN, proxy or content filter.

**Confirm with** (read-only, no login, no credentials):

```bash
cd "HCG-Mail/Windows executable Application"
dotnet run --project src/HCG.Mail.App -- --diagnose-tls <your IMAP server>:993
dotnet run --project src/HCG.Mail.App -- --diagnose-tls <your SMTP server>:465
```

The report (also saved in `~/Library/Application Support/HCG Mail/`) shows the chain, the OCSP and CRL
addresses, chain builds with online/offline/no revocation checking (comparison only) and whether the
revocation servers are reachable. Section 6 states the conclusion. Independent cross-check with
standard tools:

```bash
HOST=<your IMAP server>
openssl s_client -connect $HOST:993 -servername $HOST -showcerts </dev/null 2>/dev/null \
  | openssl x509 -noout -subject -issuer -dates -ext authorityInfoAccess,crlDistributionPoints
```

Another mail client accepting the certificate is **not** proof that it is valid: most clients treat an
unknown revocation status as acceptable (soft-fail), which HCG Mail currently does not.

## Changes made (no policy change)

* `TlsValidationRecorder` records the chain, per-certificate status and the OCSP/CRL addresses; its
  decision is still exactly `sslPolicyErrors == None`.
* New error kind `RevocationUnavailable`, used **only** when the certificate is otherwise valid. The
  message says so, names the revocation service, states that this is not evidence of revocation, and on
  macOS explains the CRL-only case. Confirmed revocation is still reported as revoked; expired,
  mismatched and untrusted certificates keep their own messages.
* A TLS handshake that times out is now reported as a timeout, not as a TLS/port problem.
* `--diagnose-tls host:port`: read-only diagnosis described above.
* Regression tests with real .NET chain building and a local CRL server: valid, expired, hostname
  mismatch, untrusted CA, revoked, CRL unreachable, CRL server error, combined problems, timeout.

## Options for founder decision

| Option | What changes | Security impact | Effort |
|---|---|---|---|
| **0. Keep current policy** | Nothing | Strongest; HCG Mail stays unusable on macOS for servers whose certificates can't be revocation-checked there | none |
| **1. Soft-fail only for "status unknown"** (behaviour of Apple Mail, Thunderbird, Firefox/Chrome defaults) | Accept when the *only* chain problems are `RevocationStatusUnknown`/`OfflineRevocation`; hostname, trust, dates and a *confirmed* revocation still block. Show a visible "revocation not verified" notice on the account and in the connection test. | An attacker who obtained a server's private key **and** can block the revocation service (e.g. on hostile Wi-Fi) could impersonate the server until the certificate expires. Short certificate lifetimes (≤ 90 days, shrinking) limit the window. | small |
| **2. HCG Mail checks CRLs itself on macOS** | When macOS reports "unknown" and the certificate has a CRL address, download the CRL over HTTP, verify its signature against the issuing CA from the validated chain, check freshness and look up the serial number; accept only on a verified "not revoked". | Keeps hard-fail semantics (an unreachable CRL still blocks) and fixes the CRL-only case. Adds security-sensitive parsing/verification code that needs careful review and tests. | medium |
| 3. Per-account opt-in override | User explicitly allows "connect when revocation can't be verified" for one server, with a warning | Same risk as option 1, limited to accounts where the user chose it | small |

**Recommendation:** first run `--diagnose-tls` on the Mac to confirm the cause. If it confirms a
CRL-only certificate on macOS, implement **option 2** (keeps the strict policy and fixes the real
problem); option 1 is the industry-standard compromise if a faster fix is preferred. Neither will be
implemented without explicit approval.
