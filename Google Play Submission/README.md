# HCG Mail — Google Play Submission

**Status: reserved — not started.** Nothing in this folder has been built, packaged, signed or
submitted.

This folder is reserved for the **Google Play distribution of HCG Mail for Android**.

## Planned responsibilities

| Area | Notes |
|---|---|
| Play Console | App listing, content rating, Data safety form for e-mail data, target API level requirements |
| Signing | Play App Signing; the upload key is kept outside the repository, never committed |
| Assets | Launcher icons, feature graphic and screenshots from the official HCG Mail icon |
| Release tracks | Internal testing → closed → production; nothing is published automatically |

## Relationship to the existing application

There is **one** HCG Mail implementation, in [`../Windows-executable-Application/`](../Windows-executable-Application/).
Builds for other operating systems are produced from that application with **HCG Executable Maker**;
this folder holds only what is specific to this distribution (store metadata, Data safety answers, release notes). No separate email client is
developed here.

## Requirements before work can start (founder approval)

* Google Play Console developer account for Hakmi Capital Group
* Reserved package name (application id)
* Privacy policy URL
