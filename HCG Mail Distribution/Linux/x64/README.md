# HCG Mail for Linux (x64)

Drop folder for **64-bit Intel/AMD Linux desktops** builds (`linux-x64`).

**Put here:** a self-contained publish folder or a tarball of it (with the `HCGMail` executable).

**How it is built:** `dotnet publish "HCG Mail Project/Desktop/src/HCG.Mail.App/HCG.Mail.App.csproj" -c Release -r linux-x64 --self-contained`

**What has been verified:** The self-contained publish for `linux-x64` builds, and the app's automated and headless UI tests run on Linux in CI. There is no AppImage or .deb packaging yet (see `HCG Mail Project/docs/packaging/linux-ui.md`). On Linux, passwords are kept for the session only (no Secret Service integration yet).

Git ignores everything in this folder except this README, so executables, app bundles and portable folders copied
here are never committed. Before you share a file, compare its SHA-256 checksum with the one recorded for that build.
