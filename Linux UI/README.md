# HCG Mail — Linux UI

**Status: reserved — not started.** Nothing in this folder has been built, packaged, signed or
submitted.

This folder is reserved for the **graphical Linux desktop build of HCG Mail**.

## Planned responsibilities

| Area | Notes |
|---|---|
| Packaging | Self-contained Linux x64/arm64 build (AppImage, .deb or tarball) produced by HCG Executable Maker |
| Desktop integration | Desktop entry, icon, `mailto:` handler registration |
| Credentials | Secret Service (GNOME Keyring / KWallet) via libsecret |

## Relationship to the existing application

There is **one** HCG Mail implementation, in [`../Windows-executable-Application/`](../Windows-executable-Application/).
Builds for other operating systems are produced from that application with **HCG Executable Maker**;
this folder holds only what is specific to this distribution (desktop integration and packaging). No separate email client is
developed here.

## Requirements before work can start (founder approval)

* Choice of package formats and target distributions
