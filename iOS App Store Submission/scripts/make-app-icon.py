#!/usr/bin/env python3
"""Regenerates the iOS AppIcon from the supplied HCG Mail artwork.

App Store icons must be 1024x1024, square, opaque (no alpha) and unrounded: iOS applies the corner mask itself.
Source: Windows-executable-Application/assets/branding/hcg-mail-app-icon-source.png (1254x1254, RGB).
Requires Pillow: python3 -m pip install Pillow
"""
from pathlib import Path
from PIL import Image

root = Path(__file__).resolve().parents[2]
source = root / "Windows-executable-Application" / "assets" / "branding" / "hcg-mail-app-icon-source.png"
target = Path(__file__).resolve().parents[1] / "src" / "HCG.Mail.iOS" / "Assets.xcassets" / "AppIcon.appiconset" / "AppIcon-1024.png"

image = Image.open(source).convert("RGB")
if image.width != image.height:
    raise SystemExit(f"{source} is not square ({image.width}x{image.height}); supply square artwork instead of stretching it")
image.resize((1024, 1024), Image.LANCZOS).save(target, optimize=True)
print(f"wrote {target}")
