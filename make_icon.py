"""Generates src/DeviceMaster.Ui/DeviceMaster.ico (16/20/24/32/40/48/64/128/256, PNG-compressed).

The "Thermal tile" mark (option E4): a rounded tile filled with the thermal gradient, cold
blue bottom-left to hot orange top-right, with a D sliced into four heatsink fins cut out of
it in dark steel. Each size is drawn at 8x and downsampled; sizes <= 24 px keep a 1 px
transparent border so the tile doesn't look cut off where Windows draws it flush to an edge.

    pip install pillow
    python make_icon.py            # writes the .ico and dist/icon-preview.png
"""

from io import BytesIO
from pathlib import Path
import struct

from PIL import Image, ImageChops, ImageDraw

ROOT = Path(__file__).resolve().parent
ICO = ROOT / "src" / "DeviceMaster.Ui" / "DeviceMaster.ico"
PREVIEW = ROOT / "dist" / "icon-preview.png"
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SS = 8  # supersampling factor

# palette (matches Theme.cs)
STEEL = (0x0F, 0x11, 0x15)
COLD = (0x4F, 0x8C, 0xFF)
HOT = (0xFF, 0x7A, 0x45)
WARM = (0xD0, 0x6C, 0xD8)


def lerp(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def thermal(t: float):
    """Cold blue -> magenta -> hot orange; a straight blue/orange blend goes grey in the middle."""
    t = min(max(t, 0.0), 1.0)
    return lerp(COLD, WARM, t / 0.5) if t < 0.5 else lerp(WARM, HOT, (t - 0.5) / 0.5)


def draw(size: int) -> Image.Image:
    s = size * SS
    u = s / 64.0  # design grid is 64x64, like the SVG concept

    # tile: the thermal gradient itself, cold (bottom-left) -> hot (top-right)
    small = Image.new("RGBA", (128, 128))
    px = small.load()
    for y in range(128):
        for x in range(128):
            px[x, y] = thermal((x + (127 - y)) / 254) + (255,)
    tile = small.resize((s, s), Image.BICUBIC)
    mask = Image.new("L", (s, s), 0)
    # small sizes keep a 1 px transparent border: the title bar and tray draw the icon flush
    # against an edge, and a full-bleed tile looks cut off there
    inset = SS if size <= 24 else 2 * u
    ImageDraw.Draw(mask).rounded_rectangle([inset, inset, s - 1 - inset, s - 1 - inset], radius=14 * u, fill=255)
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    img.paste(tile, (0, 0), mask)

    # a D (flat back, round front) sliced into four heatsink fins, cut out in dark steel
    top, bottom, left, fins, gap = 12, 52, 11.5, 4, 3.0
    r = (bottom - top) / 2
    right = left + r * 2.05
    pitch = (right - left + gap) / fins
    width = pitch - gap
    shape = Image.new("L", (s, s), 0)
    sd = ImageDraw.Draw(shape)
    cx = right - r
    sd.rectangle([left * u, top * u, cx * u, bottom * u], fill=255)
    sd.ellipse([(cx - r) * u, top * u, (cx + r) * u, bottom * u], fill=255)
    slots = Image.new("L", (s, s), 0)
    for i in range(fins):
        x = left + i * pitch
        ImageDraw.Draw(slots).rounded_rectangle([x * u, top * u, (x + width) * u, bottom * u],
                                                radius=width * 0.3 * u, fill=255)
    cut = ImageChops.multiply(ImageChops.multiply(shape, slots), mask)
    img.paste(Image.new("RGBA", (s, s), STEEL + (255,)), (0, 0), cut)

    return img.resize((size, size), Image.LANCZOS)


def write_ico(images, path: Path):
    blobs = []
    for im in images:
        buf = BytesIO()
        im.save(buf, format="PNG")
        blobs.append(buf.getvalue())
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    for im, blob in zip(images, blobs):
        w = im.width if im.width < 256 else 0
        entries += struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    path.write_bytes(header + entries + b"".join(blobs))


def main():
    images = [draw(size) for size in SIZES]
    write_ico(images, ICO)

    # preview strip on dark and light backgrounds
    PREVIEW.parent.mkdir(exist_ok=True)
    width = sum(SIZES) + 12 * (len(SIZES) + 1)
    sheet = Image.new("RGBA", (width, 2 * (256 + 24)), (0x20, 0x20, 0x20, 255))
    ImageDraw.Draw(sheet).rectangle([0, 256 + 24, width, sheet.height], fill=(0xF3, 0xF3, 0xF3, 255))
    for row in range(2):
        x = 12
        for im in images:
            sheet.alpha_composite(im, (x, row * 280 + 12 + 256 - im.height))
            x += im.width + 12
    sheet.save(PREVIEW)
    print(f"ICO written: {ICO} ({ICO.stat().st_size} bytes); preview: {PREVIEW}")


if __name__ == "__main__":
    main()
