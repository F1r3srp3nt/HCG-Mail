# HCG Mail — Linux UI

> Packaging notes, formerly `Linux UI/README.md` at the repository root. See the [index](README.md).

**Status: reserved — not started.** Nothing for this target has been built, packaged, signed or
submitted.

This plan covers the **graphical Linux desktop build of HCG Mail**.

## Planned responsibilities

| Area | Notes |
|---|---|
| Packaging | Self-contained Linux x64/arm64 build (AppImage, .deb or tarball) produced by HCG Executable Maker |
| Desktop integration | Desktop entry, icon, `mailto:` handler registration |
| Credentials | Secret Service (GNOME Keyring / KWallet) via libsecret |

## Relationship to the existing application

There is **one** HCG Mail implementation, in [`../../Desktop/`](../../Desktop/).
Builds for other operating systems are produced from that application with **HCG Executable Maker**;
this plan covers only what is specific to this distribution (desktop integration and packaging). No separate email client is
developed here.

## Requirements before work can start (founder approval)

* Choice of package formats and target distributions
