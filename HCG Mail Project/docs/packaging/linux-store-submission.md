# HCG Mail — Linux Store Submission

> Packaging notes, formerly `Linux Store Submission/README.md` at the repository root. See the [index](README.md).

**Status: reserved — not started.** Nothing for this target has been built, packaged, signed or
submitted.

This plan covers the **Linux store distribution of HCG Mail (e.g. Flathub, Snap Store)**.

## Planned responsibilities

| Area | Notes |
|---|---|
| Store packaging | Flatpak manifest / snapcraft.yaml, AppStream metadata, desktop entry |
| Sandbox permissions | Network access for mail servers; Secret Service (libsecret) for credentials |
| Assets | Icons and screenshots from the official HCG Mail icon |

## Relationship to the existing application

There is **one** HCG Mail implementation, in [`../../Desktop/`](../../Desktop/).
Builds for other operating systems are produced from that application with **HCG Executable Maker**;
this plan covers only what is specific to this distribution (store manifests and metadata). No separate email client is
developed here.

## Requirements before work can start (founder approval)

* Choice of store(s) and publisher account
* Application id (reverse-DNS name)
* Privacy policy URL
