# Release capabilities

A release capability decides which *implemented* features a build offers. Disabling one hides it. It removes nothing.

| Capability | Public Beta (default) | Internal development build |
|---|---|---|
| IMAP and SMTP accounts | ✅ | ✅ |
| Microsoft accounts (Outlook.com, Hotmail, Microsoft 365: Microsoft sign-in + Microsoft Graph) | hidden | ✅ when a client ID is configured |
| Google accounts (Gmail, Google Workspace: Google sign-in + Gmail API) | hidden | ✅ when a client ID and secret are configured (desktop) |

HCG Mail 1.0 Public Beta (`1.0.0-beta.1`) is IMAP and SMTP only.

## Choosing the channel

The channel is fixed at build time:

```bash
dotnet build …                                  # Public Beta (the default)
dotnet build … -p:HcgReleaseChannel=Internal     # internal development build
HCG_RELEASE_CHANNEL=Internal dotnet build …      # the same, from the environment
```

Any other value counts as Public Beta, so a build fails closed. The value is compiled in: assembly metadata
`HcgReleaseChannel` on the desktop, the `HcgBuildInfo.ReleaseChannel` constant on iOS. It is read by
`HCG.Mail.Core.Release.ReleaseCapabilities`. There is no runtime switch: an environment variable on a user's PC cannot
turn Microsoft or Google sign-in on in a Public Beta build. The runtime client-ID variables
(`HCG_MAIL_MICROSOFT_CLIENT_ID`, …) only take effect in internal builds.

## What a Public Beta build does with Microsoft and Google

**Hidden:**
* the welcome card buttons and wording;
* Settings › Accounts (status rows and *Add* buttons);
* the account setup banners;
* the iPhone/iPad welcome, mailbox and Settings buttons.

The *Add Microsoft/Google account* commands do nothing even if invoked directly.

**Kept unchanged:**
* the OAuth implementations (MSAL, Google loopback + PKCE);
* the provider sessions (Graph, Gmail API), callback handling and the client-ID configuration (`oauth-clients.props`);
* tests and documentation (`MICROSOFT_ACCOUNTS.md`, `GOOGLE_ACCOUNTS.md`, `OAUTH_ACTIVATION_CHECKLIST.md`).

**Existing accounts:** a Microsoft or Google account added with an earlier or internal build:
* stays in `accounts.json` exactly as it was, and its tokens stay where they were. Nothing is deleted or converted;
* is listed with the explanation *"Microsoft accounts are not available in HCG Mail 1.0 Public Beta. This account and
  its settings are kept unchanged on this device…"*;
* does not connect: no sign-in is attempted, and no Microsoft or Google service is contacted;
* can be removed by the user (Settings › Accounts › Remove). That is the only way it is deleted, and only when the user
  chooses it. Removing a Google account also deletes its token from the credential store. A Microsoft account's sign-in
  stays in the protected token store, and the app says so: the Public Beta deliberately does not activate the
  Microsoft library that could delete it. An internal build signs it out, or remove the "HCG Mail Microsoft sign-in"
  entry (macOS Keychain) or `msal-token-cache.bin` (Windows, in the data folder).

### Migration path for existing Microsoft or Google accounts (internal development)

1. **Keep using the account:** run an internal build (`-p:HcgReleaseChannel=Internal`) with the same data folder
   (`%LOCALAPPDATA%\HCG\HCG Mail`, or `HCG_MAIL_DATA_DIR`). The account connects as before; its sign-in is still in
   the token cache or credential store.
2. **Move the mailbox to IMAP/SMTP in the Public Beta:** add the same address as an IMAP/SMTP account. Microsoft
   accounts work only where Microsoft still allows IMAP/SMTP with a password for that account. Gmail needs IMAP enabled
   and a Google *app password*, which requires 2-Step Verification. Once the new account works, remove the old one if
   you no longer need it. Mail on the server is never affected.
3. **Back to an internal build later:** nothing needs restoring. The old account entry is still there, unless you
   removed it.

## Tests

* `RealAccountModeTests.Public_beta_hides_microsoft_and_google_and_keeps_an_existing_microsoft_account_unchanged`: the
  account file is byte-for-byte unchanged.
* `FlowTests.Public_beta_never_offers_microsoft_sign_in_even_when_configured` (iPhone).
* `PlatformServiceTests.Public_beta_keeps_microsoft_sign_in_inactive_and_explains_why`.
* `OAuthConfigurationTests.Builds_are_public_beta_unless_internal_is_asked_for`.
* The existing Microsoft and Google tests cover internal builds.
