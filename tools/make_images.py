"""Generate Southerly's logos, widget picker images, weather icons and backgrounds.

Usage (from the repo root):  python tools/make_images.py
Requires Pillow and the Segoe UI Variable font that ships with Windows 11.
"""

import math
import os

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Southerly")
ASSETS = os.path.join(ROOT, "Assets")
PROVIDER = os.path.join(ROOT, "ProviderAssets")
PHOTOS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "backgrounds")
FONT = "C:/Windows/Fonts/SegUIVar.ttf"

SUN_TOP = (255, 196, 61)
SUN_BOTTOM = (255, 132, 20)
TILE_TOP = (16, 76, 160)
TILE_BOTTOM = (35, 150, 214)
WIND_ON_LIGHT = (24, 112, 196)   # wind colour when there is no tile behind it

S = 1024  # master canvas size


WEIGHTS = {"Regular": 400, "Semibold": 600}


def font(size, weight="Regular"):
    """Segoe UI Variable; "Display"/"Text" suffix picks the optical size."""
    name, _, optical = weight.partition(" ")
    f = ImageFont.truetype(FONT, size)
    f.set_variation_by_axes([WEIGHTS[name], 36 if optical == "Display" else 10.5])
    return f


def vgradient(size, top, bottom):
    w, h = size
    grad = Image.new("RGB", (1, h))
    for y in range(h):
        t = y / max(h - 1, 1)
        grad.putpixel((0, y), tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)))
    return grad.resize((w, h))


def wind_mask(width):
    """Three wind streaks, the top two ending in curls (a southerly change)."""
    m = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(m)
    r = width / 2

    def cap(x, y):
        d.ellipse((x - r, y - r, x + r, y + r), fill=255)

    def line(x1, y, x2):
        d.line((x1, y, x2, y), fill=255, width=width)
        cap(x1, y)
        cap(x2, y)

    def curl(cx, cy, rad, start, end):
        # PIL arcs run clockwise from start to end (degrees, 0 = 3 o'clock).
        d.arc((cx - rad - r, cy - rad - r, cx + rad + r, cy + rad + r),
              start, end, fill=255, width=width)
        a = math.radians(start)
        cap(cx + rad * math.cos(a), cy + rad * math.sin(a))

    # top streak curls up and back over itself
    line(150, 560, 600)
    curl(600, 470, 90, 200, 450)
    # long middle streak curls down
    line(110, 700, 790)
    curl(790, 790, 90, 135, 270)  # drawn from 135 to 270 => ends at the line
    # short bottom streak
    line(260, 840, 560)
    return m


def sun_mask():
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).ellipse((470, 150, 870, 550), fill=255)
    return m


def glyph(tile):
    """The Southerly mark at S x S. tile=True draws it on the blue rounded tile."""
    stroke = 74
    wind = wind_mask(stroke)
    gap = wind.filter(ImageFilter.MaxFilter(41))  # knock the sun out around the wind
    sun = ImageChops.subtract(sun_mask(), gap)

    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    if tile:
        bg = vgradient((S, S), TILE_TOP, TILE_BOTTOM).convert("RGBA")
        rr = Image.new("L", (S, S), 0)
        ImageDraw.Draw(rr).rounded_rectangle((0, 0, S - 1, S - 1), radius=200, fill=255)
        img.paste(bg, (0, 0), rr)
    img.paste(vgradient((S, S), SUN_TOP, SUN_BOTTOM).convert("RGBA"), (0, 0), sun)
    wind_colour = (255, 255, 255) if tile else WIND_ON_LIGHT
    img.paste(Image.new("RGBA", (S, S), wind_colour + (255,)), (0, 0), wind)
    return img


def mono_glyph():
    """White silhouette for the lock screen."""
    stroke = 74
    wind = wind_mask(stroke)
    sun = ImageChops.subtract(sun_mask(), wind.filter(ImageFilter.MaxFilter(41)))
    alpha = ImageChops.lighter(sun, wind)
    img = Image.new("RGBA", (S, S), (255, 255, 255, 0))
    img.putalpha(alpha)
    return img


def fit(img, size, pad=0.0):
    """Scale a square master into a canvas of `size`, centred, with padding."""
    w, h = size
    side = round(min(w, h) * (1 - 2 * pad))
    out = Image.new("RGBA", size, (0, 0, 0, 0))
    out.alpha_composite(img.resize((side, side), Image.LANCZOS), ((w - side) // 2, (h - side) // 2))
    return out


def with_wordmark(mark, size, mark_frac, text_colour):
    """Logo mark plus the word 'Southerly', drawn at 4x then downscaled."""
    k = 4
    w, h = size[0] * k, size[1] * k
    out = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    side = round(h * mark_frac)
    f = font(round(side * 0.42), "Semibold Display")
    text = "Southerly"
    tw = f.getbbox(text)[2]
    gap = round(side * 0.18)
    x = (w - (side + gap + tw)) // 2
    out.alpha_composite(mark.resize((side, side), Image.LANCZOS), (x, (h - side) // 2))
    d = ImageDraw.Draw(out)
    d.text((x + side + gap, h // 2), text, font=f, fill=text_colour, anchor="lm")
    return out.resize(size, Image.LANCZOS)


# ---------------------------------------------------------------- weather icons

ICON = 96          # px; the widget shows them at 24-64px
K = 4              # supersampling


def grad_fill(img, mask, top, bottom):
    """Fill `mask` with a vertical gradient spanning the mask's own bounds."""
    box = mask.getbbox()
    if box:
        grad = vgradient((box[2] - box[0], box[3] - box[1]), top, bottom).convert("RGBA")
        img.paste(grad, box[:2], mask.crop(box))


def shape(n, fn):
    m = Image.new("L", (n, n), 0)
    fn(ImageDraw.Draw(m))
    return m


def dot(d, x, y, r):
    d.ellipse((x - r, y - r, x + r, y + r), fill=255)


def sun(img, u, cx, cy, r):
    def draw(d):
        dot(d, cx * u, cy * u, r * u)
        for i in range(8):
            a = math.radians(i * 45)
            x1, y1 = (cx + math.cos(a) * r * 1.4) * u, (cy + math.sin(a) * r * 1.4) * u
            x2, y2 = (cx + math.cos(a) * r * 1.75) * u, (cy + math.sin(a) * r * 1.75) * u
            d.line((x1, y1, x2, y2), fill=255, width=round(r * .3 * u))
            dot(d, x1, y1, r * .15 * u)
            dot(d, x2, y2, r * .15 * u)
    grad_fill(img, shape(img.width, draw), SUN_TOP, SUN_BOTTOM)


def moon(img, u, cx, cy, r):
    # a disc with a bite knocked out of its top right
    m = shape(img.width, lambda d: dot(d, cx * u, cy * u, r * u))
    bite = shape(img.width, lambda d: dot(d, (cx + r * .45) * u, (cy - r * .5) * u, r * .85 * u))
    grad_fill(img, ImageChops.subtract(m, bite), (255, 238, 172), (238, 188, 86))


def cloud(img, u, x, y, w, top=(255, 255, 255), bottom=(196, 208, 224)):
    """A cloud with its top-left at (x, y) and width w, soft shadow underneath."""
    def draw(d):
        d.rounded_rectangle((x * u, (y + .30 * w) * u, (x + w) * u, (y + .60 * w) * u), radius=.15 * w * u, fill=255)
        for cx, cy, r in ((.30, .32, .20), (.55, .24, .26), (.78, .38, .18)):
            dot(d, (x + cx * w) * u, (y + cy * w) * u, r * w * u)
    m = shape(img.width, draw)
    img.paste(Image.new("RGBA", img.size, (0, 0, 0, 70)), (0, round(1.2 * u)), m.filter(ImageFilter.GaussianBlur(1.5 * u)))
    grad_fill(img, m, top, bottom)


def strokes(img, u, lines, colour, width):
    """Rounded line segments [(x1, y1, x2, y2), ...] in 100-unit space."""
    def draw(d):
        for x1, y1, x2, y2 in lines:
            d.line((x1 * u, y1 * u, x2 * u, y2 * u), fill=255, width=round(width * u))
            dot(d, x1 * u, y1 * u, width / 2 * u)
            dot(d, x2 * u, y2 * u, width / 2 * u)
    img.paste(Image.new("RGBA", img.size, colour + (255,)), (0, 0), shape(img.width, draw))


RAIN = (64, 152, 238)
GREY = ((206, 216, 230), (140, 154, 174))
DARK = ((168, 180, 198), (96, 110, 132))


def drops(img, u, xs, y, length):
    strokes(img, u, [(x, y, x - length * .3, y + length) for x in xs], RAIN, 5.5)


def flake(img, u, cx, cy, r, colour=(120, 190, 242), width=4):
    arms = [(cx + math.cos(math.radians(a)) * r, cy + math.sin(math.radians(a)) * r,
             cx - math.cos(math.radians(a)) * r, cy - math.sin(math.radians(a)) * r) for a in (0, 60, 120)]
    strokes(img, u, arms, colour, width)


def draw_icon(name):
    n = ICON * K
    u = n / 100
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    if name == "sunny":
        sun(img, u, 50, 50, 20)
    elif name == "clear":
        moon(img, u, 48, 52, 30)
        for x, y, r in ((74, 28, 3.5), (82, 50, 2.5), (68, 74, 2.5)):
            grad_fill(img, shape(n, lambda d: dot(d, x * u, y * u, r * u)), (255, 238, 172), (238, 188, 86))
    elif name == "partly":
        sun(img, u, 37, 36, 16)
        cloud(img, u, 22, 42, 68)
    elif name == "partly_night":
        moon(img, u, 38, 38, 22)
        cloud(img, u, 22, 42, 68)
    elif name == "cloudy":
        cloud(img, u, 36, 20, 56, *GREY)
        cloud(img, u, 8, 36, 72)
    elif name in ("haze", "dust"):
        tint = (206, 160, 96) if name == "dust" else (150, 170, 192)
        sun(img, u, 50, 38, 17)
        strokes(img, u, [(18, 68, 82, 68), (28, 79, 72, 79), (38, 90, 62, 90)], tint, 5.5)
    elif name == "fog":
        cloud(img, u, 14, 12, 72, *GREY)
        strokes(img, u, [(16, 66, 84, 66), (26, 77, 74, 77), (36, 88, 64, 88)], (140, 170, 205), 5.5)
    elif name == "shower":
        cloud(img, u, 14, 14, 72, *GREY)
        drops(img, u, (36, 52, 68), 66, 14)
    elif name == "rain":
        cloud(img, u, 14, 10, 72, *DARK)
        drops(img, u, (30, 46, 62, 78), 62, 24)
    elif name == "storm":
        cloud(img, u, 14, 8, 72, *DARK)
        drops(img, u, (28, 74), 62, 20)
        bolt = [(54, 52), (40, 74), (50, 74), (44, 94), (66, 66), (55, 66), (62, 52)]
        grad_fill(img, shape(n, lambda d: d.polygon([(x * u, y * u) for x, y in bolt], fill=255)), (255, 224, 80), (255, 166, 30))
    elif name == "snow":
        cloud(img, u, 14, 12, 72, *GREY)
        for x in (30, 50, 70):
            flake(img, u, x, 76, 8, (150, 205, 250), 3.5)
    elif name == "frost":
        flake(img, u, 50, 50, 34, width=7)
    elif name == "wind":
        wind = wind_mask(74).filter(ImageFilter.GaussianBlur(2)).resize((n, n), Image.LANCZOS)
        img.paste(Image.new("RGBA", (n, n), (48, 140, 224, 255)), (0, 0), wind)
    return img.resize((ICON, ICON), Image.LANCZOS)


ICON_NAMES = "sunny clear partly partly_night cloudy haze dust fog shower rain storm snow frost wind".split()


def icons():
    out = os.path.join(ASSETS, "Icons")
    os.makedirs(out, exist_ok=True)
    for name in ICON_NAMES:
        # a palette keeps each icon to a few KB, as they are sent inline many times
        draw_icon(name).quantize(256, method=Image.FASTOCTREE).save(os.path.join(out, name + ".png"), optimize=True)


# ---------------------------------------------------------------- backgrounds

BG_W, BG_H = 450, 696   # 1.5x the largest widget (300x464), cropped to its aspect ratio
BG_NAMES = "sunny partly_cloudy cloudy rain storm snow fog night".split()


def background(name, dark):
    """The photo cropped to widget proportions, with a vertical scrim matching the Windows theme."""
    photo = Image.open(os.path.join(PHOTOS, name + ".jpg")).convert("RGB")
    scale = max(BG_W / photo.width, BG_H / photo.height)
    photo = photo.resize((round(photo.width * scale), round(photo.height * scale)), Image.LANCZOS)
    left, top = (photo.width - BG_W) // 2, (photo.height - BG_H) // 2
    bg = photo.crop((left, top, left + BG_W, top + BG_H)).convert("RGBA")
    # dark: ~45% black rising to ~65%; light: ~65% white rising to ~78%, as dark text needs a paler base
    tint, a0, a1 = ((0, 0, 0), 115, 165) if dark else ((243, 243, 243), 165, 200)
    scrim = Image.new("RGBA", (BG_W, BG_H))
    for y in range(BG_H):
        ImageDraw.Draw(scrim).line((0, y, BG_W, y), fill=tint + (round(a0 + (a1 - a0) * y / (BG_H - 1)),))
    bg.alpha_composite(scrim)
    return bg.convert("RGB")


def backgrounds():
    out = os.path.join(ASSETS, "Backgrounds")
    os.makedirs(out, exist_ok=True)
    for name in BG_NAMES:
        for dark in (False, True):
            background(name, dark).save(os.path.join(out, f"{name}_{'dark' if dark else 'light'}.jpg"),
                                        quality=72, optimize=True, progressive=True)


# ---------------------------------------------------------------- picker preview

def preview(dark):
    """Mock of the medium widget's Now page, 300x304 as the picker expects."""
    k = 3
    W, H = 300 * k, 304 * k
    bg = background("partly_cloudy", dark).resize((W, round(BG_H * W / BG_W)), Image.LANCZOS)
    top = (bg.height - H) // 2
    bg = bg.crop((0, top, W, top + H)).convert("RGBA")
    icon = Image.open(os.path.join(ASSETS, "Icons", "partly.png")).convert("RGBA")

    fg = (255, 255, 255) if dark else (26, 26, 26)
    subtle = (200, 200, 200) if dark else (92, 92, 92)
    accent = (76, 194, 255) if dark else (0, 95, 184)
    line_c = (255, 255, 255, 60) if dark else (0, 0, 0, 40)

    d = ImageDraw.Draw(bg)
    pad = 16 * k

    # host-drawn header: small icon + location
    y = 12 * k
    bg.alpha_composite(icon.resize((18 * k, 18 * k), Image.LANCZOS), (pad, y))
    d.text((pad + 24 * k, y + 9 * k), "Scoresby, VIC", font=font(13 * k, "Regular Text"), fill=fg, anchor="lm")

    # tabs
    y = 44 * k
    x = pad
    for i, label in enumerate(["Now", "Hourly", "Daily"]):
        sel = i == 0
        f = font(14 * k, "Semibold Text" if sel else "Regular Text")
        d.text((x, y), label, font=f, fill=accent if sel else subtle, anchor="lm")
        x += f.getbbox(label)[2] + 20 * k
    pin = ImageFont.truetype("C:/Windows/Fonts/seguiemj.ttf", 13 * k)
    change_f = font(12 * k, "Regular Text")
    cw = change_f.getbbox("Change")[2]
    d.text((W - pad, y), "Change", font=change_f, fill=subtle, anchor="rm")
    d.text((W - pad - cw - 4 * k, y), "📍", font=pin, embedded_color=True, anchor="rm")
    d.line((pad, y + 16 * k, W - pad, y + 16 * k), fill=line_c, width=k)

    # current conditions
    y = 74 * k
    bg.alpha_composite(icon.resize((64 * k, 64 * k), Image.LANCZOS), (pad, y + 2 * k))
    tx = pad + 76 * k
    d.text((tx, y - 4 * k), "24°", font=font(40 * k, "Semibold Display"), fill=fg)
    d.text((tx, y + 46 * k), "Partly cloudy", font=font(14 * k, "Regular Text"), fill=fg)
    d.text((tx, y + 66 * k), "Feels like 22°", font=font(12 * k, "Regular Text"), fill=subtle)
    d.text((W - pad, y + 6 * k), "H 27°", font=font(14 * k, "Semibold Text"), fill=fg, anchor="ra")
    d.text((W - pad, y + 28 * k), "L 13°", font=font(14 * k, "Regular Text"), fill=subtle, anchor="ra")

    # stat tiles
    y = 178 * k
    col = (W - 2 * pad) / 3
    for i, (val, lab) in enumerate([("58%", "Humidity"), ("19 km/h S", "Wind"), ("0.4 mm", "Since 9am")]):
        x = pad + i * col
        d.text((x, y), val, font=font(14 * k, "Semibold Text"), fill=fg)
        d.text((x, y + 20 * k), lab, font=font(12 * k, "Regular Text"), fill=subtle)

    # station
    d.text((pad, H - 22 * k), "Scoresby · 1.6 km", font=font(12 * k, "Regular Text"), fill=subtle, anchor="ls")

    return bg.resize((300, 304), Image.LANCZOS)


def main():
    tile = glyph(tile=True)
    bare = glyph(tile=False)

    # App / package logos
    fit(tile, (50, 50)).save(os.path.join(ASSETS, "StoreLogo.png"))
    fit(tile, (300, 300), pad=0.17).save(os.path.join(ASSETS, "Square150x150Logo.scale-200.png"))
    fit(tile, (88, 88), pad=0.08).save(os.path.join(ASSETS, "Square44x44Logo.scale-200.png"))
    fit(bare, (24, 24)).save(os.path.join(ASSETS, "Square44x44Logo.targetsize-24_altform-unplated.png"))
    fit(mono_glyph(), (48, 48)).save(os.path.join(ASSETS, "LockScreenLogo.scale-200.png"))
    with_wordmark(tile, (620, 300), 0.42, (255, 255, 255)).save(os.path.join(ASSETS, "Wide310x150Logo.scale-200.png"))
    with_wordmark(tile, (1240, 600), 0.30, (255, 255, 255)).save(os.path.join(ASSETS, "SplashScreen.scale-200.png"))

    icons()
    backgrounds()

    # Widget picker: icon and light/dark previews
    fit(bare, (256, 256)).save(os.path.join(PROVIDER, "Weather_Icon.png"))
    preview(dark=False).save(os.path.join(PROVIDER, "Weather_Screenshot.png"))
    preview(dark=True).save(os.path.join(PROVIDER, "Weather_Screenshot_Dark.png"))


if __name__ == "__main__":
    main()
