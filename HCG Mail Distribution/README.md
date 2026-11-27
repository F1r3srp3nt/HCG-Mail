# HCG Mail Distribution

Local drop folders for HCG Mail executables, one per operating system and architecture that HCG Mail is actually built
for today. Copy your builds into the matching folder.

**Nothing you copy here is committed.** Git ignores everything in these folders except the `README.md` files, including
nested app bundles (`HCG Mail.app/`) and unzipped portable folders. Releases are published through their own channels
(HCG Store, HCG Repo, GitHub Releases), never by committing binaries.

| Folder | .NET runtime identifier | For |
|---|---|---|
| [`Windows/x64/`](Windows/x64/) | `win-x64` | Intel/AMD 64-bit Windows 10 (1607+) and 11 |
| [`Windows/arm64/`](Windows/arm64/) | `win-arm64` | Windows 11 on ARM (for example Snapdragon PCs) |
| [`macOS/x64/`](macOS/x64/) | `osx-x64` | Intel Macs |
| [`macOS/arm64/`](macOS/arm64/) | `osx-arm64` | Apple Silicon Macs (M1 and later) |
| [`Linux/x64/`](Linux/x64/) | `linux-x64` | 64-bit Intel/AMD Linux desktops |
| [`Linux/arm64/`](Linux/arm64/) | `linux-arm64` | 64-bit ARM Linux desktops (for example Raspberry Pi 4/5 with a 64-bit OS) |
| [`iOS/arm64/`](iOS/arm64/) | `ios-arm64` | iPhone and iPad (iOS 16.0 or later) |

Not listed, because nothing is built for them yet: Android, the Microsoft Store, the Mac App Store, Linux stores and a
text-mode Linux edition. Their plans are in
[`HCG Mail Project/docs/packaging/`](../HCG%20Mail%20Project/docs/packaging/).

The source code is in [`HCG Mail Project/`](../HCG%20Mail%20Project/).
