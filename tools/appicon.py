#!/usr/bin/env python3
"""Builds src/ShinyCommander.Shared/Resources/AppIcon/appicon.png (1024px) from art/shiny-logo.svg:
the Shiny mark on a dark macOS-style rounded tile. Needs rsvg-convert (brew install librsvg) and Pillow."""
import os, subprocess, tempfile
from PIL import Image, ImageDraw, ImageFilter

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
svg = os.path.join(root, 'art', 'shiny-logo.svg')
out = os.path.join(root, 'src', 'ShinyCommander.Shared', 'Resources', 'AppIcon', 'appicon.png')

N, tile, r = 1024, 824, 185          # Apple's macOS icon grid: 824pt tile on a 1024 canvas
off = (N - tile) // 2
top, bottom = (0x2A, 0x1E, 0x58), (0x0D, 0x0A, 0x20)

with tempfile.TemporaryDirectory() as tmp:
    png = os.path.join(tmp, 'logo.png')
    subprocess.run(['rsvg-convert', '-w', '800', '-h', '800', svg, '-o', png], check=True)
    logo = Image.open(png).convert('RGBA')

canvas = Image.new('RGBA', (N, N), (0, 0, 0, 0))
shadow = Image.new('RGBA', (N, N), (0, 0, 0, 0))
ImageDraw.Draw(shadow).rounded_rectangle((off, off + 14, off + tile, off + tile + 14), r, fill=(0, 0, 0, 120))
canvas.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(20)))

grad = Image.new('RGBA', (tile, tile))
d = ImageDraw.Draw(grad)
for y in range(tile):
    t = y / (tile - 1)
    d.line([(0, y), (tile, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
glow = Image.new('RGBA', (tile, tile), (0, 0, 0, 0))
ImageDraw.Draw(glow).ellipse((tile * 0.18, tile * 0.18, tile * 0.82, tile * 0.82), fill=(140, 70, 255, 70))
grad.alpha_composite(glow.filter(ImageFilter.GaussianBlur(70)))
mask = Image.new('L', (tile, tile), 0)
ImageDraw.Draw(mask).rounded_rectangle((0, 0, tile - 1, tile - 1), r, fill=255)
canvas.paste(grad, (off, off), mask)
rim = Image.new('RGBA', (tile, tile), (0, 0, 0, 0))
ImageDraw.Draw(rim).rounded_rectangle((0, 0, tile - 1, tile - 1), r, outline=(255, 255, 255, 38), width=3)
canvas.alpha_composite(rim, (off, off))

scale = 800 / 360
cx, cy = 187.5, 176.5                # centre of the mark inside its 360 viewBox
canvas.alpha_composite(logo, (int(N / 2 - cx * scale), int(N / 2 - cy * scale)))
canvas.save(out)
print(out)
