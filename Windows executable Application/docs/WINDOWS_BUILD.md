# HCG Mail — Windows build and test

HCG Mail is developed on macOS and published for **Windows 11 (primary) and Windows 10 (x64)** as a
self-contained single file: the target PC does **not** need .NET installed.

## Build from the Mac (or Linux)

```bash
scripts/publish-windows.sh
# → artifacts/publish/win-x64/HCGMail.exe          (~110 MB, single file)
# → artifacts/HCGMail-0.1.0-mvp-win-x64.zip        (+ SHA-256 checksums printed)
```

Equivalent manual command:

```bash
dotnet publish src/HCG.Mail.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
  -o artifacts/publish/win-x64
```

On Windows itself: `powershell -ExecutionPolicy Bypass -File scripts\publish-windows.ps1`.

The build embeds the Windows application manifest (Windows 10/11 compatibility, Per-Monitor-V2 DPI
awareness, long paths), the HCG Mail application icon and version information (Hakmi Capital Group / HCG Mail
0.1.0).

## Install on the test PC (no installer yet)

1. Copy `HCGMail-0.1.0-mvp-win-x64.zip` to the PC and compare its SHA-256
   (`Get-FileHash .\HCGMail-0.1.0-mvp-win-x64.zip`).
2. Right-click the zip → **Properties** → tick **Unblock** (if shown) → OK, then extract to e.g.
   `C:\Users\<you>\Apps\HCG Mail\`.
3. Run `HCGMail.exe`. Because the build is **not code-signed**, SmartScreen shows *"Windows protected
   your PC"* → **More info** → **Run anyway**. (Production code signing needs a certificate and
   founder approval; nothing has been purchased or configured.)
4. Optional: right-click `HCGMail.exe` → *Pin to Start* / *Send to → Desktop (create shortcut)*.

Uninstall: delete the folder, then optionally delete `%LOCALAPPDATA%\HCG\HCG Mail` (settings, logs) and
the `HCG Mail/account:…` entries in *Control Panel → Credential Manager → Windows Credentials*
(removing an account inside HCG Mail already deletes its entry).

## First checks on Windows hardware (Phase E)

Run these before the Domene.no checklist:

| # | Check | Expected |
|---|---|---|
| W1 | `HCGMail.exe --check-credential-store` then open `%LOCALAPPDATA%\HCG\HCG Mail\credential-store-check.txt` | All four steps PASS, `RESULT: OK` |
| W2 | Start `HCGMail.exe` normally | No yellow preview banner, no sample accounts or messages; welcome screen if no account is configured (the developer preview does not exist in this Release build) |
| W3 | Title bar | HCG title strip with native minimise / maximise / close at the top right; window can be dragged by the strip, double-click maximises, Aero Snap works |
| W4 | Display scaling 125 % / 150 % (Settings → Display) | Text crisp, nothing clipped |
| W5 | Switch skin (Application settings → Appearance) | HCG Future ↔ HCG Classic switch instantly |
| W6 | After adding your account, open an HTML newsletter | Renders on a white "paper" card; remote image blocked banner shown |
| W7 | If the window is black (VM / RDP / old GPU driver) | Start with `HCGMail.exe --software-rendering` and report it |
| W8 | Continue with [MANUAL_TEST_CHECKLIST.md](MANUAL_TEST_CHECKLIST.md) | Welcome screen with *Add email account* |

## What has and has not been verified for Windows

| Verified (in a Linux container) | Not yet verified |
|---|---|
| `win-x64` self-contained single-file publish succeeds; `HCGMail.exe` is a PE32+ x64 GUI binary with Skia, HarfBuzz and ANGLE native libraries embedded | Running on real Windows 10 / Windows 11 hardware |
| Under **Wine 9** (reports Windows 10.0.19043), a folder publish of the same build: `--check-credential-store` → Windows Credential Manager write/read/update/delete **PASS** | Default GPU rendering on real Windows (under Wine only `--software-rendering` draws) |
| Under Wine with `--software-rendering`: main window renders with the extended HCG title bar and native caption buttons | Single-file bundle under Wine (Wine cannot load .NET 10 single-file bundles; unrelated to Windows) |
| | DPI scaling, Aero Snap, SmartScreen flow, Windows Defender behaviour |

## Distribution roadmap

1. *Now:* zip with single `HCGMail.exe` (unsigned) for founder testing.
2. *Next (needs approval):* Authenticode signing certificate; MSIX or Inno Setup installer with Start
   menu entry and uninstaller; optional auto-update channel (would be a visible, opt-in network
   feature).
