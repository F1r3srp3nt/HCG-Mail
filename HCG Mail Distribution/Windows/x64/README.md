# HCG Mail for Windows (x64)

Drop folder for **Intel/AMD 64-bit Windows 10 (1607+) and 11** builds (`win-x64`).

**Put here:** `HCGMail-<version>-win-x64-Setup.exe` (installer), `HCGMail-<version>-win-x64.zip` (portable), or an unzipped portable folder with `HCGMail.exe`.

**How it is built:** Built by HCG Executable Maker in `.github/workflows/windows-prerelease.yml` (installer and portable ZIP), or locally with `scripts/publish-windows.sh` / `scripts/publish-windows.ps1` in `HCG Mail Project/Desktop/`.

**What has been verified:** Started, rendered, installed and uninstalled on GitHub-hosted Windows x64 runners (Windows build 26100). Not code-signed.

Git ignores everything in this folder except this README, so executables, app bundles and portable folders copied
here are never committed. Before you share a file, compare its SHA-256 checksum with the one recorded for that build.
