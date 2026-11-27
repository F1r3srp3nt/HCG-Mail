# HCG brand assets

## Application icon — supplied ✅

The HCG Mail application icon (brushed-metal envelope with "HCG" lettering, cyan glow and circuit traces on black)
was supplied by the founder on 2026-11-12, replacing the earlier "HCG MAIL" globe icon. It is used for the Windows
executable, the taskbar/Start menu and all window title bars.

| File | Purpose |
|---|---|
| `assets/branding/hcg-mail-app-icon-source.png` | Original artwork as supplied (1254 × 1254, square) |
| `assets/icons/hcg-mail-1024.png` | Square master: the artwork scaled to 1024 × 1024 with rounded corners (radius 18 %), transparent outside the tile |
| `assets/icons/hcg-mail.ico` | Windows icon, 16 / 24 / 32 / 48 / 64 / 128 / 256 px |
| `src/HCG.Mail.App/Assets/Icons/hcg-mail.ico`, `hcg-mail.png` (256 px) | Copies built into the app (`ApplicationIcon` in `HCG.Mail.App.csproj`, `Icon=` on each window) |

To update the icon, replace the source artwork and regenerate the master and `.ico`: scale to 1024 px (Lanczos),
apply the rounded-corner mask (anti-aliased), export the sizes above. Nothing is stretched; the artwork is already
square. Other platforms' icons are produced from the 1024 px master by HCG Executable Maker.

## HCG logo — supplied ✅

The official HCG logo (chrome "HCG" lettering with electric-blue edges) was supplied by the founder and
is shown in the sidebar header, on the welcome card and on *Settings → About*, next to the live
"HCG MAIL / HAKMI CAPITAL GROUP" wordmark.

| File | Purpose |
|---|---|
| `assets/branding/hcg-logo-source.png` | Original artwork as supplied (1933 × 814, dark background) |
| `assets/branding/hcg-logo-transparent.png` | Transparent master (1520 × 700): lettering cropped from the source; transparency derived from brightness so the glow is preserved, edges feathered — reproduces the original exactly on a dark background |
| `src/HCG.Mail.App/Assets/Branding/hcg-logo.png` | In-app copy (520 × 239, ≈ 6× the 38 px display height) for sharp high-DPI rendering |

* Proportions are preserved (`Stretch="Uniform"`, high-quality interpolation); nothing is stretched.
* **HCG Future:** the logo sits directly on the sidebar — no badge or border.
* **HCG Classic:** the artwork is designed for a dark background and washes out on the light sidebar,
  so it is shown on a slim graphite backing (`Hcg.Brush.LogoBacking`, slightly smaller via
  `Hcg.LogoHeight`) to keep it legible.
* The "HCG MAIL" wordmark is live text with a brushed-metal gradient (`Hcg.Brush.Metal`).

| Still wanted (optional) | Format | Notes |
|---|---|---|
| Logo variant for light backgrounds | PNG with transparency | would let HCG Classic show the logo without the graphite backing |
| macOS icon | `.icns` from a 1024 px master | `.app` bundle — `assets/icons/hcg-mail-1024.png` can be used |
