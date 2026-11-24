"""Original artwork for SOD Performance (no game assets): a noir skyline and a frame-time trace
that goes from stuttering (left) to smooth (right). Writes preview (512) and icon (256)."""
import math, random, sys
from PIL import Image, ImageDraw, ImageFilter, ImageFont

S = 1024  # draw at 2x, downscale for clean edges
F = "/usr/share/fonts/opentype/inter/"
random.seed(7)

def font(name, size): return ImageFont.truetype(F + name, size)

def base():
    img = Image.new("RGB", (S, S))
    px = img.load()
    for y in range(S):  # night-sky gradient
        t = y / S
        r, g, b = int(10 + 8 * t), int(12 + 10 * t), int(24 + 20 * t)
        for x in range(S): px[x, y] = (r, g, b)
    d = ImageDraw.Draw(img)
    # skyline: three depth layers of blocks with a few lit windows
    for layer, (col, top, hmin, hmax) in enumerate([((22, 26, 44), 470, 120, 330), ((16, 19, 33), 560, 90, 300), ((11, 13, 23), 650, 60, 260)]):
        x = -20
        while x < S:
            w = random.randint(60, 140); h = random.randint(hmin, hmax)
            y0 = top + 300 - h
            d.rectangle([x, y0, x + w, S], fill=col)
            if layer < 2:
                for wy in range(y0 + 14, S - 40, 26):
                    for wx in range(x + 10, x + w - 14, 22):
                        if random.random() < 0.10:
                            d.rectangle([wx, wy, wx + 9, wy + 13], fill=(196, 164, 92) if random.random() < .8 else (120, 170, 190))
            x += w + random.randint(4, 18)
    # rain
    rain = Image.new("RGBA", (S, S), (0, 0, 0, 0)); rd = ImageDraw.Draw(rain)
    for _ in range(380):
        x, y, l = random.randint(-100, S), random.randint(-50, S), random.randint(18, 46)
        rd.line([x, y, x + l * 0.25, y + l], fill=(170, 190, 220, random.randint(18, 50)), width=2)
    img = Image.alpha_composite(img.convert("RGBA"), rain)
    # darken bottom for legibility
    shade = Image.new("RGBA", (S, S), (0, 0, 0, 0)); sd = ImageDraw.Draw(shade)
    for y in range(S):
        a = int(max(0, (y - 560) / (S - 560)) ** 1.3 * 200)
        sd.line([0, y, S, y], fill=(6, 7, 14, a))
    return Image.alpha_composite(img, shade)

def trace(img, y_mid, amp, width_px, x0=60, x1=S - 60, top=0):
    """Frame-time line: spiky on the left, calm on the right."""
    pts_l, pts_r = [], []
    n = 160
    for i in range(n + 1):
        x = x0 + (x1 - x0) * i / n
        t = i / n
        if t < 0.5:
            spike = (random.random() ** 3) * amp * (1.6 if random.random() < .12 else 1)
            y = max(top, y_mid - spike + random.uniform(-6, 6))
            pts_l.append((x, y))
        else:
            y = y_mid + math.sin(i * 0.7) * 2.5
            if not pts_r: pts_r.append(pts_l[-1])
            pts_r.append((x, y))
    glow = Image.new("RGBA", img.size, (0, 0, 0, 0)); gd = ImageDraw.Draw(glow)
    gd.line(pts_l, fill=(255, 120, 60, 255), width=width_px * 3, joint="curve")
    gd.line(pts_r, fill=(60, 230, 210, 255), width=width_px * 3, joint="curve")
    glow = glow.filter(ImageFilter.GaussianBlur(14))
    img = Image.alpha_composite(img, glow)
    d = ImageDraw.Draw(img)
    d.line(pts_l, fill=(255, 150, 90, 255), width=width_px, joint="curve")
    d.line(pts_r, fill=(120, 255, 235, 255), width=width_px, joint="curve")
    return img

def preview(path):
    img = base()
    img = trace(img, y_mid=680, amp=170, width_px=7, top=500)
    d = ImageDraw.Draw(img)
    d.text((66, 70), "SHADOWS OF DOUBT  ·  MOD", font=font("Inter-SemiBold.otf", 34), fill=(150, 165, 195))
    d.text((60, 112), "SOD", font=font("InterDisplay-Black.otf", 230), fill=(240, 236, 226))
    d.text((64, 352), "PERFORMANCE", font=font("InterDisplay-Black.otf", 112), fill=(240, 236, 226))
    d.text((66, 905), "more FPS  ·  fewer stutters  ·  faster loading", font=font("Inter-Medium.otf", 38), fill=(205, 215, 230))
    img.convert("RGB").resize((512, 512), Image.LANCZOS).save(path, optimize=True)

def icon(path):
    img = base()
    img = trace(img, y_mid=880, amp=230, width_px=12, top=640)
    d = ImageDraw.Draw(img)
    f = font("InterDisplay-Black.otf", 330)
    w = d.textlength("SOD", font=f)
    d.text(((S - w) / 2, 120), "SOD", font=f, fill=(240, 236, 226))
    f2 = font("InterDisplay-Black.otf", 120)
    w2 = d.textlength("PERF", font=f2)
    d.text(((S - w2) / 2, 470), "PERF", font=f2, fill=(120, 255, 235))
    img.convert("RGB").resize((256, 256), Image.LANCZOS).save(path, optimize=True)

preview(sys.argv[1]); icon(sys.argv[2])
