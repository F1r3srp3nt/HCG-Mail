# HCG Mail — Linux Terminal Only

**Status: reserved — not started.** Nothing in this folder has been built, packaged, signed or
submitted.

This folder is reserved for the **terminal (command-line / text interface) edition of HCG Mail for Linux**.

## Planned responsibilities

| Area | Notes |
|---|---|
| Scope | A text-mode client is a different user interface from the desktop app; it must reuse the existing core, e-mail and infrastructure libraries, not reimplement mail handling |
| Security | Same rules as the desktop app: TLS validation never disabled, passwords in the Secret Service, remote content never fetched automatically |
| Packaging | Self-contained Linux binary produced by HCG Executable Maker |

## Relationship to the existing application

There is **one** HCG Mail implementation, in [`../Windows-executable-Application/`](../Windows-executable-Application/).
Builds for other operating systems are produced from that application with **HCG Executable Maker**;
this folder holds only what is specific to this distribution (terminal-specific notes and packaging). No separate email client is
developed here.

## Requirements before work can start (founder approval)

* Founder decision on the scope of the terminal edition (read-only, or full compose/send)
