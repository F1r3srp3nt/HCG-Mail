# Owner checklist: activating Microsoft and Google sign-in

The code for both providers is in place. Sign-in stays switched off ("not set up in this build") until HCG supplies
the values below. Nothing here was invented or pre-filled. Details: [MICROSOFT_ACCOUNTS.md](MICROSOFT_ACCOUNTS.md),
[GOOGLE_ACCOUNTS.md](GOOGLE_ACCOUNTS.md).

## Values HCG must supply

| Value | Where it comes from | Secret? | Where it goes |
|---|---|---|---|
| Microsoft **Application (client) ID** (GUID) | Entra admin center → App registrations → HCG Mail → Overview | No | `HcgMicrosoftClientId` in `oauth-clients.props` (may be committed), **or** env `HCG_MICROSOFT_CLIENT_ID` / GitHub variable `HCG_MICROSOFT_CLIENT_ID` |
| Google **client ID** (`…apps.googleusercontent.com`) | Google Cloud → Google Auth Platform → Clients → HCG Mail desktop | No | `HcgGoogleClientId` in `oauth-clients.props` (may be committed), **or** env `HCG_GOOGLE_CLIENT_ID` / GitHub variable `HCG_GOOGLE_CLIENT_ID` |
| Google **Desktop client secret** (`GOCSPX-…`) | same client | Not confidential for installed apps, but **never commit it** | env `HCG_GOOGLE_CLIENT_SECRET` / GitHub **secret** `HCG_GOOGLE_CLIENT_SECRET` / git-ignored `oauth-clients.local.props` |
| Privacy policy, terms and home page URLs on a verified HCG domain | HCG website | No | Entra *Branding*, Google *Branding* |

## Microsoft Entra (one registration for desktop and iOS)

- [ ] New registration `HCG Mail`, supported accounts: **any Entra tenant + personal Microsoft accounts**.
- [ ] Platform **Mobile and desktop applications**: redirect URI `http://localhost`.
- [ ] Platform **iOS / macOS**: bundle ID `com.hakmicapitalgroup.hcgmail`. This gives
      `msauth.com.hakmicapitalgroup.hcgmail://auth`.
- [ ] No Web/SPA platform, and no client secret or certificate.
- [ ] Delegated Microsoft Graph permissions: `User.Read`, `Mail.ReadWrite`, `Mail.Send`. No application permissions.
- [ ] Branding: logo, privacy and terms URLs. Publisher verification (recommended before outside users).
- [ ] Copy the client ID → put it in the build (table above).

## Google Cloud

- [ ] Project `hcg-mail` with two owners.
- [ ] **Gmail API** enabled.
- [ ] Branding:
  - [ ] app name, support e-mail and logo
  - [ ] home page, privacy policy and terms URLs
  - [ ] authorized domain verified in Search Console
- [ ] Audience: **External** (public) or **Internal** (HCG Workspace users only, no Google verification).
- [ ] Data access: `gmail.modify`, `openid`, `userinfo.email`, `userinfo.profile`. Nothing else.
- [ ] Client type **Desktop app** `HCG Mail desktop` (no redirect URI to enter). Copy the client ID and secret.
- [ ] While in Testing: add test users (max 100). Refresh tokens expire after **7 days**, so reconnecting weekly is
      expected.
- [ ] Before public release:
  - [ ] set the app to In production
  - [ ] pass brand verification
  - [ ] pass restricted-scope verification (justification, demo video, Limited Use privacy policy)
  - [ ] complete a CASA security assessment if Google requires one

## Builds

- [ ] **macOS desktop (the app in the screenshot):**
  - [ ] copy `oauth-clients.local.props.example` to `oauth-clients.local.props` and fill in the values
  - [ ] run `dotnet run --project src/HCG.Mail.App`
  - [ ] the build log shows `HCG020: … Microsoft: client ID …; Google: client ID …, client secret present`
- [ ] **GitHub release workflow:**
  - [ ] variables `HCG_MICROSOFT_CLIENT_ID` and `HCG_GOOGLE_CLIENT_ID`
  - [ ] secret `HCG_GOOGLE_CLIENT_SECRET`
- [ ] **HCG Executable Maker:**
  - [ ] set the same three environment variables in its build environment, or commit the two client IDs in
        `oauth-clients.props` and give Maker only `HCG_GOOGLE_CLIENT_SECRET`
  - [ ] check for `HCG020` in its log
- [ ] **iOS:**
  - [ ] Microsoft: `HCG_MICROSOFT_CLIENT_ID` for `scripts/export-ipa.sh`, or the committed ID
  - [ ] Google: not offered on iOS yet; no value needed

## Live verification (manual, with test mailboxes only)

- [ ] A dedicated Outlook.com test account and, if possible, a Microsoft 365 test user.
- [ ] A dedicated Gmail test account listed as a Google test user.
- [ ] Run the manual lists at the end of [MICROSOFT_ACCOUNTS.md](MICROSOFT_ACCOUNTS.md#testing-with-real-accounts-founder-manual)
      and [GOOGLE_ACCOUNTS.md](GOOGLE_ACCOUNTS.md#testing-with-real-accounts-founder-manual).
- [ ] Sending: only from a test account to your own test addresses.
- [ ] Record the results in [TESTING.md](TESTING.md). Until then, both providers count as "implemented, not tested with
      real accounts".
