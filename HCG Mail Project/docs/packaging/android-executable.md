# HCG Mail — Android Executable

> Packaging notes, formerly `Android Executable/README.md` at the repository root. See the [index](README.md).

**Status: reserved — not started.** Nothing for this target has been built, packaged, signed or
submitted.

This plan covers the **directly installable Android build of HCG Mail (APK/AAB outside Google Play)**.

## Planned responsibilities

| Area | Notes |
|---|---|
| Packaging | Signed APK/AAB produced by HCG Executable Maker |
| Credentials | Android Keystore-backed storage for passwords and Microsoft tokens |
| Distribution | Sideloading or internal distribution — never uploaded automatically |

## Relationship to the existing application

There is **one** HCG Mail implementation, in [`../../Desktop/`](../../Desktop/).
Builds for other operating systems are produced from that application with **HCG Executable Maker**;
this plan covers only what is specific to this distribution (device configuration, distribution notes). No separate email client is
developed here.

## Requirements before work can start (founder approval)

* Release signing key held outside the repository
* Decision on distribution channel
