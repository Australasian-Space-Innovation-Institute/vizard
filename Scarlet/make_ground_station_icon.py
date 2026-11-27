"""Make the Scarlet ground station icon: Assets/Resources/Scarlet/ground_station_icon.png.

A satellite dish on a mast, white with a dark outline, on a transparent
background (RGBA, 256 x 256). Drawn by DrawLocationMarker for a location that
carries its own label (the demo app's ground stations), tinted with the
location's colour, in place of the capsule marker.

    python make_ground_station_icon.py [size]
"""
import math
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

SIZE = int(sys.argv[1]) if len(sys.argv) > 1 else 256
OUT = Path(__file__).resolve().parent.parent / "VizardUnityProject/Assets/Resources/Scarlet/ground_station_icon.png"
S = 1024                                     # drawn at 1024 px, then scaled down (anti-aliased)
OUTLINE = 34                                 # outline width at 1024 px


def add(p, q, k=1.0):
    return (p[0] + k * q[0], p[1] + k * q[1])


def glyph() -> Image.Image:
    """The dish in white on transparent, at S x S."""
    img = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(img)
    r = 1 / math.sqrt(2)
    rim = (r, r)                             # along the dish's rim (image y down): down-right
    bulge = (-r, r)                          # the bowl's back: down-left; it faces up-right
    c = (470, 430)                           # the rim's centre
    a, b = 300, 120                          # half the rim's width, the bowl's depth
    bowl = [add(add(c, rim, a * math.cos(t)), bulge, b * math.sin(t))
            for t in [math.pi * k / 64 for k in range(65)]]
    d.polygon(bowl, fill=255)
    feed = add(c, bulge, -230)               # the feed, in front of the dish on an arm
    d.line([c, feed], fill=255, width=30)
    d.ellipse([feed[0] - 46, feed[1] - 46, feed[0] + 46, feed[1] + 46], fill=255)
    back = add(c, bulge, b)                  # the bowl's back, on the mast
    d.polygon([(back[0] - 34, back[1] - 20), (back[0] + 34, back[1] - 20), (560, 820), (470, 820)], fill=255)
    d.polygon([(380, 820), (650, 820), (710, 930), (320, 930)], fill=255)    # the base
    return img


def main() -> None:
    shape = glyph()
    outline = shape.filter(ImageFilter.MaxFilter(OUTLINE * 2 + 1))
    rgba = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    rgba.paste((30, 30, 30, 255), mask=outline)
    rgba.paste((255, 255, 255, 255), mask=shape)
    rgba = rgba.resize((SIZE, SIZE), Image.LANCZOS)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    rgba.save(OUT)
    print(f"wrote {OUT} ({SIZE} x {SIZE})")


if __name__ == "__main__":
    main()
