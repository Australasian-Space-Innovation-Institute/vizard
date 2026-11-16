"""Make the Scarlet cloud layer texture: Assets/Resources/Scarlet/clouds.png.

An equirectangular (2:1) RGBA map: white, cloud cover in the alpha channel.
Procedural (fractal noise sampled on the unit sphere, so it is seamless in
longitude and not pinched at the poles), with Earth-like banding: more cloud
along the equator (ITCZ) and the mid-latitude storm tracks, less over the
subtropics. Not real weather: a backdrop, drawn by ScarletPresentation's
"CLOUDS on|off".

    python make_cloud_texture.py [width] [seed]
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image

WIDTH = int(sys.argv[1]) if len(sys.argv) > 1 else 4096
SEED = int(sys.argv[2]) if len(sys.argv) > 2 else 7
OUT = Path(__file__).resolve().parent.parent / "VizardUnityProject/Assets/Resources/Scarlet/clouds.png"

rng = np.random.default_rng(SEED)
PERM = np.concatenate([rng.permutation(256)] * 2)
GRAD = rng.normal(size=(256, 3))
GRAD /= np.linalg.norm(GRAD, axis=1, keepdims=True)


def _fade(t):
    return t * t * t * (t * (t * 6 - 15) + 10)


def perlin(p):
    """3D gradient noise at points p (..., 3), roughly in [-1, 1]."""
    cell = np.floor(p).astype(np.int64)
    f = p - cell
    u = _fade(f)
    total = 0.0
    for dx in (0, 1):
        for dy in (0, 1):
            for dz in (0, 1):
                h = PERM[(PERM[(PERM[(cell[..., 0] + dx) & 255] + cell[..., 1] + dy) & 255] + cell[..., 2] + dz) & 255]
                g = GRAD[h]
                d = f - np.array([dx, dy, dz])
                dot = (g * d).sum(-1)
                w = ((u[..., 0] if dx else 1 - u[..., 0]) * (u[..., 1] if dy else 1 - u[..., 1])
                     * (u[..., 2] if dz else 1 - u[..., 2]))
                total = total + w * dot
    return total


def fbm(p, octaves=7, lacunarity=2.03, gain=0.52):
    value, amp, freq, norm = 0.0, 1.0, 1.0, 0.0
    for i in range(octaves):
        value = value + amp * perlin(p * freq + i * 17.3)
        norm += amp
        amp *= gain
        freq *= lacunarity
    return value / norm


height = WIDTH // 2
lon = (np.arange(WIDTH) + 0.5) / WIDTH * 2 * np.pi - np.pi
lat = np.pi / 2 - (np.arange(height) + 0.5) / height * np.pi
LON, LAT = np.meshgrid(lon, lat)
sphere = np.stack([np.cos(LAT) * np.cos(LON), np.cos(LAT) * np.sin(LON), np.sin(LAT)], -1)

# Zonal shear: points are turned about the polar axis by a smooth, latitude-
# dependent amount, so cloud is drawn out east-west into streaks and fronts.
shear = 0.35 * fbm(sphere * 1.1 + 3.3, 2) + 0.2 * np.sin(3 * LAT)
c, s_ = np.cos(shear), np.sin(shear)
sheared = np.stack([c * sphere[..., 0] - s_ * sphere[..., 1],
                    s_ * sphere[..., 0] + c * sphere[..., 1], sphere[..., 2]], -1)
base = sheared * np.array([2.6, 2.6, 3.4])  # finer north-south: flatter, wider bands
# Domain warp: swirls rather than round blobs.
warp = np.stack([fbm(base + 5.2, 4), fbm(base + 1.7, 4), fbm(base + 9.1, 4)], -1)
n = fbm(base + 1.1 * warp, 8, gain=0.56)
n = (n - n.mean()) / n.std()
# Fine wisps on top, mostly at the cloud edges.
wisp = fbm(sheared * 8.0 + 11.0, 3)
n = n + 0.25 * wisp / wisp.std()

deg = np.degrees(np.abs(LAT))
# Cloudiness by latitude: + equator, - subtropics (~25 deg), + storm tracks (~55 deg), - poles.
bias = (0.6 * np.exp(-(deg / 8) ** 2) - 0.75 * np.exp(-((deg - 24) / 9) ** 2)
        + 0.6 * np.exp(-((deg - 56) / 10) ** 2) - 0.9 * np.clip((deg - 70) / 18, 0, 1))
cover = n + bias
lo, hi = 0.35, 2.4                        # cover below lo: clear sky; soft ramp up to hi
alpha = np.clip((cover - lo) / (hi - lo), 0, 1)
alpha = np.sqrt(alpha) * 0.85             # thin cloud shows, thick cloud stays translucent

rgba = np.empty((height, WIDTH, 4), np.uint8)
rgba[..., :3] = 255
rgba[..., 3] = (alpha * 255).round().astype(np.uint8)
OUT.parent.mkdir(parents=True, exist_ok=True)
Image.fromarray(rgba, "RGBA").save(OUT, optimize=True)
print(f"{OUT} {WIDTH}x{height}, cloudy {(alpha > 0.1).mean():.0%}, mean alpha {alpha.mean():.2f}")
