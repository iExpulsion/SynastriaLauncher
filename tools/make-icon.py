"""Draws the launcher's icon: a gold Cinzel "S" on a navy tile with a gold rim, over one of two frost backgrounds.

  star   six ice spikes with side branches, crossing behind the letter
  noise  frost creeping in from the tile's edges, with fine ice sparkles, clear around the letter

Writes app.ico (16 to 256 px) and app.png (256 px, for previews) into src/SynastriaLauncher.App/Assets, or into
the folder given after the style.
Run: uv run --no-project --with pillow python -I -X utf8 tools/make-icon.py [star|noise] [out folder]
"""
import math
import pathlib
import random
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

ROOT = pathlib.Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "SynastriaLauncher.App" / "Assets"
FONT = ASSETS / "Fonts" / "Cinzel-Bold.ttf"
SIZE = 1024

NAVY_TOP = (20, 48, 74)
NAVY_BOTTOM = (11, 18, 32)
FROST = (124, 196, 240)
ICE = (214, 238, 252)
GOLD_TOP = (247, 214, 140)
GOLD_BOTTOM = (205, 148, 52)
RIM = (232, 176, 75)


def vertical_gradient(size, top, bottom):
    gradient = Image.new("RGBA", (1, size))
    for y in range(size):
        t = y / (size - 1)
        gradient.putpixel((0, y), tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)) + (255,))
    return gradient.resize((size, size))


def rounded_mask(size, inset, radius):
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((inset, inset, size - 1 - inset, size - 1 - inset), radius, fill=255)
    return mask


def frost_star(size):
    """Six thin ice spikes with small side branches, soft-glowing."""
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    cx = cy = size / 2
    length = size * 0.40
    width = round(size * 0.022)
    for i in range(6):
        angle = math.radians(90 + i * 60)
        draw.line((cx, cy, cx + length * math.cos(angle), cy - length * math.sin(angle)), fill=FROST + (150,), width=width)
        for along in (0.55, 0.78):
            bx = cx + length * along * math.cos(angle)
            by = cy - length * along * math.sin(angle)
            for side in (-1, 1):
                branch = angle + side * math.radians(40)
                bl = length * 0.16
                draw.line((bx, by, bx + bl * math.cos(branch), by - bl * math.sin(branch)), fill=FROST + (150,), width=width)
    glow = layer.filter(ImageFilter.GaussianBlur(size * 0.012))
    return Image.alpha_composite(glow, layer)


def noise(size, cells, seed, blur):
    """Value noise: random cells scaled up and softened, 0-255."""
    rng = random.Random(seed)
    small = Image.new("L", (cells, cells))
    small.putdata([rng.randrange(256) for _ in range(cells * cells)])
    return small.resize((size, size), Image.BICUBIC).filter(ImageFilter.GaussianBlur(blur))


def frost_noise(size):
    """Frost on glass: layered noise in ice blue, thickest at the tile's edges and clear in the middle, with fine
    sparkles. Seeded, so every run draws the same pattern."""
    edge = Image.new("L", (size, size), 0)
    ImageDraw.Draw(edge).ellipse((size * 0.12, size * 0.12, size * 0.88, size * 0.88), fill=255)
    edge = ImageChops.invert(edge.filter(ImageFilter.GaussianBlur(size * 0.09)))
    texture = ImageChops.add(noise(size, 12, 1, size * 0.01), noise(size, 40, 2, size * 0.003), scale=2)
    texture = ImageChops.add(texture, noise(size, 140, 3, 1), scale=2)
    texture = texture.point(lambda v: max(0, min(255, (v - 92) * 3)))
    cover = ImageChops.multiply(texture, edge).point(lambda v: round(v * 0.75))
    layer = Image.new("RGBA", (size, size), FROST + (0,))
    layer.putalpha(cover)
    rng = random.Random(4)
    sparkles = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(sparkles)
    for _ in range(260):
        x, y = rng.uniform(0, size), rng.uniform(0, size)
        if edge.getpixel((int(x), int(y))) > 90:
            r = rng.uniform(size * 0.002, size * 0.006)
            draw.ellipse((x - r, y - r, x + r, y + r), fill=ICE + (rng.randrange(120, 230),))
    return Image.alpha_composite(layer, sparkles)


def gold_letter(size):
    font = ImageFont.truetype(str(FONT), round(size * 0.66))
    mask = Image.new("L", (size, size), 0)
    draw = ImageDraw.Draw(mask)
    left, top, right, bottom = draw.textbbox((0, 0), "S", font=font)
    draw.text(((size - (right - left)) / 2 - left, (size - (bottom - top)) / 2 - top), "S", font=font, fill=255)
    letter = vertical_gradient(size, GOLD_TOP, GOLD_BOTTOM)
    letter.putalpha(mask)
    shadow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    shadow.putalpha(mask.filter(ImageFilter.GaussianBlur(size * 0.02)).point(lambda a: a * 0.8))
    return Image.alpha_composite(shadow, letter)


def draw_icon(size, style):
    inset = round(size * 0.04)
    radius = round(size * 0.2)
    tile = vertical_gradient(size, NAVY_TOP, NAVY_BOTTOM)
    tile = Image.alpha_composite(tile, frost_star(size) if style == "star" else frost_noise(size))
    tile = Image.alpha_composite(tile, gold_letter(size))
    icon = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    icon.paste(tile, (0, 0), rounded_mask(size, inset, radius))
    rim = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(rim).rounded_rectangle((inset, inset, size - 1 - inset, size - 1 - inset), radius,
                                          outline=RIM + (255,), width=round(size * 0.022))
    return Image.alpha_composite(icon, rim)


def main():
    style = sys.argv[1] if len(sys.argv) > 1 else "noise"
    out = pathlib.Path(sys.argv[2]) if len(sys.argv) > 2 else ASSETS
    out.mkdir(parents=True, exist_ok=True)
    big = draw_icon(SIZE, style)
    sizes = [256, 128, 64, 48, 32, 24, 16]
    big.resize((256, 256), Image.LANCZOS).save(out / "app.png")
    big.save(out / "app.ico", sizes=[(s, s) for s in sizes])
    print(f"wrote {out / 'app.ico'} ({style}; {', '.join(map(str, sizes))} px) and app.png")


if __name__ == "__main__":
    main()
