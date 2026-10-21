# PingWhen icons, drawn with Pillow (py tools/icon.py from the repo root):
# docs/icon.png 256, src/PingWhen/Assets/app.ico (16-256), tray-light.ico (dark glyph) and tray-dark.ico (white glyph).
from PIL import Image, ImageDraw

S = 1024  # drawn large, scaled down for anti-aliasing


def bell(d, cx, cy, s, fill):
    """A bell centred at (cx, cy), s = overall height."""
    w = s * 0.78
    top, bottom = cy - s * 0.42, cy + s * 0.22
    d.ellipse([cx - s * 0.07, top - s * 0.09, cx + s * 0.07, top + s * 0.05], fill=fill)            # knob
    d.chord([cx - w * 0.36, top, cx + w * 0.36, top + w * 0.72], 180, 360, fill=fill)               # dome
    d.polygon([(cx - w * 0.36, top + w * 0.36), (cx + w * 0.36, top + w * 0.36),
               (cx + w * 0.50, bottom), (cx - w * 0.50, bottom)], fill=fill)                         # skirt
    r = s * 0.05
    d.rounded_rectangle([cx - w * 0.56, bottom - r, cx + w * 0.56, bottom + r * 1.4], r, fill=fill)  # rim
    d.ellipse([cx - s * 0.11, bottom + s * 0.07, cx + s * 0.11, bottom + s * 0.27], fill=fill)       # clapper


def app_icon():
    im = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([40, 40, S - 40, S - 40], 200, fill=(0x00, 0x67, 0xC0, 255))
    bell(d, S * 0.47, S * 0.50, S * 0.62, (255, 255, 255, 255))
    # badge: green check, "done"
    cx, cy, r = S * 0.73, S * 0.72, S * 0.17
    d.ellipse([cx - r - 22, cy - r - 22, cx + r + 22, cy + r + 22], fill=(0x00, 0x67, 0xC0, 255))
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(0x2E, 0xB8, 0x5C, 255))
    d.line([(cx - r * 0.48, cy + r * 0.02), (cx - r * 0.12, cy + r * 0.38), (cx + r * 0.52, cy - r * 0.34)], fill=(255, 255, 255, 255), width=int(r * 0.26), joint='curve')
    return im


def tray_icon(color):
    im = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    bell(ImageDraw.Draw(im), S * 0.5, S * 0.52, S * 0.80, color)
    return im


app = app_icon()
app.resize((256, 256), Image.LANCZOS).save('docs/icon.png')
app.resize((256, 256), Image.LANCZOS).save('src/PingWhen/Assets/app.ico', sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (256, 256)])
tray_sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (48, 48)]
tray_icon((0x1C, 0x1C, 0x1C, 255)).resize((48, 48), Image.LANCZOS).save('src/PingWhen/Assets/tray-light.ico', sizes=tray_sizes)
tray_icon((255, 255, 255, 255)).resize((48, 48), Image.LANCZOS).save('src/PingWhen/Assets/tray-dark.ico', sizes=tray_sizes)
print('ok')
