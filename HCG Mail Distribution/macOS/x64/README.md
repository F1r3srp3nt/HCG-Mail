# HCG Mail for macOS (x64)

Drop folder for **Intel Macs** builds (`osx-x64`).

**Put here:** a self-contained publish folder (with the `HCGMail` executable) or an `HCG Mail.app` bundle you made from it.

**How it is built:** `dotnet publish "HCG Mail Project/Desktop/src/HCG.Mail.App/HCG.Mail.App.csproj" -c Release -r osx-x64 --self-contained`

**What has been verified:** The self-contained publish for `osx-x64` builds. There is no `.app` bundle, signing or notarization in this repository yet (see `HCG Mail Project/docs/packaging/macos-executable.md`), and no test on a real Mac is recorded.

Git ignores everything in this folder except this README, so executables, app bundles and portable folders copied
here are never committed. Before you share a file, compare its SHA-256 checksum with the one recorded for that build.
