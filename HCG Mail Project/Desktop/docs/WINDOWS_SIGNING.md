# HCG Mail for Windows: Authenticode signing (prepared for the next Windows update)

Status: **prepared, not active.** 1.0.0-beta.1 for Windows is unsigned, and its files (run 45935461816) are not changed.
Signing starts with the next Windows version. Signing changes the file bytes, so it always means a new version with new
checksums and passports. A published file is never re-signed in place.

## What gets signed

- `HCGMail.exe`, the program people start.
- HCG Mail's own assemblies (`HCG.Mail.*.dll`).
- The installer `HCGMail-<version>-win-<arch>-Setup.exe`.

Third-party and .NET runtime files keep their publishers' signatures.

Each signature:
- uses SHA-256;
- has an RFC 3161 timestamp, so it stays valid after the certificate expires;
- is checked afterwards by HCG Executable Maker's Windows Signing Doctor (`--windows-signing-doctor`) and by Windows itself (`Get-AuthenticodeSignature`).

The order matters:
1. Sign the program.
2. Build the installer around the signed program: HCG Executable Maker `--package-windows --app-dir` with an already-built folder.
3. Sign the installer.
4. Build the portable ZIP from the signed program.

Maker's Authenticode signer signs only after an explicit approval tied to the SHA-256 of the exact file (`--authenticode-sign … --approve <sha256>`). It uses a certificate named by thumbprint, holds no keys and never imports certificates.

## What you need to decide: where the private key lives

Since June 2023, publicly trusted code-signing keys must be kept in certified hardware (an HSM or a token). A `.pfx` file
from a certificate authority is no longer issued. That leaves three options:

| Option | Works in GitHub Actions | What you get |
|---|---|---|
| **Microsoft Artifact Signing** (formerly Trusted Signing), an Azure service | Yes, with Microsoft's GitHub action, through an Azure identity with no stored key | Microsoft validates your identity and issues short-lived certificates. It is billed monthly. Eligibility depends on organization and country, so check it on Microsoft's Artifact Signing page first. |
| **An OV or EV certificate with cloud key storage** from a certificate authority (for example DigiCert KeyLocker, SSL.com eSigner) | Yes, with the provider's signing tool and an API credential | A certificate in your organization's name. The key stays in the provider's HSM. |
| **An OV or EV certificate on a USB token** | No: only on a Windows PC with the token plugged in | Signing by hand on that PC with Maker's `--authenticode-sign`. It does not fit a MacBook-only setup. |

SmartScreen reputation builds up as signed files are downloaded. A new certificate can still show "Windows protected your PC" for a while, so plan the first signed release with that in mind.

## What will be added once you choose

- **Artifact Signing:**
  - In Azure: an Artifact Signing account, an identity validation, a certificate profile, and an app registration with federated credentials for this repository. The workflow then needs no stored secret.
  - Repository variables: `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_SUBSCRIPTION_ID`, `ARTIFACT_SIGNING_ENDPOINT`, `ARTIFACT_SIGNING_ACCOUNT`, `ARTIFACT_SIGNING_PROFILE`.
- **Cloud-HSM certificate:** the provider's credential as secrets in a protected GitHub environment `windows-signing` (with you as required reviewer), and the certificate's thumbprint.

In both cases the Windows workflow gets the same stages as macOS:
- an approval gate;
- signing of the program, then the installer, then the ZIP;
- the Signing Doctor and `Get-AuthenticodeSignature`;
- the existing runtime and installer checks;
- passports whose `signing.status` is `signed` only when Windows itself reports `Valid`.
