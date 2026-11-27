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

The official HCG logo (angular chrome "HCG" lettering with electric-blue light) was supplied by the founder as
`hcg-logo-source.png` (1933 × 814, the logo photographed on black). It is never redrawn: every visible pixel of the
transparent versions comes from that file, made by `make-transparent-logo.py` (reproducible: numpy, scipy, Pillow).

| File | Purpose |
|---|---|
| `assets/branding/hcg-logo-source.png` | Original artwork as supplied (approved source for HCG Representation) |
| `assets/branding/hcg-logo-transparent.png` | Transparent master, 1933 × 795, all of its light (flares to the edges) |
| `assets/branding/hcg-logo-transparent-compact.png` | Transparent master for UI use, 1624 × 694: the letters plus a margin, flares faded at the edges |
| `assets/branding/variants/hcg-logo-{512,1024}w.png` | The compact master downscaled (Lanczos) |
| `src/HCG.Mail.App/Assets/Branding/hcg-logo.png` | In-app copy, 562 × 240 (≈ 6× the 38 px display height) for sharp high-DPI rendering |
| `docs/images/branding/hcg-logo-backgrounds.png` | Check sheet: white, light grey, both sidebars, black, blue, red, green and amber backgrounds; large, 38 px and 76 px |

How the transparency is made:
* **Letters** (the metal bodies with their dark bevel lines and recesses) are **opaque**, with the source's own pixels,
  so the metal keeps its colours on light, dark and coloured backgrounds.
* **The blue light** (glow, highlights, flares) keeps the source's light as transparency, so it fades into any background
  without a dark fringe. The faint ambient haze of the photo background is dropped.
* On black, the master reproduces the source exactly (the script checks it: mean difference 0.00/255).
* No backing plate in any theme (the earlier graphite plate in HCG Classic is gone); proportions preserved.

Known limit: the glow in the source is photographed light. On white at large sizes it shows as a soft blue halo, and the
lowest, blue-lit faces of the letters cannot be separated from that light by colour. At the app's sizes it reads cleanly
in both themes. A perfect light-background version needs the layered or vector original of the logo.

The "HCG MAIL" wordmark is live text with a brushed-metal gradient (`Hcg.Brush.Metal`), shown once per window (the
sidebar's brand mark), and the brand mark scales down as a whole when its column is narrow, so it is never clipped.

| Still wanted (optional) | Format | Notes |
|---|---|---|
| Layered or vector original of the HCG logo | AI, SVG, PSD or PDF with separate layers | a perfect light-background version (glow as its own layer) |
| macOS icon | `.icns` from a 1024 px master | `.app` bundle — `assets/icons/hcg-mail-1024.png` can be used |
