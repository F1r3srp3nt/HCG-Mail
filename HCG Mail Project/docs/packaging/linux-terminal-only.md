# HCG Mail — Linux Terminal Only

> Packaging notes, formerly `Linux Terminal Only/README.md` at the repository root. See the [index](README.md).

**Status: reserved — not started.** Nothing for this target has been built, packaged, signed or
submitted.

This plan covers the **terminal (command-line / text interface) edition of HCG Mail for Linux**.

## Planned responsibilities

| Area | Notes |
|---|---|
| Scope | A text-mode client is a different user interface from the desktop app; it must reuse the existing core, e-mail and infrastructure libraries, not reimplement mail handling |
| Security | Same rules as the desktop app: TLS validation never disabled, passwords in the Secret Service, remote content never fetched automatically |
| Packaging | Self-contained Linux binary produced by HCG Executable Maker |

## Relationship to the existing application

There is **one** HCG Mail implementation, in [`../../Desktop/`](../../Desktop/).
Builds for other operating systems are produced from that application with **HCG Executable Maker**;
this plan covers only what is specific to this distribution (terminal-specific notes and packaging). No separate email client is
developed here.

## Requirements before work can start (founder approval)

* Founder decision on the scope of the terminal edition (read-only, or full compose/send)
