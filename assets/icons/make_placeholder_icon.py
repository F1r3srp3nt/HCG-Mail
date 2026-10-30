"""Generates the TEMPORARY placeholder application icon for HCG Mail.

This is not the official HCG logo. Replace assets/icons/hcg-mail-placeholder.* with the
official asset when it is supplied (see assets/branding/README.md).
"""
import sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter

out_dir = sys.argv[1]
font_path = "/usr/share/fonts/opentype/inter/Inter-Bold.otf"

def render(size):
    s = size * 4  # supersample
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = int(s * 0.22)
    # graphite tile with subtle vertical gradient
    tile = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    grad = Image.new("RGBA", (1, s))
    for y in range(s):
        t = y / s
        c = (int(28 - 16 * t), int(38 - 22 * t), int(50 - 28 * t), 255)
        grad.putpixel((0, y), c)
    grad = grad.resize((s, s))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=r, fill=255)
    tile.paste(grad, (0, 0), mask)
    img = Image.alpha_composite(img, tile)
    d = ImageDraw.Draw(img)
    # cyan hairline border
    w = max(2, s // 64)
    d.rounded_rectangle([w, w, s - 1 - w, s - 1 - w], radius=r - w, outline=(63, 208, 245, 200), width=w)
    # letters
    font = ImageFont.truetype(font_path, int(s * 0.30))
    text = "HCG"
    bbox = d.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    x = (s - tw) / 2 - bbox[0]
    y = (s - th) / 2 - bbox[1] - s * 0.04
    glow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    ImageDraw.Draw(glow).text((x, y), text, font=font, fill=(63, 208, 245, 160))
    glow = glow.filter(ImageFilter.GaussianBlur(s * 0.03))
    img = Image.alpha_composite(img, glow)
    d = ImageDraw.Draw(img)
    d.text((x, y), text, font=font, fill=(230, 238, 244, 255))
    # accent bar under the letters
    bar_w = tw * 0.55
    by = y + bbox[3] + s * 0.07
    d.rounded_rectangle([(s - bar_w) / 2, by, (s + bar_w) / 2, by + s * 0.025], radius=s * 0.0125, fill=(63, 208, 245, 255))
    return img.resize((size, size), Image.LANCZOS)

sizes = [16, 24, 32, 48, 64, 128, 256]
images = [render(sz) for sz in sizes]
images[-1].save(f"{out_dir}/hcg-mail-placeholder.png")
render(512).save(f"{out_dir}/hcg-mail-placeholder-512.png")
images[-1].save(f"{out_dir}/hcg-mail-placeholder.ico", sizes=[(sz, sz) for sz in sizes])
print("ok")
