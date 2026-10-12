#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
校徽 → Windows 多尺寸 .ico（仅资源处理，不参与编译）

源图实测特征（决定了下面的处理方式，不能想当然）：
  · 背景是浅灰 #E6E6E6，不是白色
  · 徽标圆环被横向拉伸：横 426px × 纵 406px（非正圆）
  · 圆内也是浅灰背景（没有白色内盘）

处理流程：
 1. 用深蓝阈值定位圆环四个极点（左右 / 上下）
 2. 按极点裁切；长宽比偏差 > 2% 时校正为正圆（官方徽标是正圆）
 3. 圆内近灰背景洗成纯白（保留蓝色线条与文字），圆外一律透明
 4. 逐尺寸 LANCZOS 单独缩放，手写 ICO 容器（PNG 压缩帧）
 5. 边缘探针自动验证：圆外透明、圆内边缘必须是圆环而非灰底
 6. 输出浅底/深底预览图，便于肉眼确认小尺寸可辨识度

用法：
  python make_icon.py <源图.png> <输出.ico> <预览.png>
"""
import io
import struct
import sys
from PIL import Image, ImageDraw, ImageFilter

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
RING_BLUE = dict(min_b=100, d_r=60, d_g=25)   # 圆环深蓝 #0259A0 判定
BG_GRAY = 230                                 # 源图背景灰
GRAY_TOL = 14                                 # 洗白容差


def is_ring_blue(r: int, g: int, b: int) -> bool:
    """圆环深蓝判定。阈值收紧，避免把浅蓝灰光晕当成徽标。"""
    return b > RING_BLUE["min_b"] and b - r > RING_BLUE["d_r"] and b - g > RING_BLUE["d_g"]


def is_bg_gray(r: int, g: int, b: int) -> bool:
    """近中性灰背景（低饱和 + 灰度接近 230）→ 需要洗白。"""
    return max(r, g, b) - min(r, g, b) <= GRAY_TOL and abs(r - BG_GRAY) <= 20


def detect_ring(im: Image.Image):
    """定位圆环四个极点，返回 (xl, yt, xr, yb)。"""
    rgb = im.convert("RGB")
    w, h = rgb.size
    px = rgb.load()

    # 先粗扫求出圆环纵向中点，用于选一条穿透圆环的水平线
    ys = [y for y in range(h) if any(is_ring_blue(*px[x, y]) for x in range(0, w, 3))]
    if not ys:
        raise SystemExit("未检测到圆环蓝色像素，请检查源图")
    y_mid = (ys[0] + ys[-1]) // 2
    xs = [x for x in range(w) if any(is_ring_blue(*px[x, y]) for y in range(0, h, 3))]
    x_mid = (xs[0] + xs[-1]) // 2

    def first_last(seq):
        seq = list(seq)
        return (seq[0], seq[-1]) if seq else (None, None)

    xl, xr = first_last(x for x in range(w) if is_ring_blue(*px[x, y_mid]))
    yt, yb = first_last(y for y in range(h) if is_ring_blue(*px[x_mid, y]))
    if None in (xl, xr, yt, yb):
        raise SystemExit("圆环极点探测失败")
    print(f"  圆环极点 xl={xl} xr={xr}（横 {xr - xl}）  yt={yt} yb={yb}（纵 {yb - yt}）")
    return xl, yt, xr, yb


def build_master(im: Image.Image) -> Image.Image:
    """极点裁切 → 长宽比校正为正圆 → 圆内灰底洗白 → 圆外透明。"""
    xl, yt, xr, yb = detect_ring(im)
    dx, dy = xr - xl + 1, yb - yt + 1
    crop = im.convert("RGBA").crop((xl, yt, xr + 1, yb + 1))

    # 长宽比校正：官方徽标是正圆，源图横向被拉伸 → 压回正圆
    if abs(dx - dy) / max(dx, dy) > 0.02:
        crop = crop.resize((dy, dy), Image.LANCZOS)
        print(f"  长宽比校正 {dx}x{dy} → {dy}x{dy}（横向压缩 {dx / dy:.3f}）")
    side = crop.size[0]

    # 圆内灰底洗白（保留蓝色线条/文字；圆外灰底稍后被透明蒙版丢弃）
    px = crop.load()
    whiten = 0
    for y in range(side):
        for x in range(side):
            r, g, b, a = px[x, y]
            if is_bg_gray(r, g, b):
                px[x, y] = (255, 255, 255, a)
                whiten += 1
    print(f"  背景洗白 {whiten} 像素（{whiten * 100 / (side * side):.1f}%）  正圆尺寸 {side}x{side}")

    # 圆形蒙版：圆外透明
    big = side * 4
    mask = Image.new("L", (big, big), 0)
    inset = max(1, round(big * 0.004))
    ImageDraw.Draw(mask).ellipse((inset, inset, big - 1 - inset, big - 1 - inset), fill=255)
    mask = mask.resize((side, side), Image.LANCZOS).filter(ImageFilter.GaussianBlur(max(1, side // 256) / 2))
    alpha = crop.getchannel("A")
    crop.putalpha(Image.composite(alpha, Image.new("L", (side, side), 0), mask))
    return crop


def make_frames(master: Image.Image):
    frames = []
    for s in SIZES:
        frames.append((s, master.resize((s, s), Image.LANCZOS)))
    return frames


def write_ico(frames, path: str):
    blobs = []
    for s, img in frames:
        buf = io.BytesIO()
        img.save(buf, format="PNG", optimize=True)
        blobs.append((s, buf.getvalue()))
    header = struct.pack("<HHH", 0, 1, len(blobs))
    offset = 6 + 16 * len(blobs)
    entries = data = b""
    for s, blob in blobs:
        b0 = 0 if s >= 256 else s
        entries += struct.pack("<BBBBHHII", b0, b0, 0, 0, 1, 32, len(blob), offset)
        data += blob
        offset += len(blob)
    with open(path, "wb") as f:
        f.write(header + entries + data)
    return len(header + entries + data)


def is_ring_blue_px(p) -> bool:
    r, g, b = p[0], p[1], p[2]
    return b > 100 and b - r > 50 and b - g > 18


def verify_master(im: Image.Image) -> bool:
    """
    基准图自检（唯一有意义的检测点）。

    为什么不在 16/32px 帧上做灰环判定：源徽标的外环只有约 8px 厚（424px 直径的 1.9%），
    缩到 32px 后环宽不足 1px，整圈会被抗锯齿冲淡成浅灰过渡色 —— 那是缩放固有损失，
    不是"背景没洗干净"。在 32px 上按像素判灰环只会得到假阳性。

    这里在 409px 基准图上检查三件真实缺陷：
      ① 圆外必须透明          → 否则深色桌面上会出现灰方块
      ② 不允许残留 #E6E6E6 中性灰（不透明）→ 用户看到的"灰白圆环"就是这个
      ③ 最外 1% 环带必须是蓝色圆环 → 证明圆形蒙版没有切掉/洗掉徽标外环
    """
    s = im.size[0]
    px = im.load()
    c = (s - 1) / 2.0
    r_out = s / 2.0

    outside_opaque = 0      # 圆外不透明像素
    residual_gray = 0       # 残留中性灰（#E6E6E6 一族）
    band_blue = band_total = 0
    band_sum = [0, 0, 0]

    for y in range(s):
        for x in range(s):
            r, g, b, a = px[x, y]
            d = ((x - c) ** 2 + (y - c) ** 2) ** 0.5
            if d > r_out:
                if a > 8:
                    outside_opaque += 1
                continue
            if a > 200 and max(r, g, b) - min(r, g, b) < 12 and 205 <= min(r, g, b) <= 248:
                residual_gray += 1
            if d >= 0.99 * r_out:
                band_total += 1
                if is_ring_blue_px((r, g, b)):
                    band_blue += 1
                for i in range(3):
                    band_sum[i] += (r, g, b)[i]

    avg = tuple(v // max(1, band_total) for v in band_sum)
    blue_rate = band_blue / band_total if band_total else 0.0

    # 残留灰阈值：洗白后仍会有少量中性灰，来自徽标自身细线（内圈细线、楼体线条、
    # 环形文字）的抗锯齿过渡色。灰底若没洗净，量级是整片背景（数万像素），
    # 因此以"圆面积 1.5%"为上限即可把"洗过"和"没洗"区分开。
    circle_area = 3.14159 * r_out * r_out
    gray_limit = circle_area * 0.015
    ok = outside_opaque == 0 and residual_gray <= gray_limit and blue_rate >= 0.60
    print(
        f"  基准自检 {s}px {'OK  ' if ok else 'FAIL'}"
        f"  圆外不透明 {outside_opaque}"
        f"  残留灰 {residual_gray}（上限 {gray_limit:.0f}）"
        f"  最外环带蓝占比 {blue_rate * 100:.1f}%（均值 RGB{avg}）"
    )
    return ok


def verify_frame(frame: Image.Image, size: int) -> bool:
    """
    小尺寸帧检查：四角透明 + 不透明覆盖率落在圆盘应有范围内。

    覆盖率替代了原先的"极点 alpha"检查：极点像素的 alpha 随缩放比例变化
    （409→32 时整圈被模糊，极点 alpha≈227；409→256 时边缘更陡，极点 alpha 可低到 24），
    这属于抗锯齿的正常差异，不是缺陷。覆盖率则能稳定地抓出
    "蒙版过小把徽标切掉"和"蒙版缺失留下方块"这两类真实错误。
    """
    s = frame.size[0]
    px = frame.load()
    corner_max = max(px[x][3] for x in ((0, 0), (s - 1, 0), (0, s - 1), (s - 1, s - 1)))
    opaque = sum(1 for y in range(s) for x in range(s) if px[x, y][3] >= 200)
    cover = opaque / (s * s)
    ok = corner_max <= 8 and 0.70 <= cover <= 0.83
    print(f"  帧检查 {size:>3}px {'OK  ' if ok else 'FAIL'}  角A≤{corner_max}  不透明覆盖 {cover * 100:.1f}%")
    return ok


def make_preview(frames, path: str):
    pad, label_h = 16, 20
    show = [f for f in frames if f[0] <= 128]
    w = pad + sum(s + pad for s, _ in show)
    h = pad * 2 + label_h + max(s for s, _ in show)
    sheet = Image.new("RGB", (w, h * 2 + 8), (245, 246, 250))
    for idx, base in enumerate(((245, 246, 250), (32, 34, 38))):
        bg = Image.new("RGB", (w, h), base)
        x = pad
        for s, img in show:
            bg.paste(img, (x, pad + (max(q for q, _ in show) - s) // 2), img)
            ImageDraw.Draw(bg).text((x, h - label_h + 2), f"{s}px",
                                    fill=(255, 255, 255) if idx == 1 else (90, 95, 105))
            x += s + pad
        sheet.paste(bg, (0, idx * (h + 8)))
    sheet.save(path)


def main():
    if len(sys.argv) < 4:
        raise SystemExit("用法: python make_icon.py <源图.png> <输出.ico> <预览.png>")
    src, ico_out, preview_out = sys.argv[1], sys.argv[2], sys.argv[3]
    im = Image.open(src)
    print(f"[1/5] 源图 {src}  {im.size[0]}x{im.size[1]} {im.mode}")
    master = build_master(im)
    print("[2/5] 生成各尺寸帧")
    frames = make_frames(master)
    print("[3/5] 自检")
    master_ok = verify_master(master)
    frame_ok = all(verify_frame(img, s) for s, img in frames if s >= 32)
    all_ok = master_ok and frame_ok
    print("[4/5] 写入 ICO")
    size = write_ico(frames, ico_out)
    print(f"  {ico_out}  {size} 字节  尺寸 {[s for s, _ in frames]}")
    print("[5/5] 预览图")
    make_preview(frames, preview_out)
    master.resize((256, 256), Image.LANCZOS).save(preview_out.replace("icon-preview", "icon-256"))
    print(f"  {preview_out}")
    if not all_ok:
        raise SystemExit("边缘自检未通过")


if __name__ == "__main__":
    main()
