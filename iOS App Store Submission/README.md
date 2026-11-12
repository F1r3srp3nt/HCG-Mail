# HCG Mail — iOS App Store Submission

**Status: reserved — not started.** Nothing in this folder has been built, packaged, signed or
submitted.

This folder is reserved for the **App Store distribution of HCG Mail for iPhone and iPad**.

## Planned responsibilities

| Area | Notes |
|---|---|
| App Store Connect | App record, bundle identifier, App Store listing, age rating, privacy nutrition labels for e-mail data |
| Signing | Apple Distribution certificate and provisioning profiles — kept outside the repository, never committed |
| Review | App Review guidelines (account deletion, data use disclosure, Sign in with Apple only if third-party sign-in is offered) |
| Assets | App icons and screenshots generated from the official HCG Mail icon |

## Relationship to the existing application

There is **one** HCG Mail implementation, in [`../Windows-executable-Application/`](../Windows-executable-Application/).
Builds for other operating systems are produced from that application with **HCG Executable Maker**;
this folder holds only what is specific to this distribution (store metadata, entitlements, review notes). No separate email client is
developed here.

## Requirements before work can start (founder approval)

* Apple Developer Program membership for Hakmi Capital Group
* Reserved bundle identifier and app name
* Privacy policy URL
