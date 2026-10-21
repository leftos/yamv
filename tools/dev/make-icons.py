"""Builds the app icons in src/Yamv.App/Assets/ from the SVG master.

The master is docs/design/mocks/icon/option-b.svg; Assets/yamv.svg is a copy of it beside the binaries. The macOS art
is the whole 1024 canvas (the Big Sur grid, with its transparent margin) and goes into yamv.icns; the Windows art is
the same drawing cropped to the body (viewBox "100 100 824 824") and goes into yamv.ico. Every size is rasterised from
the vector at that size rather than scaled down from a large bitmap. The ICNS set starts at 32: Pillow writes the 16
and 32 point icons only as @2x images (32 and 64 px), so a 16 px image would never be used.

Usage, from the repo root:

    uv run --no-project --with pillow==13.0.0 --with resvg-py==0.5.0 python -I tools/dev/make-icons.py
"""

import io
import re
import shutil
import sys
from pathlib import Path

import resvg_py
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
MASTER = ROOT / "docs" / "design" / "mocks" / "icon" / "option-b.svg"
ASSETS = ROOT / "src" / "Yamv.App" / "Assets"
WINDOWS_VIEWBOX = 'viewBox="100 100 824 824"'
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
ICNS_SIZES = [32, 64, 128, 256, 512, 1024]


def rasterise(svg: str, size: int) -> Image.Image:
    """Renders the SVG text as a size-by-size RGBA image."""
    png = resvg_py.svg_to_bytes(svg_string=svg, width=size, height=size)
    return Image.open(io.BytesIO(bytes(png))).convert("RGBA")


def windows_art(svg: str) -> str:
    """Returns the master with its viewBox cropped to the icon body."""
    cropped, count = re.subn(r'viewBox="[^"]*"', WINDOWS_VIEWBOX, svg, count=1)
    if count != 1:
        sys.exit(f"{MASTER} has no viewBox attribute to crop; the Windows art cannot be built.")
    return cropped


def write_ico(svg: str, path: Path) -> None:
    """Writes a multi-size .ico, each size rasterised on its own."""
    images = [rasterise(svg, size) for size in ICO_SIZES]
    largest = images[-1]
    largest.save(path, format="ICO", sizes=[(s, s) for s in ICO_SIZES], append_images=images[:-1])


def write_icns(svg: str, path: Path) -> None:
    """Writes an .icns with the 32-1024 px set, each size rasterised on its own."""
    images = [rasterise(svg, size) for size in ICNS_SIZES]
    largest = images[-1]
    largest.save(path, format="ICNS", append_images=images[:-1])


def main() -> None:
    if not MASTER.is_file():
        sys.exit(f"The icon master {MASTER} does not exist.")
    ASSETS.mkdir(parents=True, exist_ok=True)
    svg = MASTER.read_text(encoding="utf-8")
    shutil.copyfile(MASTER, ASSETS / "yamv.svg")
    write_ico(windows_art(svg), ASSETS / "yamv.ico")
    write_icns(svg, ASSETS / "yamv.icns")
    for name in ("yamv.svg", "yamv.ico", "yamv.icns"):
        print(f"wrote {ASSETS / name}")


if __name__ == "__main__":
    main()
