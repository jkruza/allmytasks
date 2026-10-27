#!/usr/bin/env python3
"""Regenerates the app and tray icons in src/AllMyTasks/Assets from the three-lines logo.

The logo is the three bars in the header of site/index.html (22/16/10 wide, 5 tall, 3 apart).
Run: python3 scripts/make-icons.py   (needs Pillow). See docs/design/app-icon.md.
"""
from pathlib import Path
from PIL import Image, ImageDraw

BARS = [(22, "#3b82f6"), (16, "#10b981"), (10, "#f59e0b")]  # width, colour
BAR_H, GAP, RADIUS = 5, 3, 2
LOGO_W, LOGO_H = 22, 3 * BAR_H + 2 * GAP
SS = 4  # supersampling factor
OUT = Path(__file__).resolve().parent.parent / "src" / "AllMyTasks" / "Assets"


def draw_bars(d, x, y, scale):
    for i, (w, colour) in enumerate(BARS):
        top = y + i * (BAR_H + GAP) * scale
        d.rounded_rectangle([x, top, x + w * scale, top + BAR_H * scale], RADIUS * scale, fill=colour)


def render(size, tile):
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    if tile:  # app icon: bars on a dark rounded tile so it reads on any background
        d.rounded_rectangle([0, 0, s - 1, s - 1], s * 0.22, fill="#1f2937")
        scale = s * 0.60 / LOGO_W
    else:  # tray icon: bars only, as large as the canvas allows
        scale = s * 0.94 / LOGO_W
    draw_bars(d, (s - LOGO_W * scale) / 2, (s - LOGO_H * scale) / 2, scale)
    return img.resize((size, size), Image.LANCZOS)


OUT.mkdir(parents=True, exist_ok=True)
render(64, tile=False).save(OUT / "tray.png")
render(256, tile=True).save(OUT / "app.png")
render(256, tile=True).save(OUT / "app.ico", sizes=[(s, s) for s in (16, 24, 32, 48, 64, 128, 256)])
