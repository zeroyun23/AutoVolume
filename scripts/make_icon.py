"""生成 AutoVolume 应用图标：蓝底圆角方块 + 白色喇叭 + 两条暂停竖条。

每档尺寸单独超采样渲染再缩，避免 16px 糊成一团。
输出：src/AutoVolume.App/app.ico（多尺寸）、artifacts/icon/preview.png（预览）
"""
import io
import os
import struct

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_ICO = os.path.join(ROOT, "src", "AutoVolume.App", "app.ico")
OUT_PREVIEW = os.path.join(ROOT, "artifacts", "icon", "preview.png")

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
SS = 8  # 超采样倍数

BG_TOP = (30, 125, 212)
BG_BOTTOM = (10, 88, 156)
WHITE = (255, 255, 255)


def lerp(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m


def draw(size):
    s = size * SS

    bg = Image.new("RGB", (s, s))
    d = ImageDraw.Draw(bg)
    for y in range(s):
        d.line([(0, y), (s, y)], fill=lerp(BG_TOP, BG_BOTTOM, y / (s - 1)))

    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    im.paste(bg, (0, 0), rounded_mask(s, round(s * 0.22)))

    d = ImageDraw.Draw(im)

    # 16/20/24：只画喇叭。像素不够，硬塞暂停条会糊成一片
    if size <= 24:
        big = [
            (0.14, 0.375), (0.34, 0.375), (0.74, 0.235),
            (0.74, 0.765), (0.34, 0.625), (0.14, 0.625),
        ]
        d.polygon([(x * s, y * s) for x, y in big], fill=WHITE)
        return im.resize((size, size), Image.LANCZOS)

    # 暂停条：按像素下限加粗，小尺寸也要留出可见间隙
    bar_w = max(0.105, 2.0 / size)
    gap = max(0.06, 1.2 / size)
    right = 0.869
    x0 = right - (2 * bar_w + gap)
    tip = x0 - max(0.02, 1.2 / size)
    base = tip - 0.20           # 锥体收口

    # 不留正面厚度：16px 下那条 1px 白边会被误认成第三根暂停条
    speaker = [
        (0.10, 0.405), (base, 0.405), (tip, 0.255),
        (tip, 0.745), (base, 0.595), (0.10, 0.595),
    ]
    d.polygon([(x * s, y * s) for x, y in speaker], fill=WHITE)

    for i in range(2):
        left = (x0 + i * (bar_w + gap)) * s
        d.rounded_rectangle(
            [left, 0.315 * s, left + bar_w * s, 0.685 * s],
            radius=bar_w * s * 0.32,
            fill=WHITE,
        )

    return im.resize((size, size), Image.LANCZOS)


def save_ico(path, frames):
    """手写 ICO 容器：每档都放自己的 PNG，而不是让系统从 256 缩放。"""
    frames = sorted(frames, key=lambda f: f[0])
    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    entries, blobs = b"", b""
    for size, im in frames:
        buf = io.BytesIO()
        im.save(buf, format="PNG")
        png = buf.getvalue()
        wh = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", wh, wh, 0, 0, 1, 32, len(png), offset + len(blobs))
        blobs += png
    with open(path, "wb") as f:
        f.write(header + entries + blobs)


def preview(frames):
    """深/浅两种背景展示 256，另有 48/32/16 与小尺寸 8 倍放大。"""
    w, h = 1100, 420
    im = Image.new("RGB", (w, h))
    d = ImageDraw.Draw(im)
    d.rectangle([0, 0, 660, h], fill=(32, 32, 32))
    d.rectangle([660, 0, w, h], fill=(245, 246, 248))

    font = ImageFont.load_default()
    im.paste(frames[256], (60, 80), frames[256])
    im.paste(frames[256], (700, 80), frames[256])

    x = 400
    for size in (48, 32, 16):
        im.paste(frames[size], (x, 105), frames[size])
        x += size + 30
    zoom = frames[16].resize((128, 128), Image.NEAREST)
    im.paste(zoom, (400, 190), zoom)

    d.text((60, 370), "256", fill=(230, 230, 230), font=font)
    d.text((700, 370), "256", fill=(60, 60, 60), font=font)
    d.text((400, 340), "48 / 32 / 16", fill=(230, 230, 230), font=font)
    d.text((400, 330 - 10), "16px x8", fill=(160, 160, 160), font=font)

    im.save(OUT_PREVIEW)


def main():
    os.makedirs(os.path.dirname(OUT_PREVIEW), exist_ok=True)
    frames = {size: draw(size) for size in SIZES}
    save_ico(OUT_ICO, frames.items())
    preview(frames)
    print("ico:", OUT_ICO, os.path.getsize(OUT_ICO), "bytes")
    print("sizes:", ",".join(str(s) for s in SIZES))
    print("preview:", OUT_PREVIEW)


if __name__ == "__main__":
    main()
