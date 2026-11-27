# HCG Mail for macOS: Developer ID signing and notarization

This is how HCG Mail for macOS is signed, notarized and checked before HCG Store distributes it. It also lists
exactly what the owner sets up on their MacBook.

An ad-hoc signature is not a Developer ID signature, and it is not notarization. A customer never has to:
- remove the quarantine attribute;
- right-click › Open;
- choose "Open Anyway";
- turn off Gatekeeper.

If Gatekeeper complains about a file, that file is not a release.

## What was wrong (audit of 1.0.0-beta.1)

| Finding | Effect |
|---|---|
| The app carried only an **ad-hoc** signature: no Developer ID, no team, not notarized | Gatekeeper rejects it on every Mac but the one that built it |
| `Contents/MacOS` held **250 files, about 230 of them .NET assemblies and JSON files** (not code) | codesign can sign such files only in **extended attributes**. ZIP tools, installer payloads and file transfers drop those, so the ZIP and PKG arrived with a broken seal, which macOS reports as "damaged" |
| HCG Executable Maker (`595e989`) leaves the bundle **unsealed**: only the .NET app host has the SDK's ad-hoc signature | `codesign --verify --deep` fails ("code has no resources but signature indicates they must be present") |
| No hardened runtime and no secure timestamp | Apple's notary service rejects both |

## What changed (1.0.0-beta.2 for macOS)

- **Code only in `Contents/MacOS`.**
  - On macOS, HCG Mail is published as a single-file app: one Mach-O host with the managed code inside, plus 3 native libraries (`libAvaloniaNative`, `libSkiaSharp`, `libHarfBuzzSharp`).
  - Each of the 4 files carries its signature inside itself, so the signature survives ZIP, PKG, DMG and any copy.
  - Native libraries are not self-extracted at run time. Extracted copies would not carry HCG's Developer ID, and the hardened runtime would refuse them.
  - Windows and Linux builds are unchanged.
  - Source: `src/HCG.Mail.App/HCG.Mail.App.csproj`.
- **Hardened runtime** on every file, with one entitlement, `com.apple.security.cs.allow-jit`, because the .NET runtime compiles code just in time. Its evidence is in `packaging/macos/HCGMail.entitlements`. No App Sandbox, no library-validation exception, no debugging entitlement.
- **Signing, notarization and the distribution files:** `scripts/macos/sign-and-notarize.sh`.
  - It signs inside out with a secure timestamp: libraries first, then the bundle with the entitlements.
  - It verifies the result and notarizes the app, then staples it.
  - It builds the three distribution files and notarizes and staples the PKG and the DMG separately:

    | File | Contents | Signed with |
    |---|---|---|
    | ZIP | the stapled app | Developer ID Application (the app) |
    | PKG | installs exactly `/Applications/HCG Mail.app`; no scripts; not relocatable | Developer ID Installer |
    | DMG | the app and an Applications link | Developer ID Application |

  - It runs Gatekeeper (`spctl`, `syspolicy_check`) and writes `signing-evidence-osx-<arch>.json`.
- **Download check:** `scripts/macos/verify-distribution.sh`.
  - Each file gets the quarantine attribute a browser sets, and it is never removed.
  - Gatekeeper must answer `accepted, source=Notarized Developer ID`.
  - Then the file is installed (PKG with `installer`, ZIP extracted, DMG mounted and copied), and HCG Mail is started, checked and restarted.
- **CI:** `.github/workflows/macos-release.yml`.
  - On real Apple silicon and Intel Macs, the release commands run with an ad-hoc identity, and each test file is installed and run under the hardened runtime. This proves the layout and the entitlements without a certificate. Those test files are named `…-adhoc-TEST-not-for-distribution` and are never released.
  - Executable Maker's **Signing Doctor** (`--mac-signing-doctor`, Maker `431c8b3`) reports on the result.
  - When the owner's signing secrets exist, the same script signs with Developer ID, notarizes and staples.
  - The download check then runs on both architectures, and `scripts/macos/macos-evidence.py` writes the release manifest, `SHA256SUMS.txt` and Release Passports.

## Verified so far (run 45942931119, commit `8bc1bc1`)

These results were produced without a certificate, from the release commands with an ad-hoc identity. They prove
the layout, the hardened runtime and the entitlements. They do not prove Developer ID signing or notarization: those
need your certificates.

| Check | Apple silicon (macOS 15, `macos-15`) | Intel (`macos-15-intel`) |
|---|---|---|
| `Contents/MacOS` | 4 files, all Mach-O | 4 files, all Mach-O |
| Signature seals the whole bundle (`codesign --verify --deep --strict`) | passed | passed |
| Hardened runtime on the app and on each of the 3 libraries; entitlements exactly `allow-jit` | passed | passed |
| ZIP, DMG and PKG (PKG installed into `/Applications` by `installer`, root-owned, no scripts) | installed, seal intact | installed, seal intact |
| HCG Mail started, captured on screen, quit and restarted under the hardened runtime; Keychain round trip; TLS refusal of bad certificates | passed from all three files | passed from all three files |
| Gatekeeper | rejected, as expected without Developer ID | rejected, as expected |
| Executable Maker Signing Doctor (`431c8b3`) | ad-hoc, hardened runtime on, `allow-jit` the only exception, strict verification passes, no ticket | — |

The Developer ID job reported **not configured**, so nothing was signed with Developer ID or notarized. The unsigned
signing input is the run's artifact `hcg-mail-macos-signing-input-1.0.0-beta.2`.

## What you need from Apple

| # | What | Where | Notes |
|---|---|---|---|
| 1 | **Apple Developer Program membership** (organization or individual), with you as **Account Holder** | developer.apple.com/programs | Only the Account Holder can create Developer ID certificates. |
| 2 | Your **Team ID** (10 characters) | developer.apple.com/account › Membership details | Used to check that every signature is your team's. |
| 3 | **Developer ID Application** certificate | Created on your MacBook (below) | Signs the app, its libraries and the DMG. A team can have up to five. |
| 4 | **Developer ID Installer** certificate | Created on your MacBook (below) | Signs the `.pkg`. Signing a package with the Application certificate looks like it works but fails on customers' Macs. |
| 5 | **Notarization credential:** an App Store Connect **Team** API key with the **Developer** role | appstoreconnect.apple.com › Users and Access › Integrations › App Store Connect API › Team Keys | Gives you a Key ID, an Issuer ID and `AuthKey_<KeyID>.p8`, which can be downloaded **once**. Individual keys cannot notarize. An Apple ID with an app-specific password also works, but the API key is preferred. |

You do **not** need these for HCG Store:
- a Mac App Store certificate (Apple Distribution, Mac Installer Distribution);
- a provisioning profile;
- the App Sandbox.

### On your MacBook (once)

1. Install **Xcode** from the App Store, current version, and open it once. The Command Line Tools also work. `xcrun notarytool` and `xcrun stapler` must exist.
2. Create the two certificates. Their private keys are generated in your login keychain on this MacBook and never leave it.
   - Xcode › Settings › Accounts › your Apple ID › your team › **Manage Certificates…** › **+** › **Developer ID Application**.
   - Repeat for **Developer ID Installer**.
   - Equivalent: Keychain Access › Certificate Assistant › Request a Certificate From a Certificate Authority, then upload the request at developer.apple.com › Certificates › + › Developer ID (G2 sub-CA).
3. Store the notarization credential in your keychain. Then move the `.p8` file to your password manager or delete it.
   ```bash
   xcrun notarytool store-credentials "hcg-notary" \
     --key ~/Downloads/AuthKey_<KeyID>.p8 --key-id <KeyID> --issuer <Issuer ID>
   ```
4. Check everything (read only):
   ```bash
   "HCG Mail Project/Desktop/scripts/macos/sign-and-notarize.sh" --check-setup --notary-profile hcg-notary
   ```
   It must list one `Developer ID Application: … (<Team ID>)` and one `Developer ID Installer: … (<Team ID>)` identity, and `Notarization profile 'hcg-notary': works`.
5. **Back up** both identities with their private keys.
   - Keychain Access › My Certificates › select both › Export › `.p12` with a strong password.
   - Keep the file offline or in your password manager.
   - If a key is lost, Apple lets you create a new certificate; files signed earlier stay valid.

## Signing a release

### A. On your MacBook: the keys stay on it

1. From the macOS release workflow run, download `hcg-mail-macos-signing-input-1.0.0-beta.2`. It holds the unsigned `.app` for each architecture, which is not for distribution.
2. Check the input files against `SHA256SUMS-signing-input.txt`:
   ```bash
   shasum -a 256 -c SHA256SUMS-signing-input.txt
   ```
3. Unpack the two apps:
   ```bash
   ditto -x -k HCGMail-1.0.0-beta.2-osx-arm64-unsigned-app.zip in-arm64
   ditto -x -k HCGMail-1.0.0-beta.2-osx-x64-unsigned-app.zip in-x64
   ```
4. Sign, notarize and build the files for each architecture:
   ```bash
   S="HCG Mail Project/Desktop/scripts/macos"
   for arch in arm64 x64; do
     "$S/sign-and-notarize.sh" --app "in-$arch/HCG Mail.app" --version 1.0.0-beta.2 --build-number 4 --arch $arch --out dist \
       --identity "Developer ID Application: <Name> (<Team ID>)" \
       --installer-identity "Developer ID Installer: <Name> (<Team ID>)" \
       --notary-profile hcg-notary
   done
   ```
5. Run the download check on your Mac for each file. Each must report "Download check passed".
   ```bash
   for f in dist/*.zip dist/*.dmg dist/*.pkg; do "$S/verify-distribution.sh" "$f" "verify/$(basename "$f")" --expect developer-id; done
   ```
   - Add `--install` for the `.pkg` to also install it into `/Applications`.
   - The checks use a separate data folder, so your own accounts are not touched.
6. Write the manifest and the passports:
   ```bash
   pip3 install --user jsonschema
   python3 "$S/macos-evidence.py" --dist dist --signing dist --verify verify --schema <hcg-repo>/docs/schemas/release-passport-v1.schema.json \
     --run-url "signed on the owner's Mac, $(date -u +%F)" --commit <commit> --out manifest
   ```
7. **Real customer test:** upload to HCG Store as a private draft, download the ZIP or PKG with Safari on your MacBook, and open it normally. The expected result is one standard confirmation that the app was downloaded from the Internet, and nothing else.

### B. In CI: GitHub environment `macos-signing`

This path puts encrypted copies of the two private keys in GitHub. Path A avoids that, so the choice is yours.

1. Repository › Settings › Environments › **New environment** `macos-signing`. Under Required reviewers, add yourself, so that each signing run waits for your approval.
2. Add the environment **secrets**:

   | Secret | Value |
   |---|---|
   | `MACOS_DEVELOPER_ID_APPLICATION_P12_BASE64` | output of `base64 -i DeveloperIDApplication.p12` |
   | `MACOS_DEVELOPER_ID_APPLICATION_P12_PASSWORD` | that `.p12`'s password |
   | `MACOS_DEVELOPER_ID_INSTALLER_P12_BASE64` | output of `base64 -i DeveloperIDInstaller.p12` |
   | `MACOS_DEVELOPER_ID_INSTALLER_P12_PASSWORD` | that `.p12`'s password |
   | `APPLE_NOTARY_API_KEY_P8_BASE64` | output of `base64 -i AuthKey_<KeyID>.p8` |
   | `APPLE_NOTARY_API_KEY_ID` | the Key ID |
   | `APPLE_NOTARY_API_ISSUER_ID` | the Issuer ID |

   Also add the **variable** `APPLE_TEAM_ID` (your Team ID).
3. Run **macOS release (Developer ID)** with Actions › Run workflow on the `macos/1.0.0-beta.2` branch, then approve the `macos-signing` step.
4. Each run uses a temporary keychain that is deleted at the end of the job. Nothing is ever printed.
5. The run produces:
   - `hcg-mail-macos-1.0.0-beta.2`: the files;
   - `hcg-mail-macos-verify-*`: the download checks;
   - `hcg-mail-macos-release-manifest`: the manifest, `SHA256SUMS.txt` and passports.

## Customers with an earlier build

- Passwords saved by an earlier build are bound to that build's ad-hoc signature. On first use, macOS asks once whether HCG Mail may use them; choose **Always Allow**.
- Later Developer ID builds of the same team keep that permission.
- Accounts, settings and messages are untouched. The data folder does not change.

## For Executable Maker (owner authorization needed before Maker is changed)

1. `MacAppBundlePackager` leaves the bundle unsealed. It should sign the whole bundle, at least ad-hoc.
2. For .NET apps, it copies the whole publish folder into `Contents/MacOS`. For distribution it should require a code-only layout, or move data files to `Contents/Resources`.
3. Its single-file option sets `IncludeNativeLibrariesForSelfExtract=true`. Under the hardened runtime, self-extracted native libraries are not loaded.

Maker's Developer ID signer, PKG packager and notarization service (`MacCodeSigner`, `MacPkgPackager`, `NotarizationService`) already follow this order. They are used from the native Xcode path, not from the .NET path.
