# Signing, notarizing and shipping HCG Mail for macOS with HCG Executable Maker (1.0.0-beta.2)

The exact order for the owner's Apple silicon Mac. It follows Executable Maker's own HCG Mail path
(`HCG-Executable-Maker/docs/HCG-MAIL-MACOS.md`) and its Signing Vault (`docs/SIGNING-VAULT.md`), filled in with HCG
Mail's values. Nothing here publishes: HCG Store keeps the upload as a private draft until you approve and publish it
there.

| Value | |
|---|---|
| Source | HCG Mail `main`, at the merge commit of beta.2 (check it with `git -C <checkout> rev-parse HEAD`) |
| Version | `1.0.0-beta.2` — Info.plist `CFBundleShortVersionString` **1.0.0**, `CFBundleVersion` **5** |
| Target | macOS, `osx-arm64` (Apple silicon) |
| Entitlements | exactly one: `com.apple.security.cs.allow-jit` (file `HCG Mail Project/Desktop/packaging/macos/HCGMail.entitlements`); no provisioning profile |
| Store file names | `HCG-Mail-1.0.0-beta.2-macos-arm64.zip`, `HCG-Mail-1.0.0-beta.2-macos-arm64.pkg` |
| Store channel / eligibility | beta / private |

## 0. Once: certificates and notarization credentials

1. **Signing Vault › Apple Signing › Import .p12…** — import the *Developer ID Application* and the *Developer ID
   Installer* identities. Each certificate and its private key go into the login keychain; the password is checked
   before anything is written.
2. **Project Signing:**
   - HCG Mail, macOS, mode **Manual**, route **macOS Developer ID**, your Team ID;
   - the Developer ID Application certificate and the Developer ID Installer certificate;
   - **Save settings**, then **Check signing**: every local check must pass. Revocation shows as *Not verified*; Apple checks it during notarization.
3. Notarization credentials are a keychain profile; Maker never sees them:
   ```bash
   xcrun notarytool store-credentials hcg-notary --key AuthKey_<KEYID>.p8 --key-id <KEYID> --issuer <ISSUER UUID>
   ```
4. Keep your own backup of both `.p12` files. The Signing Vault's encrypted private-key backup is not available yet.

## 1. Build (Executable Maker › New Build)

1. **Source.** Check out HCG Mail `main` at the beta.2 merge commit.
2. **New Build settings:**
   - project: the HCG Mail desktop project (`HCG Mail Project/Desktop/src/HCG.Mail.App`);
   - platform macOS, architecture arm64, formats `.app`, ZIP and PKG;
   - version `1.0.0-beta.2`.
3. **Distribution settings:**
   - Developer ID: the Application certificate and the Installer certificate;
   - entitlements confirmed as exactly `com.apple.security.cs.allow-jit`.
4. **Check the app before notarizing:**
   ```bash
   plutil -p "HCG Mail.app/Contents/Info.plist" | grep -E 'CFBundleShortVersionString|CFBundleVersion|CFBundleIdentifier'
   codesign --verify --deep --strict --verbose=2 "HCG Mail.app"
   codesign -d --entitlements - "HCG Mail.app"
   ```
   - The Info.plist must show 1.0.0 and build 5.
   - If the build number differs, fix it with `plutil -replace CFBundleVersion -string 5` on the unsigned app, then sign again.
   - Run Maker's **Signing Doctor** on the signed app: hardened runtime, Developer ID, timestamp, entitlements.

## 2. Notarize (on the build result)

**Notarize** with the keychain profile `hcg-notary`. Maker runs:
- `notarytool submit --wait`;
- `stapler staple` and `stapler validate`;
- a Gatekeeper check (`spctl -a -vv`) for the `.app` and the `.pkg`.

The target result is **accepted — Notarized Developer ID**. On a rejection Maker saves the notary log; fix the cause and
notarize again.

## 3. Re-zip the stapled app

The ZIP made during the build holds the app from **before** stapling. Re-create it from the stapled app:
```bash
ditto -c -k --keepParent "HCG Mail.app" HCG-Mail-1.0.0-beta.2-macos-arm64.zip
```
Rename the PKG to `HCG-Mail-1.0.0-beta.2-macos-arm64.pkg` if Maker named it differently. Rename it, never rebuild it:
renaming does not touch its signature or ticket.

## 4. Download check, as a customer

Download both files through Safari (or keep the quarantine attribute), then run HCG Mail's check on the Mac:
```bash
S="<HCG Mail checkout>/HCG Mail Project/Desktop/scripts/macos"
"$S/verify-distribution.sh" HCG-Mail-1.0.0-beta.2-macos-arm64.zip verify/zip --expect developer-id
"$S/verify-distribution.sh" HCG-Mail-1.0.0-beta.2-macos-arm64.pkg verify/pkg --expect developer-id --install
```
Both must print "Download check passed". The check covers Gatekeeper, installation, launch, and the runtime checks with a
separate data folder, so your own accounts are not touched.

## 5. Checksums and Release Passports

```bash
shasum -a 256 HCG-Mail-1.0.0-beta.2-macos-arm64.zip HCG-Mail-1.0.0-beta.2-macos-arm64.pkg > SHA256SUMS-macos.txt
HCGExecutableMaker --release-passport --app "HCG Mail.app" --file HCG-Mail-1.0.0-beta.2-macos-arm64.zip --source-repo <HCG Mail checkout>
HCGExecutableMaker --release-passport --app "HCG Mail.app" --file HCG-Mail-1.0.0-beta.2-macos-arm64.pkg --source-repo <HCG Mail checkout>
```
The passport records HCG Mail's own commit, read from the checkout; it is never guessed. Make the passports after
notarization, so they describe the final bytes.

## 6. Ship to Store (private draft)

1. Open **Distribution Studio › HCG Mail › Add New Update** (or **Add to Listed Product › macOS arm64**).
2. Enter version `1.0.0-beta.2`, build 5, channel **beta**, eligibility **private**.
3. Add the ZIP and the PKG with their passports, then **Upload private draft**.
4. Release notes and description: `docs/STORE_AND_NEWS_1.0.0-beta.2.md`, macOS edition.

The published Windows release is not touched.

## 7. Publish (by a person)

Review the draft in HCG Store's Control Center, download it once more as a customer would, then approve and publish
there. Executable Maker and HCG Mail never publish.

## Alternative: HCG Mail's own scripts

The same signing can be done without Maker's build step:
- run **macOS release (Developer ID)** with `signing: local-vault`; it builds, audits and qualifies the unsigned app on
  macOS runners and never asks for Apple credentials;
- then follow `docs/MACOS_SIGNING.md` § A with `scripts/macos/sign-and-notarize.sh`, which uses the same keychain
  identities and notary profile.

Use one path per release, not both.
