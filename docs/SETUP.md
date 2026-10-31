# HCG Mail — Development setup

## Prerequisites

| Tool | Version | Install |
|---|---|---|
| .NET SDK | 10.0.100 or newer 10.0.x (`global.json` rolls forward within 10.0) | macOS: `brew install --cask dotnet-sdk` or the installer from https://dotnet.microsoft.com/download/dotnet/10.0 · Windows: `winget install Microsoft.DotNet.SDK.10` · Ubuntu 24.04: `sudo apt install dotnet-sdk-10.0` |
| Git | any | macOS ships it with the Xcode command-line tools (`xcode-select --install`) |
| IDE (optional) | JetBrains Rider, VS Code + C# Dev Kit, or Visual Studio 2022 17.14+ | Avalonia XAML previewer plug-ins are optional |

No Avalonia templates or workloads are required — everything comes from NuGet.

Check the SDK:
```bash
dotnet --info      # SDK 10.0.x must be listed
```

## Everyday commands (macOS / Linux / Windows)

```bash
dotnet restore                                        # download NuGet packages
dotnet build                                          # build all projects (Debug)
dotnet test                                           # unit + headless UI tests (no network)
dotnet run --project src/HCG.Mail.App                 # start HCG Mail
dotnet run --project src/HCG.Mail.App -- --developer-preview   # Debug builds only: labelled sample data, no network
dotnet build -c Release                               # release build
```

Useful switches and variables:

| Switch / variable | Effect |
|---|---|
| `--developer-preview` | **Debug builds only** (ignored in Release builds): development preview with labelled sample messages. Nothing is persisted, nothing is sent, a banner says so. A normal start never loads sample data, and no environment variable enables preview. |
| `HCG_MAIL_DATA_DIR=/some/folder` | Use another settings folder (handy for testing a clean first run). |
| `--software-rendering` or `HCG_MAIL_SOFTWARE_RENDERING=1` | Force CPU rendering (troubleshooting for VMs/RDP/broken GPU drivers). |
| `--check-credential-store` | Verifies the OS credential store with a throwaway entry, writes `credential-store-check.txt` to the settings folder, exit code 0 = OK. |
| `HCG_MAIL_SCREENSHOT_DIR=/folder` | UI tests save PNG screenshots there. |
| `HCG_MAIL_E2E=1` | Enables the end-to-end tests (needs the local test server below). |

### Avalonia build telemetry is disabled

Avalonia's build tooling normally sends anonymous build statistics to avaloniaui.net. HCG Mail's
`Directory.Build.targets` overrides that target, so no build of this repository phones home. (You can
additionally set `AVALONIA_TELEMETRY_OPTOUT=1` in your shell profile to cover other Avalonia projects.)

## macOS specifics

* **Keychain:** passwords are stored as generic-password items named **"HCG Mail"** in the login
  keychain. The first save/read may show a macOS prompt — choose *Always Allow*. Verify with
  `dotnet run --project src/HCG.Mail.App -- --check-credential-store`.
  ⚠️ The Keychain implementation has not yet been executed on a Mac; please run the check above and
  the manual checklist once.
* The window uses the native traffic-light buttons with the HCG title bar extended underneath.
* Settings folder: `~/Library/Application Support/HCG Mail`.

## Linux (development containers / CI)

Builds, tests and runs (X11). There is **no secure credential store implementation on Linux**: the
password is kept in memory for the session only and the account screen says so; it is never written
to disk.

## End-to-end tests against a disposable local mail server (optional)

The default test run never touches a mail server. To exercise the real IMAP/SMTP code paths:

```bash
# Linux only, in a disposable VM/container, as root — it installs a test CA into the system trust store
sudo apt install dovecot-imapd dovecot-lmtpd postfix
sudo tests/integration-server/setup-local-mail-server.sh start
HCG_MAIL_E2E=1 dotnet test
sudo tests/integration-server/setup-local-mail-server.sh cleanup   # removes server, CA and hosts entry
```

The server (`mail.hcgmail.test`) offers IMAP 993/143 and SMTP 465/587 with SASL auth, SPECIAL-USE
folders, a CRL endpoint for revocation checks, and refuses every non-local recipient — nothing can
leave the machine. **Never point the end-to-end tests at a real mailbox.**

## Troubleshooting

| Symptom | Fix |
|---|---|
| `A compatible .NET SDK was not found` | Install .NET SDK 10.0.x. |
| Window opens but stays black (VM/RDP) | Start with `--software-rendering`. |
| Connection test fails at *TLS negotiation* | Check that the security setting matches the port (993/465 = SSL/TLS, 143/587 = STARTTLS) and the server name matches the provider's certificate. |
| "revocation status could not be checked" | The certificate authority's CRL/OCSP server was unreachable (firewall/proxy). HCG Mail does not skip revocation checks. |
| Logs | `logs/` inside the settings folder (no passwords or message content). |
