# HCG Mail for Windows (arm64)

Drop folder for **Windows 11 on ARM (for example Snapdragon PCs)** builds (`win-arm64`).

**Put here:** `HCGMail-<version>-win-arm64-Setup.exe` (installer), `HCGMail-<version>-win-arm64.zip` (portable), or an unzipped portable folder with `HCGMail.exe`.

**How it is built:** Built by HCG Executable Maker in `.github/workflows/windows-prerelease.yml` (installer and portable ZIP).

**What has been verified:** Started, rendered, installed and uninstalled on GitHub-hosted Windows 11 ARM64 runners (build 26200). Not code-signed.

Git ignores everything in this folder except this README, so executables, app bundles and portable folders copied
here are never committed. Before you share a file, compare its SHA-256 checksum with the one recorded for that build.
