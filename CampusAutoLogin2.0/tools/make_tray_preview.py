#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
托盘图标预览（仅用于肉眼核对，不参与编译）

完全复刻 Platform/AppIcon.cs 里 Tint() 的绘制逻辑：
  · 从 app.ico 取「系统小图标尺寸」那一帧做底图（本机 125% → 20px）
  · 右下角叠加状态圆点，直径 d = max(6, n*6/16)，边距 m = max(1, n/16)
  · 圆点外扩 1px 白色描边，保证浅色/深色任务栏上都可辨

GDI+ 的 Rectangle(x, y, w, h) 覆盖像素 x..x+w-1，换算成 PIL 的
ellipse bbox（闭区间 x0..x1）需要减 1，否则圆点会大一圈。

用法: python make_tray_preview.py <app.ico> <输出.png> [尺寸...]
"""
import io
import struct
import sys
from PIL import Image, ImageDraw

# 与 AppIcon.cs 中的 ColorOk / ColorFail / ColorIdle 一一对应
STATES = [
    ("已连接 (绿)", (46, 160, 67)),
    ("登录失败 (红)", (214, 69, 69)),
    ("未连接 (橙)", (232, 132, 42)),
]
DEFAULT_SIZES = [16, 20, 24]
ZOOM = 10          # 放大倍数（NEAREST，保留像素感）
PAD = 18
LABEL_H = 34


def read_frames(path):
    """手写解析 ICO 容器，返回 {边长: RGBA Image}。"""
    data = open(path, "rb").read()
    count = struct.unpack("<H", data[4:6])[0]
    off = 6
    frames = {}
    for _ in range(count):
        w, h, _c, _r, _p, _bpp, ln, o = struct.unpack("<BBBBHHII", data[off:off + 16])
        off += 16
        frames[w if w else 256] = Image.open(io.BytesIO(data[o:o + ln])).convert("RGBA")
    return frames


def tint(base, n, dot):
    """复刻 AppIcon.Tint()。"""
    scaled = base.resize((n, n), Image.LANCZOS)
    bmp = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    bmp.paste(scaled, (0, 0), scaled)

    d = max(6, n * 6 // 16)
    m = max(1, n // 16)
    # GDI+ Rectangle(n-d-m, ..., d, d) → 像素 n-d-m .. n-m-1
    r = (n - d - m, n - d - m, n - m - 1, n - m - 1)
    # Rectangle.Inflate(rect, 1, 1) → 像素 n-d-m-1 .. n-m
    halo = (r[0] - 1, r[1] - 1, r[2] + 1, r[3] + 1)

    dr = ImageDraw.Draw(bmp)
    dr.ellipse(halo, fill=(255, 255, 255, 255))
    dr.ellipse(r, fill=dot + (255,))
    return bmp


def build_preview(frames, sizes, out_path):
    cell_w = max(sizes) * ZOOM
    row_h = cell_w + LABEL_H
    w = PAD + len(sizes) * (cell_w + PAD)
    h = PAD + len(STATES) * (row_h + PAD)
    sheet = Image.new("RGB", (w, h * 2 + 12), (255, 255, 255))

    for idx, bg_color in enumerate(((245, 246, 250), (32, 34, 38))):
        bg = Image.new("RGB", (w, h), bg_color)
        draw = ImageDraw.Draw(bg)
        fg = (60, 64, 72) if idx == 0 else (225, 228, 234)
        for row, (label, dot) in enumerate(STATES):
            y0 = PAD + row * (row_h + PAD)
            for col, n in enumerate(sizes):
                x0 = PAD + col * (cell_w + PAD)
                icon = tint(frames[n], n, dot)
                big = icon.resize((n * ZOOM, n * ZOOM), Image.NEAREST)
                ox = x0 + (cell_w - n * ZOOM) // 2
                bg.paste(big, (ox, y0), big)
                draw.text((x0, y0 + cell_w + 6), f"{label}  {n}px", fill=fg)
        sheet.paste(bg, (0, idx * (h + 12)))
    sheet.save(out_path)
    return sheet.size


def main():
    if len(sys.argv) < 3:
        raise SystemExit("用法: python make_tray_preview.py <app.ico> <输出.png> [尺寸...]")
    ico, out = sys.argv[1], sys.argv[2]
    sizes = [int(a) for a in sys.argv[3:]] or DEFAULT_SIZES
    frames = read_frames(ico)
    print(f"ICO 帧: {sorted(frames)}")
    missing = [s for s in sizes if s not in frames]
    if missing:
        raise SystemExit(f"ICO 缺少帧 {missing}")
    size = build_preview(frames, sizes, out)
    print(f"已输出 {out}  {size[0]}x{size[1]}  尺寸 {sizes}  放大 {ZOOM}x")


if __name__ == "__main__":
    main()
