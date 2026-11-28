# Shipping HCG Mail 1.0.0-beta.2 for macOS from the owner's Mac (HCG Executable Maker + HCG Mail's signing script)

The exact order for signing, notarizing and shipping the **already-built, CI-qualified** app on the owner's Apple silicon
Mac. Nothing is rebuilt, the downloaded signing input is never changed, and nothing is published: HCG Store keeps the
upload as a private draft until a person approves and publishes it there.

This page was checked against HCG Executable Maker `main` at `6ddd9683e1119b3a9fe5e6aea163f4b6219c1054` (code, not docs).
Every Maker step below names a control that exists in that version. Where Maker has no such step for this app, the page
says so and uses HCG Mail's own script instead.

| Value | |
|---|---|
| Source commit (the binary's provenance) | `6ddd667d9dc189e24f5d31a313a9ae96299d2dfa` (HCG Mail `main`, merge of PR #27) |
| Qualification run | macOS release (Developer ID) run `46007763971`, `signing: local-vault` — build audit, hardened runtime with test signing, install and launch, regression suites on macOS 15 arm64: all passed |
| Signing input | Actions artifact `hcg-mail-macos-signing-input-1.0.0-beta.2` (artifact ID `15201589154`, kept 90 days from 27 Nov 2026) |
| Unsigned app | `HCGMail-1.0.0-beta.2-osx-arm64-unsigned-app.zip`, SHA-256 `a6ffcc6b2f66a104d439d040387f66df0a7851003670374a2a97153443169b4e`, GitHub build attestation |
| Version | `1.0.0-beta.2` — Info.plist `CFBundleShortVersionString` **1.0.0**, `CFBundleVersion` **5**, `com.hakmicapitalgroup.hcgmail` |
| Entitlements | exactly one: `com.apple.security.cs.allow-jit`; no provisioning profile |
| Store files | `HCG-Mail-1.0.0-beta.2-macos-arm64.zip`, `HCG-Mail-1.0.0-beta.2-macos-arm64.pkg` |
| Store channel / eligibility | Beta / Private (publishers only) |

## What Executable Maker does and does not do for this app

| Step | In Maker today? | Used here |
|---|---|---|
| Import Developer ID certificates (.p12) into the login keychain | **Yes** — Signing & Security › Apple Signing › **Import .p12…** | Maker |
| Show each identity with its full SHA-1, team and expiry | **Yes** — Apple Signing › Signing identities | Maker |
| Project Signing (route *macOS Developer ID*, Check signing) | **Yes, as a saved record and a check only.** No macOS desktop build reads it (only iOS device builds do) | Maker, optional check |
| Notarization keychain profile: store the name, *Check profile* | **Yes** — Settings › Apple credentials | Maker |
| Import an already-built `.app` as a build | **No.** New Build always builds from source; Build History and the Artifact Vault list only Maker's own builds | — |
| Developer ID signing of a **.NET** app | **No.** New Build › Distribution marks Developer ID *not applicable* for .NET ("NET apps stay ad-hoc signed in this version"); it is offered for Swift GUI apps only | HCG Mail script |
| Signed installer package (.pkg) for a .NET app | **No.** Unsigned test packages only for .NET | HCG Mail script |
| Notarize / staple / Gatekeeper (Results page › *Notarize with Apple…*) | **Only for a Maker build made with Developer ID signing**, so not for this app | HCG Mail script |
| Signing Doctor on any `.app` | **Yes, command line only** (`--mac-signing-doctor`); no page in the app | Maker CLI |
| Release Passport for an existing `.app` + ZIP/PKG | **Yes, command line only** (`--release-passport`) | Maker CLI |
| Ship to Store with local files, private draft | **Yes** — Ship to Store › 6 · Artifacts › **Choose files…** | Maker |
| Encrypted private-key backup in the Signing Vault | **No** (account backup stores metadata only) | your own `.p12` backup |

Gaps that matter for this release are listed at the end of this page.

## 0. Once: certificates, notarization profile, Maker version

1. **Maker version.** Use HCG Executable Maker built from `main` at `6ddd968` or later. Check in Terminal:
   ```bash
   M="/Applications/HCG Executable Maker.app/Contents/MacOS/HCGExecutableMaker"
   "$M" --version
   ```
2. **Certificates.** Maker › **Signing & Security** › **Apple Signing** › **Import .p12…**:
   - pick the *Developer ID Application* `.p12`, enter its export password; repeat for *Developer ID Installer*;
   - under *Where imported assets go*, keep **Import to this Mac (default)**. The other option records metadata only;
   - **Refresh**: both identities appear under *Signing identities*, each with **SHA-1 …**, team and expiry. Note both
     SHA-1 values; the script below takes them.
3. **Optional record and check.** In the same page, *Project Signing*: Project **HCG Mail** (it must be in Maker's
   Projects list), Platform **macOS**, Signing mode **Manual**, Route **macOS Developer ID (direct distribution)**, your
   Team ID, Certificate = the Developer ID Application identity, Installer certificate = the Developer ID Installer
   identity, no provisioning profile. **Save settings**, then **Check signing**. Revocation shows as *Not verified*;
   Apple checks it during notarization. This record is not used by any step below; it is a second check of the keychain.
4. **Notarization profile** (Maker never sees the key or password). In Terminal, with an App Store Connect API key:
   ```bash
   xcrun notarytool store-credentials hcg-notary --key AuthKey_<KEYID>.p8 --key-id <KEYID> --issuer <ISSUER UUID>
   ```
   Then Maker › **Settings** › **Apple credentials** › *Notarization (notarytool keychain profile)*: profile `hcg-notary`,
   **Check profile** (runs `notarytool history`).
5. Keep your own backup of both `.p12` files.

## 1. Get the signing input without changing it

1. Download `hcg-mail-macos-signing-input-1.0.0-beta.2` from run 46007763971 (GitHub › Actions › the run ›
   *Artifacts*). Unzip it into a new folder, e.g. `~/hcg-mail-beta2/input`. It contains
   `signing-input/HCGMail-1.0.0-beta.2-osx-arm64-unsigned-app.zip`, `signing-input/SHA256SUMS-signing-input.txt` and
   `package.log`.
2. Check the bytes, and optionally the GitHub attestation:
   ```bash
   cd ~/hcg-mail-beta2/input/signing-input
   shasum -a 256 -c SHA256SUMS-signing-input.txt     # must print OK
   shasum -a 256 HCGMail-1.0.0-beta.2-osx-arm64-unsigned-app.zip
   # a6ffcc6b2f66a104d439d040387f66df0a7851003670374a2a97153443169b4e
   gh attestation verify HCGMail-1.0.0-beta.2-osx-arm64-unsigned-app.zip --repo F1r3srp3nt/HCG-Mail   # optional
   ```
3. Unpack a working copy; the downloaded ZIP stays as it is:
   ```bash
   cd ~/hcg-mail-beta2 && ditto -x -k input/signing-input/HCGMail-1.0.0-beta.2-osx-arm64-unsigned-app.zip unsigned
   plutil -p "unsigned/HCG Mail.app/Contents/Info.plist" | grep -E 'CFBundleShortVersionString|CFBundleVersion|CFBundleIdentifier'
   # 1.0.0 · 5 · com.hakmicapitalgroup.hcgmail
   ```
4. A clean HCG Mail checkout at the source commit (for the script and for the passports' source commit):
   ```bash
   git clone https://github.com/F1r3srp3nt/HCG-Mail.git ~/hcg-mail-beta2/src
   git -C ~/hcg-mail-beta2/src checkout --detach 6ddd667d9dc189e24f5d31a313a9ae96299d2dfa
   git -C ~/hcg-mail-beta2/src status --short        # must print nothing
   ```

## 2. Sign, notarize, staple, package and check Gatekeeper (HCG Mail's script)

Maker cannot Developer ID–sign or notarize a .NET app it did not build (table above), so this step uses
`scripts/macos/sign-and-notarize.sh`. It uses the identities you imported with Maker, by SHA-1, from the login keychain,
and the `hcg-notary` profile. It copies the input `.app` before touching it.

```bash
cd ~/hcg-mail-beta2
S="$PWD/src/HCG Mail Project/Desktop/scripts/macos"
"$S/sign-and-notarize.sh" --check-setup --notary-profile hcg-notary     # read only: both identities and the profile must be listed
"$S/sign-and-notarize.sh" --app "unsigned/HCG Mail.app" --version 1.0.0-beta.2 --build-number 5 --arch arm64 --out dist \
  --identity <Developer ID Application SHA-1> --installer-identity <Developer ID Installer SHA-1> \
  --notary-profile hcg-notary \
  --zip-name HCG-Mail-1.0.0-beta.2-macos-arm64.zip --pkg-name HCG-Mail-1.0.0-beta.2-macos-arm64.pkg
```

It checks the version (Info.plist 1.0.0 / build 5, the app's own version 1.0.0-beta.2), then signs every Mach-O with the
hardened runtime, a secure timestamp and the one entitlement; builds the PKG (no scripts, `/Applications`) and signs it
with the Installer identity; submits the app and the PKG to Apple (`notarytool submit --wait`), staples and validates
both; re-zips the **stapled** app; and runs Gatekeeper (`spctl`, `syspolicy_check`). It stops at the first failure.
Result in `dist/`: the two Store files, `signing-evidence-osx-arm64.json`, `SHA256SUMS-osx-arm64.txt`, `logs/`.

## 3. Download check, as a customer

```bash
"$S/verify-distribution.sh" dist/HCG-Mail-1.0.0-beta.2-macos-arm64.zip verify/zip --expect developer-id
"$S/verify-distribution.sh" dist/HCG-Mail-1.0.0-beta.2-macos-arm64.pkg verify/pkg --expect developer-id --install
```
Both must print "Download check passed": quarantine kept, Gatekeeper "accepted, source=Notarized Developer ID", install
(`--install` asks for your administrator password and replaces `/Applications/HCG Mail.app`), and launch with a separate
data folder, so your own accounts are not touched.

## 4. Signing Doctor and Release Passports (Maker, command line)

The passports must describe the final bytes, so they come after notarization. Use the stapled app from the final ZIP:

```bash
cd ~/hcg-mail-beta2
ditto -x -k dist/HCG-Mail-1.0.0-beta.2-macos-arm64.zip final
"$M" --mac-signing-doctor "final/HCG Mail.app"        # Developer ID, hardened runtime, timestamp, entitlements, notarization, Gatekeeper
for f in dist/HCG-Mail-1.0.0-beta.2-macos-arm64.zip dist/HCG-Mail-1.0.0-beta.2-macos-arm64.pkg; do
  "$M" --release-passport --app "final/HCG Mail.app" --file "$f" --source-repo src --build-number 5
done
```
Each writes `<file>.release-passport.json` next to the file, which is where Ship to Store looks for it. Check that each
prints `source: 6ddd667d9dc189e24f5d31a313a9ae96299d2dfa` (no "uncommitted changes"), and that its `code signing:` line
names Developer ID, Gatekeeper accepted and the notarization. Do not move or rename the files after this step.

## 5. Ship to Store: private draft (Maker › Ship to Store)

1. **1 · Connection** › **Check connection** (sign in with **Account…** if asked).
2. **2 · Products you may manage** › select **HCG Mail**.
3. **3 · Operation**:
   - **Add New Update** — unless a 1.0.0-beta.2 draft of HCG Mail is already open in HCG Store (for example the Windows
     files uploaded as a draft); then **Add to Listed Product** and select **that draft**.
   - Never select the published 1.0.0-beta.1 release: Maker would prepare a new revision of it.
4. **5 · Release configuration**: Version `1.0.0-beta.2`, Build number `5`, Channel **Beta**, Release notes (one per line)
   from `docs/STORE_AND_NEWS_1.0.0-beta.2.md` (macOS edition), Requested eligibility **Private (publishers only)**. Leave
   *Make this the current release…* and *Supersede the previous compatible release…* **unticked**, so the published
   Windows beta.1 stays as it is.
5. **6 · Artifacts** › **Choose files…** › `dist/HCG-Mail-1.0.0-beta.2-macos-arm64.zip`; then **Choose files…** again ›
   the `.pkg` (one file per pick). Each row must show macOS arm64, SHA-256, Developer ID signing, notarization, and the
   build provenance read from its passport.
6. Expect one warning per file: *"Discrepancy: … Release Passport states version 1.0.0; entered 1.0.0-beta.2."* The
   passport takes the version from Info.plist, which Apple limits to numbers (see gaps). Confirm it; the note is sent with
   the draft so reviewers see why.
7. **7 · Upload to private staging** › **Upload private draft** (Retry resumes). Stop here. *Submit for review…* is a
   separate decision; nothing is published by either.

## 6. Publish (a person, in HCG Store)

Review the draft in HCG Store's Control Center, download it once with Safari as a customer would, then approve and
publish there. Maker and HCG Mail never publish.

## Gaps in HCG Executable Maker found while checking this workflow

1. **No Developer ID signing for .NET apps** (`MacDistributionCatalog.DeveloperId`: *not applicable*; only the Swift GUI
   packager signs with Developer ID). Maker's own `docs/HCG-MAIL-MACOS.md` says it is implemented; the code says it is not.
2. **No import of an already-built `.app`.** Signing, Notarize, staple and Test install act only on Maker's own
   `BuildRecord`s (`NotarizationService.Eligible` requires a Developer ID build).
3. **No signed `.pkg` for .NET apps** (unsigned test packages only).
4. **Signing Doctor and Release Passport are command-line only** for this case; there is no page for them.
5. **Passport version for pre-releases:** `--release-passport` records `CFBundleShortVersionString` (1.0.0) and has no
   option for the release version, so Distribution Studio reports a version discrepancy for every beta.
6. **Project Signing's macOS Developer ID route is not read by any macOS build**; it is a saved record and a check.
7. **Signing Vault's encrypted private-key backup is not implemented.**
8. The Apple Signing page still shows an old roadmap note: "No signing operation is implemented in M0.1".

HCG Mail's script closes gaps 1–3 for this release. Changing Executable Maker needs a separate, authorized change.
