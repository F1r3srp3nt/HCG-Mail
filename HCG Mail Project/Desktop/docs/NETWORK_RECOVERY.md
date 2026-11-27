# Restricted networks, Network Doctor and connection recovery

HCG Mail 1.0.0-beta.2 handles school, guest and other restricted Wi-Fi without silently stopping synchronization. The user
always sees **what is wrong, whose problem it is and what happens next**, mail stays readable offline, and nothing written
while offline is lost.

Security does not change on any network. HCG Mail **never** turns off certificate validation, never falls back to an
unencrypted connection (no `Auto`, `None` or `StartTlsWhenAvailable`), never relaxes validation because a VPN is on, and
never sends a password before the server's certificate has been verified.

## Network Doctor (Mail Health › Network Doctor)

The Network Doctor runs only when the user starts it (or presses *Network Doctor* on the offline banner). It measures from the
outside in and turns the facts into one verdict (`NetworkClassifier`):

| Step | What is measured | Separates |
| --- | --- | --- |
| Device connection | Active interfaces (Wi-Fi, Ethernet, mobile, VPN) | No network at all |
| Wi-Fi sign-in page | Plain-HTTP request that must return an empty `204` | Captive portals |
| Name lookups | A well-known public name | Broken DNS on the network |
| Internet access | HTTPS request with normal certificate validation | Network up but offline |
| Certificate interception | Whether that well-known site's certificate verifies | Inspecting proxies and content filters |
| Mail ports on this network | TCP connection (nothing sent) to a large public provider's ports 993 and 465/587 | **Network blocks mail** vs **server outage** |
| Incoming / outgoing server | DNS, TCP, TLS with full certificate validation, then sign-in | Server down, bad certificate, wrong settings, wrong password |
| Alternatives for sending | Provider-documented ports (or the other standard submission port, confirmed by the server) | A way to keep sending on this network |
| VPN | Active VPN interfaces and the optional HCG VPN state | Information only |
| Recent network events | Transitions, sleep and wake recorded by the network monitor | When the trouble started |

Verdict categories: **Working**, **No connection**, **Network restriction**, **Server problem**, **Password refused** and
**HCG Mail problem** (every network, certificate and sign-in check passes while the account failed with an unexpected
error). Each verdict has a plain explanation and concrete actions.

*Copy report* produces a support report without passwords, tokens or message content; e-mail addresses keep only their
domain (`LogRedactor`).

### What the Network Doctor contacts

Only while it runs: `connectivitycheck.gstatic.com` and `www.gstatic.com` (the same checks Android and Chrome use for
Wi-Fi sign-in pages), and a TCP connection, opened and closed without sending anything, to `imap.gmail.com` and
`smtp.gmail.com` to see whether the network allows mail ports at all. The user's password goes only to the user's own mail
servers, over a verified encrypted connection. The hosts are listed in the window (`NetworkDoctorOptions.ContactedHostsText`)
and can be replaced in `NetworkDoctorOptions`.

### Provider-approved alternative SMTP

Restricted networks often close one submission port and leave the other open. When the outgoing server cannot be reached,
the Network Doctor tries:

- the ports the provider documents (Gmail 465/587, Yahoo 465/587, Fastmail 465/587; iCloud documents only 587, so nothing
  else is offered); and
- for other servers, the other standard submission port of the same server (465 ↔ 587), **offered only when the server
  itself accepted the sign-in on it** over a verified encrypted connection on this network.

Port 25 and unencrypted connections are never offered. Switching is the user's choice (*Use this*, after a confirmation); only
the outgoing port and security mode change, the password stays where it is, and it can be changed back in account settings.

## Automatic reconnection

`ConnectionRecoveryService` reconnects accounts after a connection problem (setting: *Reconnect automatically after a
connection problem*, on by default):

| Failure | Recovery |
| --- | --- |
| Network, DNS, timeout, throttling, unreachable revocation service, connection lost | Bounded exponential backoff: 5 s, 10 s, 20 s … up to 5 minutes, ±20 % jitter (`ReconnectPolicy`), and at once (after 2 s) when the network becomes available, changes or the computer wakes |
| Certificate or TLS failure (often a Wi-Fi sign-in page or an inspecting proxy) | Retried only when the network changes |
| Refused password, revoked sign-in, unexpected errors | Never retried automatically (repeated sign-ins can lock an account) |

While there is no network at all nothing is attempted. The account always shows what happens next (*Reconnecting
automatically at 14:05 (attempt 3)*, *Waiting for a network connection*, *HCG Mail does not retry a refused password by
itself*). This only repairs a failed connection: it does not poll for new mail. Manual refresh stays the default.

### Network transitions and sleep

`SystemNetworkMonitor` follows the operating system's network notifications (bursts during a Wi-Fi switch are combined into
one event) and detects wake-up from the clock (a 15-second check that finds minutes have passed). After a network change or
wake-up, connections that still look open are marked stale, so the next action reconnects at once instead of waiting for the
45-second command timeout. An IMAP connection unused for 3 minutes is checked with a short NOOP (10 s) before the next
command the user asked for: networks that drop idle connections silently no longer cause a long hang.

## Offline mailbox access

Every session is wrapped in `OfflineCapableMailSession`. Successful reads are copied to the device (`JsonMailCache`): the
folder list, the newest page of each opened folder and the messages that were opened (not attachments; inline images of very
large messages are left out; at most 400 messages per account).

When the server cannot be reached, the session goes offline: the folder and messages on screen stay, the message list shows
*Offline — showing mail saved on this device (from 14:02)* with the reason and the next reconnection, and reads are answered
at once from the copy. Changes (flags, delete, move), attachment downloads and opening server drafts fail immediately with a
clear message. Only a successful connection brings the session back online.

The copy lives in HCG Mail's private data folder (`offline/`, files readable only by the user on macOS and Linux, file names
are hashes so folder names and subjects do not appear in the file system). It is deleted when the account is removed, when
*Keep a copy of my mail on this computer for reading offline* is turned off, or with *Delete offline copies* in Settings.

## Outbox: persistent outgoing mail and drafts

A message goes to the Outbox only when it certainly did not reach the server: the account is offline, or the connection
failed before the server received it. Drafts saved while offline are kept on the device and copied to the Drafts folder on
reconnection. The Outbox is stored on disk (`outbox/`, one file per item, written atomically, attachments included so the
original files can be moved).

When the account reconnects, waiting items are handed to the server one at a time, oldest first. Each item is marked
*Sending* on disk **before** the attempt:

- accepted → removed, and the user is told;
- connection lost again before the server received it → stays *Waiting*;
- connection lost while the server was receiving it, or HCG Mail stopped mid-send → **Needs review**, never sent again
  automatically; the user checks Sent and chooses *Send again* or *Discard*;
- refused by the server (for example a rejected recipient) → *Failed*, kept for the user.

Outbox items are listed in Mail Health › Network Doctor with *Send again* and *Discard*.

## Optional HCG VPN

`IHcgVpn` is the connection point for the future HCG VPN service. This build registers `NoHcgVpn` (not installed). A host
application that ships HCG VPN registers its own implementation; HCG Mail then shows its state in the Network Doctor and
mentions it as one option when a network blocks mail. HCG Mail never installs, starts or downloads a VPN, never passes it
credentials or message content, and works exactly the same without it.

## Tests

- Engine (`tests/HCG.Mail.Tests/Network`): verdict matrix (school network, outage, wrong password, HCG Mail defect, captive
  portal, interception, revocation blocked, bad server certificate, blocked sending port with alternative, DNS), backoff and
  recovery modes, SMTP alternatives, offline session behaviour, cache privacy and bounds, Outbox persistence, sleep and
  transition detection, and the Network Doctor against simulated networks including a real in-process TLS server with an
  untrusted certificate (the password never crosses it).
- UI (`tests/HCG.Mail.App.Tests/NetworkRecoveryTests.cs`): offline banner and copy, Outbox, backoff on a manual clock, recovery
  on network return and wake, refused password not retried, settings respected, Network Doctor tab; Outbox and offline
  compose guarantees (no loss, no duplicate send).
- End-to-end with the local Dovecot/Postfix server (`RestrictedNetworkE2ETests`, `HCG_MAIL_E2E=1`): Wi-Fi drop with offline
  reading and Outbox delivery exactly once after recovery, a dead connection after sleep replaced at once, a silently dead
  idle connection detected within seconds, and the Network Doctor telling a filtering firewall, closed mail ports, an outage
  and a wrong password apart on the real server.

## Images

`docs/images/1.0.0-beta.2/network/beta2-offline-banner.png` and `beta2-network-doctor.png` are rendered by the headless UI
tests (`NetworkRecoveryTests`) with development-preview sample data and simulated network failures. They are not
screenshots of a Windows or macOS window.
