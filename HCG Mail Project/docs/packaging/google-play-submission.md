# HCG Mail — Google Play Submission

> Packaging notes, formerly `Google Play Submission/README.md` at the repository root. See the [index](README.md).

**Status: reserved — not started.** Nothing for this target has been built, packaged, signed or
submitted.

This plan covers the **Google Play distribution of HCG Mail for Android**.

## Planned responsibilities

| Area | Notes |
|---|---|
| Play Console | App listing, content rating, Data safety form for e-mail data, target API level requirements |
| Signing | Play App Signing; the upload key is kept outside the repository, never committed |
| Assets | Launcher icons, feature graphic and screenshots from the official HCG Mail icon |
| Release tracks | Internal testing → closed → production; nothing is published automatically |

## Relationship to the existing application

There is **one** HCG Mail implementation, in [`../../Desktop/`](../../Desktop/).
Builds for other operating systems are produced from that application with **HCG Executable Maker**;
this plan covers only what is specific to this distribution (store metadata, Data safety answers, release notes). No separate email client is
developed here.

## Requirements before work can start (founder approval)

* Google Play Console developer account for Hakmi Capital Group
* Reserved package name (application id)
* Privacy policy URL
