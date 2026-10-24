"""Generate Southerly's app logos and widget picker images.

Usage (from the repo root):  python tools/make_images.py
Requires Pillow and the Segoe UI Variable font that ships with Windows 11.
"""

import math
import os

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Southerly")
ASSETS = os.path.join(ROOT, "Assets")
PROVIDER = os.path.join(ROOT, "ProviderAssets")
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


# ---------------------------------------------------------------- picker preview

def partly_cloudy_icon(px):
    """Sun peeking behind a cloud, roughly like the forecast icons the widget uses."""
    k = 4
    n = px * k
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    sun = Image.new("L", (n, n), 0)
    ImageDraw.Draw(sun).ellipse((n * .12, n * .10, n * .62, n * .60), fill=255)
    img.paste(vgradient((n, n), SUN_TOP, SUN_BOTTOM).convert("RGBA"), (0, 0), sun)
    cloud = Image.new("L", (n, n), 0)
    d = ImageDraw.Draw(cloud)
    d.ellipse((n * .30, n * .38, n * .70, n * .78), fill=255)
    d.ellipse((n * .55, n * .48, n * .90, n * .83), fill=255)
    d.ellipse((n * .18, n * .55, n * .48, n * .85), fill=255)
    d.rounded_rectangle((n * .18, n * .62, n * .90, n * .85), radius=n * .11, fill=255)
    shadow = cloud.filter(ImageFilter.GaussianBlur(n * .02))
    img.paste(Image.new("RGBA", (n, n), (0, 0, 0, 40)), (0, round(n * .015)), shadow)
    img.paste(vgradient((n, n), (255, 255, 255), (222, 230, 240)).convert("RGBA"), (0, 0), cloud)
    return img.resize((px, px), Image.LANCZOS)


def preview(dark):
    """Mock of the medium widget's Now page, 300x304 as the picker expects."""
    k = 3
    W, H = 300 * k, 304 * k

    photo = Image.open(os.path.join(ASSETS, "WeatherBackgrounds", "partly_cloudy.jpg")).convert("RGB")
    pw, ph = photo.size
    scale = max(W / pw, H / ph)
    photo = photo.resize((round(pw * scale), round(ph * scale)), Image.LANCZOS)
    left, top = (photo.width - W) // 2, (photo.height - H) // 2
    bg = photo.crop((left, top, left + W, top + H)).convert("RGBA")

    # Same theme-matched overlay as WeatherWidget.ApplyScrim
    tint, a0, a1 = ((0, 0, 0), 115, 165) if dark else ((243, 243, 243), 165, 200)
    scrim = Image.new("RGBA", (W, H))
    for y in range(H):
        a = round(a0 + (a1 - a0) * y / (H - 1))
        ImageDraw.Draw(scrim).line((0, y, W, y), fill=tint + (a,))
    bg.alpha_composite(scrim)

    fg = (255, 255, 255) if dark else (26, 26, 26)
    subtle = (200, 200, 200) if dark else (92, 92, 92)
    accent = (76, 194, 255) if dark else (0, 95, 184)
    line_c = (255, 255, 255, 60) if dark else (0, 0, 0, 40)

    d = ImageDraw.Draw(bg)
    pad = 16 * k

    # host-drawn header: small icon + location
    y = 12 * k
    bg.alpha_composite(partly_cloudy_icon(18 * k), (pad, y))
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
    bg.alpha_composite(partly_cloudy_icon(64 * k), (pad, y + 2 * k))
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
    d.text((pad, H - 22 * k), "Scoresby station · 1.6 km", font=font(12 * k, "Regular Text"), fill=subtle, anchor="ls")

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

    # Widget picker: icon and light/dark previews
    fit(bare, (256, 256)).save(os.path.join(PROVIDER, "Weather_Icon.png"))
    preview(dark=False).save(os.path.join(PROVIDER, "Weather_Screenshot.png"))
    preview(dark=True).save(os.path.join(PROVIDER, "Weather_Screenshot_Dark.png"))


if __name__ == "__main__":
    main()
