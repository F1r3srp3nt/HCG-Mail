# HCG brand assets

## Application icon — supplied ✅

The HCG Mail application icon (metallic envelope over a globe, "HCG MAIL", cyan glow) was supplied by
the founder and is used for the Windows executable, the taskbar/Start menu and all window title bars.

| File | Purpose |
|---|---|
| `assets/branding/hcg-mail-app-icon-source.png` | Original artwork as supplied (1783 × 882) |
| `assets/icons/hcg-mail-1024.png` | Square master: tile cropped from the source, transparent outside the rounded tile |
| `assets/icons/hcg-mail.ico` | Windows icon, 16 / 24 / 32 / 48 / 64 / 128 / 256 px |
| `src/HCG.Mail.App/Assets/Icons/hcg-mail.ico`, `hcg-mail.png` | Copies built into the app (`ApplicationIcon` in `HCG.Mail.App.csproj`, `Icon=` on each window) |

To update the icon, replace the source artwork and regenerate the square master and `.ico` (crop the
rounded tile, pad to a square, export the sizes above).

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
