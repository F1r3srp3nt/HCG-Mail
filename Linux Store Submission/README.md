# HCG Mail — Linux Store Submission

**Status: reserved — not started.** Nothing in this folder has been built, packaged, signed or
submitted.

This folder is reserved for the **Linux store distribution of HCG Mail (e.g. Flathub, Snap Store)**.

## Planned responsibilities

| Area | Notes |
|---|---|
| Store packaging | Flatpak manifest / snapcraft.yaml, AppStream metadata, desktop entry |
| Sandbox permissions | Network access for mail servers; Secret Service (libsecret) for credentials |
| Assets | Icons and screenshots from the official HCG Mail icon |

## Relationship to the existing application

There is **one** HCG Mail implementation, in [`../Windows-executable-Application/`](../Windows-executable-Application/).
Builds for other operating systems are produced from that application with **HCG Executable Maker**;
this folder holds only what is specific to this distribution (store manifests and metadata). No separate email client is
developed here.

## Requirements before work can start (founder approval)

* Choice of store(s) and publisher account
* Application id (reverse-DNS name)
* Privacy policy URL
