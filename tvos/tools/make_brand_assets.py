#!/usr/bin/env python3
"""Generate the tvOS brand assets (layered app icon + top shelf art).

Apple rejects a tvOS upload without these, and they are layered: the icon is a stack of
Front/Middle/Back images that the TV parallaxes as focus moves. Each layer is drawn
separately here — background, glowing orb, wordmark — so the effect works instead of
being a flat picture cut into three.

Re-run after changing the artwork:  python3 tools/make_brand_assets.py
"""
import json
import os
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "EQAvatarTV", "Assets.xcassets")
BRAND = os.path.join(ROOT, "App Icon & Top Shelf Image.brandassets")

BG = (11, 15, 22)
ACCENT = (79, 195, 247)
TEXT = (230, 237, 243)

INFO = {"version": 1, "author": "xcode"}


def font(size):
    """A bold sans face. Runs on the macOS CI runner as well as Linux, so both sets of
    system font paths are tried before falling back."""
    for path in (
        # macOS (CI runner)
        "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
        "/Library/Fonts/Arial Bold.ttf",
        "/System/Library/Fonts/HelveticaNeue.ttc",
        # Linux
        "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
    ):
        if os.path.exists(path):
            try:
                return ImageFont.truetype(path, size)
            except Exception:
                continue
    return ImageFont.load_default(size)


def write_json(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        json.dump(data, f, indent=2)


def orb(size, cx, cy, r):
    """The EQ Avatar orb: a bright core falling off into the accent blue, plus a glow."""
    img = Image.new("RGBA", size, (0, 0, 0, 0))
    glow = Image.new("RGBA", size, (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse([cx - r * 1.18, cy - r * 1.18, cx + r * 1.18, cy + r * 1.18],
                                 fill=ACCENT + (70,))
    img.alpha_composite(glow.filter(ImageFilter.GaussianBlur(r * 0.30)))

    ball = Image.new("RGBA", size, (0, 0, 0, 0))
    d = ImageDraw.Draw(ball)
    steps = max(24, int(r))
    for i in range(steps, 0, -1):
        t = i / steps
        # highlight sits up and left, so the sphere reads as lit rather than flat
        px = cx - r * 0.22 * (1 - t)
        py = cy - r * 0.26 * (1 - t)
        rr = r * t
        mix = (1 - t) ** 1.5
        col = (
            int(ACCENT[0] + (255 - ACCENT[0]) * mix),
            int(ACCENT[1] + (255 - ACCENT[1]) * mix),
            int(ACCENT[2] + (255 - ACCENT[2]) * mix),
            255,
        )
        d.ellipse([px - rr, py - rr, px + rr, py + rr], fill=col)
    img.alpha_composite(ball)
    return img


def layer_back(w, h):
    img = Image.new("RGBA", (w, h), BG + (255,))
    # a soft wash in the upper-left corner, blurred so the flat background has depth
    wash = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    ImageDraw.Draw(wash).ellipse([-w * 0.25, -h * 0.55, w * 0.60, h * 0.75],
                                 fill=ACCENT + (46,))
    img.alpha_composite(wash.filter(ImageFilter.GaussianBlur(max(8, h * 0.16))))
    return img


def layer_middle(w, h):
    return orb((w, h), cx=w * 0.235, cy=h * 0.50, r=h * 0.205)


def layer_front(w, h):
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    f1 = font(int(h * 0.235))
    f2 = font(int(h * 0.115))
    x = w * 0.42
    d.text((x, h * 0.415), "EQ", font=f1, fill=TEXT + (255,), anchor="lm")
    d.text((x, h * 0.645), "AVATAR", font=f2, fill=TEXT + (235,), anchor="lm")
    return img


def imageset(path, sizes, draw):
    """sizes: [(scale, (w, h)), ...]"""
    images = []
    for scale, (w, h) in sizes:
        name = f"image{scale}x.png"
        draw(w, h).convert("RGB").save(os.path.join(path, name))
        images.append({"idiom": "tv", "filename": name, "scale": f"{scale}x"})
    write_json(os.path.join(path, "Contents.json"), {"images": images, "info": INFO})


def imagestack(path, base_size, marketing=False):
    scales = [(1, base_size)] if marketing else [
        (1, base_size),
        (2, (base_size[0] * 2, base_size[1] * 2)),
    ]
    # ALWAYS "tv" — including the 1280x768 App Store stack. Apple's own catalogs use "tv"
    # here; "tv-marketing" makes actool file those renditions under idiom "marketing", and
    # the App Store validator then reports "Missing App Store Icon" (error 90471) even
    # though the image is plainly in the bundle.
    idiom = "tv"
    for name, fn in (("Front", layer_front), ("Middle", layer_middle), ("Back", layer_back)):
        layer_dir = os.path.join(path, f"{name}.imagestacklayer")
        content = os.path.join(layer_dir, "Content.imageset")
        os.makedirs(content, exist_ok=True)
        images = []
        for scale, (w, h) in scales:
            fname = f"{name.lower()}{scale}x.png"
            img = fn(w, h)
            # Only the back layer is opaque; the others must keep alpha for parallax.
            (img.convert("RGB") if name == "Back" else img).save(os.path.join(content, fname))
            images.append({"idiom": idiom, "filename": fname, "scale": f"{scale}x"})
        write_json(os.path.join(content, "Contents.json"), {"images": images, "info": INFO})
        write_json(os.path.join(layer_dir, "Contents.json"), {"info": INFO})

    # First entry is the front-most layer.
    write_json(os.path.join(path, "Contents.json"), {
        "layers": [
            {"filename": "Front.imagestacklayer"},
            {"filename": "Middle.imagestacklayer"},
            {"filename": "Back.imagestacklayer"},
        ],
        "info": INFO,
    })


def top_shelf(w, h):
    img = layer_back(w, h)
    img.alpha_composite(orb((w, h), cx=w * 0.115, cy=h * 0.5, r=h * 0.175))
    d = ImageDraw.Draw(img)
    d.text((w * 0.22, h * 0.40), "EQ AVATAR", font=font(int(h * 0.20)), fill=TEXT + (255,), anchor="lm")
    d.text((w * 0.22, h * 0.62), "Watch your character live", font=font(int(h * 0.093)),
           fill=(154, 167, 180, 255), anchor="lm")
    return img


def main():
    write_json(os.path.join(ROOT, "Contents.json"), {"info": INFO})

    imagestack(os.path.join(BRAND, "App Icon.imagestack"), (400, 240))
    imagestack(os.path.join(BRAND, "App Icon - App Store.imagestack"), (1280, 768), marketing=True)

    ts = os.path.join(BRAND, "Top Shelf Image.imageset")
    os.makedirs(ts, exist_ok=True)
    imageset(ts, [(1, (1920, 720)), (2, (3840, 1440))], top_shelf)

    tsw = os.path.join(BRAND, "Top Shelf Image Wide.imageset")
    os.makedirs(tsw, exist_ok=True)
    imageset(tsw, [(1, (2320, 720)), (2, (4640, 1440))], top_shelf)

    write_json(os.path.join(BRAND, "Contents.json"), {
        "assets": [
            {"filename": "App Icon.imagestack", "idiom": "tv", "role": "primary-app-icon", "size": "400x240"},
            {"filename": "App Icon - App Store.imagestack", "idiom": "tv",
             "role": "primary-app-icon", "size": "1280x768"},
            {"filename": "Top Shelf Image Wide.imageset", "idiom": "tv",
             "role": "top-shelf-image-wide", "size": "2320x720"},
            {"filename": "Top Shelf Image.imageset", "idiom": "tv",
             "role": "top-shelf-image", "size": "1920x720"},
        ],
        "info": INFO,
    })
    print("brand assets written to", BRAND)


if __name__ == "__main__":
    main()
