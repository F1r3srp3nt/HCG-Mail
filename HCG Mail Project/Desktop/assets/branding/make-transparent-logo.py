#!/usr/bin/env python3
"""Transparent master of the official HCG logo, from the supplied artwork (hcg-logo-source.png, the logo on black).

Nothing is redrawn: every visible pixel comes from the source. The source is the logo photographed on black, so each
pixel is "light emitted by the logo" (premultiplied colour on black). Two parts are separated:

  letters  The angular metal letter bodies with their dark bevel lines and recesses (closed within the letter outline).
           They are opaque, so the metal keeps its colours on any background. A brightness-derived alpha would make the
           grey metal semi-transparent and wash it out on light backgrounds; the previous master did that, which is why
           the app needed a dark backing plate in the light theme.
  light    The blue glow, highlights and flares around the letters. They keep the source's light as transparency
           (alpha from brightness, colour un-premultiplied), so they fade into any background with no dark fringe. The
           faint ambient blue haze of the photo background (below LIGHT_FLOOR) is background, not logo, and is dropped.

On black, the master reproduces the source (checked below, over the whole logo). Known limit: the photographic glow is
additive light; on white at large sizes it shows as a soft blue halo, and the lowest, blue-lit letter faces cannot be
separated from that light by colour alone. A perfect light-background version needs the layered or vector original.

Outputs (RGBA PNG, straight alpha):
  hcg-logo-transparent.png              master at the source resolution with all of its light (flares to the edges)
  hcg-logo-transparent-compact.png      the letters plus a margin, flares faded at the edges: for UI use
  variants/hcg-logo-{512,1024}w.png     the compact master downscaled (Lanczos)
  ../../src/HCG.Mail.App/Assets/Branding/hcg-logo.png   in-app copy, 240 px high (about 6x its 38 px display height)
  ../../docs/images/branding/hcg-logo-backgrounds.png    check on light, dark and coloured backgrounds (large, 38 and 76 px)
Usage: python3 make-transparent-logo.py   (numpy, scipy, Pillow)
"""
import os
import numpy as np
from PIL import Image
from scipy import ndimage

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "hcg-logo-source.png")
LIGHT_FLOOR, LIGHT_FULL = 0.09, 0.55     # brightness: below the floor = background haze; above full = opaque light

src = np.asarray(Image.open(SRC).convert("RGB")).astype(np.float64) / 255.0
v = src.max(axis=2)
s = np.where(v > 0, (v - src.min(axis=2)) / np.maximum(v, 1e-6), 0)

def disk(r):
    return np.add.outer(np.arange(-r, r + 1) ** 2, np.arange(-r, r + 1) ** 2) <= r * r

# 1. Letters: metal (grey/white, low saturation) and bright edge highlights; thin flare streaks removed; dark bevel
#    lines and recesses within each letter outline closed; small enclosed gaps filled (the counters of C and G are
#    large or open and stay transparent); specks dropped.
metal = ((v > 0.16) & (s < 0.45)) | (v > 0.80)
metal = ndimage.binary_opening(metal, structure=disk(3))
metal = ndimage.binary_closing(metal, structure=disk(15))
holes = ndimage.binary_fill_holes(metal) & ~metal
labels, n = ndimage.label(holes)
sizes = ndimage.sum(holes, labels, range(1, n + 1))
for i, size in enumerate(sizes, start=1):
    if size < 16000:
        metal[labels == i] = True
labels, n = ndimage.label(metal)
sizes = ndimage.sum(metal, labels, range(1, n + 1))
letters = np.isin(labels, [i + 1 for i, size in enumerate(sizes) if size > 20000])
letter_alpha = np.clip(ndimage.gaussian_filter(letters.astype(np.float64), 0.9) * 1.6 - 0.3, 0, 1)   # anti-aliased edge

# 2. Light: alpha from brightness above the haze floor (smoothstep, never less than the light itself), colour = source /
#    alpha; letters keep the source pixels exactly.
t = np.clip((v - LIGHT_FLOOR) / (LIGHT_FULL - LIGHT_FLOOR), 0, 1)
light_alpha = np.maximum(t * t * (3 - 2 * t), np.where(v > LIGHT_FLOOR, v, 0))
alpha = np.maximum(letter_alpha, light_alpha)
colour = np.clip(src / np.maximum(alpha, 1e-6)[..., None], 0, 1)
colour = np.where(letter_alpha[..., None] >= 0.999, src, colour)
rgba = np.dstack([colour, alpha])

# 3. Crop to the logo (alpha > 2 %) with a 2 % margin.
ys, xs = np.where(alpha > 0.02)
m = int(0.02 * src.shape[1])
y0, y1 = max(ys.min() - m, 0), min(ys.max() + m + 1, src.shape[0])
x0, x1 = max(xs.min() - m, 0), min(xs.max() + m + 1, src.shape[1])
rgba, src_c = rgba[y0:y1, x0:x1], src[y0:y1, x0:x1]

# 4. Check: on black the master reproduces the source (premultiplied colour), apart from the dropped haze.
on_black = rgba[..., :3] * rgba[..., 3:4]
logo_px = rgba[..., 3] > 0.05
err = np.abs(on_black - src_c)[logo_px].mean() * 255
haze = src_c.max(axis=2)[~logo_px].mean() * 255
print(f"crop {x1 - x0}x{y1 - y0} of {src.shape[1]}x{src.shape[0]}; opaque letters {letters[y0:y1, x0:x1].mean():.1%} of the crop")
print(f"on black: mean difference from the source over the logo {err:.2f}/255; dropped background haze averaged {haze:.2f}/255")
assert err < 6, "the transparent master no longer matches the source on black"

def save(arr, path, width=None):
    im = Image.fromarray(np.round(arr * 255).astype(np.uint8), "RGBA")
    if width:
        im = im.resize((width, round(im.height * width / im.width)), Image.LANCZOS)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    im.save(path, optimize=True)
    return im

master = save(rgba, os.path.join(HERE, "hcg-logo-transparent.png"))

# Compact crop for UI use: the letters plus a margin (the long horizontal flares are faded out at the crop edges instead
# of cut), so the letters keep their size at a given height. Proportions of the letters are unchanged.
ly, lx = np.where(letters[y0:y1, x0:x1])
mx, my = int(0.035 * (x1 - x0)), int(0.06 * (y1 - y0))
cy0, cy1 = max(ly.min() - my, 0), min(ly.max() + my + 1, rgba.shape[0])
cx0, cx1 = max(lx.min() - mx, 0), min(lx.max() + mx + 1, rgba.shape[1])
compact = rgba[cy0:cy1, cx0:cx1].copy()
ramp = 40
fade_x = np.clip(np.minimum(np.arange(compact.shape[1]), np.arange(compact.shape[1])[::-1]) / ramp, 0, 1)
fade_y = np.clip(np.minimum(np.arange(compact.shape[0]), np.arange(compact.shape[0])[::-1]) / ramp, 0, 1)
compact[..., 3] *= np.minimum.outer(fade_y, fade_x)
compact_im = save(compact, os.path.join(HERE, "hcg-logo-transparent-compact.png"))
for w in (512, 1024):
    save(compact, os.path.join(HERE, "variants", f"hcg-logo-{w}w.png"), w)
app_h = 240
save(compact, os.path.join(HERE, "..", "..", "src", "HCG.Mail.App", "Assets", "Branding", "hcg-logo.png"),
     round(compact_im.width * app_h / compact_im.height))
print(f"compact (UI) crop {compact_im.width}x{compact_im.height}, ratio {compact_im.width / compact_im.height:.3f}")
master = compact_im      # the check sheet shows what the app uses

# 5. Check sheet: the master on light, dark and coloured backgrounds, large and at the app's 38 px (and 76 px = 200 %).
backgrounds = ["#FFFFFF", "#E9EDF1", "#0B1016", "#000000", "#1F5C99", "#B91C1C", "#1E7B4A", "#F2B33D"]
big_h = round(master.height * 560 / master.width)
big = master.resize((560, big_h), Image.LANCZOS)
small = [master.resize((round(master.width * h / master.height), h), Image.LANCZOS) for h in (38, 76)]
tile_w, tile_h = 640, big_h + 120
sheet = Image.new("RGBA", (tile_w * 2, tile_h * 4), "white")
for i, hexcol in enumerate(backgrounds):
    tile = Image.new("RGBA", (tile_w, tile_h), hexcol)
    tile.alpha_composite(big, (40, 8))
    x = 40
    for im in small:
        tile.alpha_composite(im, (x, big_h + 24 + (76 - im.height) // 2))
        x += im.width + 30
    sheet.paste(tile, ((i % 2) * tile_w, (i // 2) * tile_h))
out = os.path.join(HERE, "..", "..", "docs", "images", "branding", "hcg-logo-backgrounds.png")
os.makedirs(os.path.dirname(out), exist_ok=True)
sheet.convert("RGB").save(out, optimize=True)
print("written: hcg-logo-transparent.png, variants/, the in-app copy and", os.path.relpath(out, HERE))
