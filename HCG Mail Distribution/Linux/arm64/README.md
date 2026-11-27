# HCG Mail for Linux (arm64)

Drop folder for **64-bit ARM Linux desktops (for example Raspberry Pi 4/5 with a 64-bit OS)** builds (`linux-arm64`).

**Put here:** a self-contained publish folder or a tarball of it (with the `HCGMail` executable).

**How it is built:** `dotnet publish "HCG Mail Project/Desktop/src/HCG.Mail.App/HCG.Mail.App.csproj" -c Release -r linux-arm64 --self-contained`

**What has been verified:** The self-contained publish for `linux-arm64` builds. It has not been run on ARM64 Linux, and there is no AppImage or .deb packaging yet. On Linux, passwords are kept for the session only.

Git ignores everything in this folder except this README, so executables, app bundles and portable folders copied
here are never committed. Before you share a file, compare its SHA-256 checksum with the one recorded for that build.
