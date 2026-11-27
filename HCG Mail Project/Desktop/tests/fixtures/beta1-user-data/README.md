# User data written by HCG Mail 1.0.0-beta.1

`accounts.json` and `settings.json` exactly as HCG Mail 1.0.0-beta.1 writes them (storage code unchanged since release
candidate `0b88b94`), for one sanitized IMAP/SMTP account (`test@example.com` at `mail.example.com`) and the HCG Classic
theme. Used by `StorageCompatibilityTests` (the current code must read them unchanged) and by the Windows upgrade check
(`scripts/windows-upgrade-check.ps1`: install beta.1, place this data, install the new version over it, start it).
