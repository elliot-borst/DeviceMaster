"""Generates src/DeviceMaster.Ui/DeviceMaster.ico (16/20/24/32/40/48/64/128/256, PNG-compressed).

The "Thermal" mark: a D sliced into heatsink fins, shaded cold blue to hot orange
like a temperature curve, on a cool-steel rounded tile. Each size is drawn at 8x and
downsampled; the small sizes (<= 24 px) use four wider fins and drop the tile border and base
bar so the D stays
readable in the taskbar and tray.

    pip install pillow
    python make_icon.py            # writes the .ico and dist/icon-preview.png
"""

from io import BytesIO
from pathlib import Path
import struct

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent
ICO = ROOT / "src" / "DeviceMaster.Ui" / "DeviceMaster.ico"
PREVIEW = ROOT / "dist" / "icon-preview.png"
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SS = 8  # supersampling factor

# palette (matches Theme.cs)
TILE_TOP = (0x1D, 0x21, 0x29)
TILE_BOTTOM = (0x0F, 0x11, 0x15)
TILE_EDGE = (0x3C, 0x43, 0x51)
COLD = (0x4F, 0x8C, 0xFF)
HOT = (0xFF, 0x7A, 0x45)
WARM = (0xD0, 0x6C, 0xD8)
BASE = (0xA0, 0xA6, 0xB3)


def lerp(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def thermal(t: float):
    """Cold blue -> magenta -> hot orange; a straight blue/orange blend goes grey in the middle."""
    t = min(max(t, 0.0), 1.0)
    return lerp(COLD, WARM, t / 0.5) if t < 0.5 else lerp(WARM, HOT, (t - 0.5) / 0.5)


def draw(size: int) -> Image.Image:
    s = size * SS
    u = s / 64.0  # design grid is 64x64, like the SVG concept
    small = size <= 24

    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # tile: vertical gradient clipped to a rounded square
    gradient = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(gradient)
    for y in range(s):
        gd.line([(0, y), (s, y)], fill=lerp(TILE_TOP, TILE_BOTTOM, y / (s - 1)) + (255,))
    mask = Image.new("L", (s, s), 0)
    inset = 0 if small else 2 * u
    radius = (12 if small else 14) * u
    ImageDraw.Draw(mask).rounded_rectangle([inset, inset, s - 1 - inset, s - 1 - inset], radius=radius, fill=255)
    img.paste(gradient, (0, 0), mask)
    d = ImageDraw.Draw(img)
    if not small:
        d.rounded_rectangle([inset, inset, s - 1 - inset, s - 1 - inset], radius=radius,
                            outline=TILE_EDGE + (255,), width=max(1, round(1.2 * u)))

    # fins: a D silhouette (flat back, round front) sliced into vertical fins, shaded
    # cold -> hot left to right like a thermal image
    if small:
        top, bottom, left, fins, gap = 9, 49, 9, 4, 3.4
    else:
        top, bottom, left, fins, gap = 12, 44, 15, 5, 2.6
    r = (bottom - top) / 2
    right = left + r * 2.05                   # bowl of the D
    pitch = (right - left + gap) / fins
    width = pitch - gap

    bank = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    bd = ImageDraw.Draw(bank)
    for px in range(int(left * u), int(right * u) + 2):
        bd.line([(px, top * u), (px, bottom * u)], fill=thermal((px / u - left) / (right - left)) + (255,))
    shape = Image.new("L", (s, s), 0)
    sd = ImageDraw.Draw(shape)
    cx = right - r
    sd.rectangle([left * u, top * u, cx * u, bottom * u], fill=255)
    sd.ellipse([(cx - r) * u, top * u, (cx + r) * u, bottom * u], fill=255)
    slots = Image.new("L", (s, s), 0)
    for i in range(fins):
        x = left + i * pitch
        ImageDraw.Draw(slots).rounded_rectangle([x * u, top * u, (x + width) * u, bottom * u],
                                                radius=width * 0.35 * u, fill=255)
    from PIL import ImageChops
    img.paste(bank, (0, 0), ImageChops.multiply(shape, slots))

    if not small:
        d.rounded_rectangle([left * u - 2 * u, 47 * u, right * u + 2 * u, 51.5 * u], radius=2.25 * u, fill=BASE + (255,))

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
