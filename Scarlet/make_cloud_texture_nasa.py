"""Make the Scarlet cloud layer texture from NASA's Blue Marble clouds:
Assets/Resources/Scarlet/clouds.png. Real cloud imagery, nothing generated:
only its brightness is turned into the overlay's transparency.

Source: NASA Visible Earth, "Blue Marble: Clouds" (image record 57747,
cloud_combined_8192.tif, 8192 x 4096; NASA no longer serves it: the Wayback
Machine's copy of
https://eoimages.gsfc.nasa.gov/images/imagerecords/57000/57747/cloud_combined_8192.tif,
captured 2014-07-09, sha256 d137775d...daf0f): a global cloud composite from
MODIS (Terra, 2001), white cloud on black, equirectangular, 2:1,
-180 deg longitude at the left edge and north at the top -- the same layout as
Vizard's Earth map (Textures/CelestialBodies/Earth/8k_earth_daymap.jpg), so the
cloud shell (Earth's own mesh and UVs) lines up with it. NASA imagery is not
copyrighted; credit "NASA Goddard Space Flight Center Image by Reto Stöckli".

Output: RGBA, white, the cloud's brightness as its alpha (what the CloudLayer
material, Standard Fade, expects), 8192 x 4096 by default (as Vizard's Earth
map).

    python make_cloud_texture_nasa.py <cloud_combined_*.jpg|tif> [width]
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None
OUT = Path(__file__).resolve().parent.parent / "VizardUnityProject/Assets/Resources/Scarlet/clouds.png"
CLEAR = 0.06        # brightness at or below this: clear sky (the composite's faint haze)
OPAQUE = 0.85       # brightness at or above this: as thick as the layer is drawn
MAX_ALPHA = 0.95    # the thickest cloud stays slightly translucent


def main() -> None:
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    source = Path(sys.argv[1])
    width = int(sys.argv[2]) if len(sys.argv) > 2 else 8192
    gray = Image.open(source).convert("L")
    if gray.width != 2 * gray.height:
        sys.exit(f"{source}: {gray.width} x {gray.height} is not a 2:1 equirectangular map")
    if gray.width != width:
        gray = gray.resize((width, width // 2), Image.LANCZOS)
    b = np.asarray(gray, dtype=np.float32) / 255.0
    alpha = np.clip((b - CLEAR) / (OPAQUE - CLEAR), 0.0, 1.0) * MAX_ALPHA
    rgba = np.empty(b.shape + (4,), np.uint8)
    rgba[..., :3] = 255
    rgba[..., 3] = (alpha * 255).round().astype(np.uint8)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(rgba, "RGBA").save(OUT, optimize=True)
    print(f"{OUT} {width}x{width // 2} from {source.name}: cloudy {(alpha > 0.1).mean():.0%}, "
          f"mean alpha {alpha.mean():.2f}")


if __name__ == "__main__":
    main()
