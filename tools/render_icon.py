"""Render the project SVG into Windows icon sizes.

Requires: python -m pip install pillow cairosvg
"""

from io import BytesIO
from pathlib import Path

import cairosvg
from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "assets" / "icon.svg"
PREVIEW = ROOT / "assets" / "icon-preview.png"
WINDOWS_ICON = ROOT / "ResourceManager.App" / "Assets" / "ResourceManager.ico"
APP_LOGO = ROOT / "ResourceManager.App" / "Assets" / "ResourceManager.png"


def main() -> None:
    rendered = cairosvg.svg2png(url=str(SOURCE), output_width=1024, output_height=1024)
    with Image.open(BytesIO(rendered)) as original:
        image = original.convert("RGBA")
        preview = image.resize((512, 512), Image.Resampling.LANCZOS)
        preview.save(PREVIEW, format="PNG", optimize=True)
        WINDOWS_ICON.parent.mkdir(parents=True, exist_ok=True)
        image.resize((128, 128), Image.Resampling.LANCZOS).save(APP_LOGO, format="PNG", optimize=True)
        image.save(
            WINDOWS_ICON,
            format="ICO",
            sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
            bitmap_format="png",
        )
    print(f"Created {PREVIEW}, {APP_LOGO}, and {WINDOWS_ICON}")


if __name__ == "__main__":
    main()
