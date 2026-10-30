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

## In-app logo mark — still a placeholder

Inside the app (sidebar, welcome card, About page) a **temporary placeholder** mark is shown: the
letters "HCG" in a rounded graphite tile, labelled as a placeholder in its tooltip and on
*Settings → About*. It is not, and must not be presented as, the official HCG logo.

To replace it, add the official logo as `src/HCG.Mail.App/Assets/Branding/hcg-logo.png` (square PNG with
transparency, ≥ 256 px, ideally 512 px). `Views/BrandMark.axaml(.cs)` detects the file at start-up,
shows it and hides the placeholder automatically — no code change needed. (The application icon above
could also be used there if HCG wants the same artwork in the sidebar.)

| Still wanted | Format | Notes |
|---|---|---|
| Official HCG logo mark | PNG with transparency (SVG master welcome), ≥ 256 px | sidebar, welcome card, About |
| Light-background variant (optional) | PNG | HCG Classic skin (needs a small code change to select per skin) |
| macOS icon (later) | `.icns` from a 1024 px master | `.app` bundle — `hcg-mail-1024.png` can be used |

The "HCG MAIL" wordmark next to the mark is live text with a brushed-metal gradient
(`Hcg.Brush.Metal` in `Themes/Palette.axaml`).
