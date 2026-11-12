# HCG Mail — iOS Executable

**Status: reserved — not started.** Nothing in this folder has been built, packaged, signed or
submitted.

This folder is reserved for the **directly installable iOS build of HCG Mail (TestFlight / ad-hoc / enterprise distribution)**.

## Planned responsibilities

| Area | Notes |
|---|---|
| Packaging | Signed `.ipa` produced by HCG Executable Maker |
| Credentials | iOS Keychain for passwords and Microsoft tokens |
| Distribution | TestFlight or ad-hoc devices — never uploaded automatically |

## Relationship to the existing application

There is **one** HCG Mail implementation, in [`../Windows-executable-Application/`](../Windows-executable-Application/).
Builds for other operating systems are produced from that application with **HCG Executable Maker**;
this folder holds only what is specific to this distribution (device configuration, distribution notes). No separate email client is
developed here.

## Requirements before work can start (founder approval)

* Apple Developer Program membership and device / TestFlight setup
* Signing identity held outside the repository
