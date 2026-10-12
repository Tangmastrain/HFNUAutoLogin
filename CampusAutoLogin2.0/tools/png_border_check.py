"""极简 PNG 解码 + 输入框边框连续性检查（无第三方依赖，仅用 zlib 标准库）。

检测思路：描边色 #E3E6EC 很浅，抗锯齿后与纯白差异极小，
因此不以「精确匹配颜色」为准，而以「非白墨迹」（与白色的最大通道差 >= 阈值）为准，
再分别统计四条边的覆盖率与四角闭合情况。
"""
import struct
import sys
import zlib


def load_png(path):
    data = open(path, 'rb').read()
    assert data[:8] == b'\x89PNG\r\n\x1a\n', 'not a png'
    pos = 8
    width = height = bitdepth = colortype = None
    idat = b''
    while pos < len(data):
        (length,) = struct.unpack('>I', data[pos:pos + 4])
        ctype = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + length]
        if ctype == b'IHDR':
            width, height, bitdepth, colortype = struct.unpack('>IIBB', chunk[:10])
        elif ctype == b'IDAT':
            idat += chunk
        elif ctype == b'IEND':
            break
        pos += 12 + length

    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[colortype]
    assert bitdepth == 8, f'unsupported bitdepth {bitdepth}'
    raw = zlib.decompress(idat)
    stride = width * channels

    pixels = bytearray(width * height * channels)
    prev = bytearray(stride)
    offset = 0
    for y in range(height):
        ftype = raw[offset]
        offset += 1
        line = bytearray(raw[offset:offset + stride])
        offset += stride
        if ftype == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 0xFF
        elif ftype == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ftype == 3:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif ftype == 4:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                b = prev[i]
                c = prev[i - channels] if i >= channels else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 0xFF
        pixels[y * stride:(y + 1) * stride] = line
        prev = line
    return width, height, channels, pixels


class Img:
    def __init__(self, path):
        self.w, self.h, self.c, self.px = load_png(path)
        self.stride = self.w * self.c

    def ink(self, x, y, thr=10):
        """非白墨迹强度：与白色的最大通道差（0..255）。"""
        i = y * self.stride + x * self.c
        r, g, b = self.px[i], self.px[i + 1], self.px[i + 2]
        return max(255 - r, 255 - g, 255 - b) >= thr


def find_bbox(img, x0, y0, x1, y1, thr=10, side_band=4):
    """在区域内定位边框包围盒：仅在四条边的窄带内搜索墨迹，避开内部文字/图标。"""
    xs, ys = [], []
    for y in range(y0, min(y1, img.h)):
        for x in range(x0, min(x1, img.w)):
            if img.ink(x, y, thr):
                xs.append(x)
                ys.append(y)
    if not xs:
        return None
    return min(xs), min(ys), max(xs), max(ys)


def edge_coverage(img, bx0, by0, bx1, by1, thr=10, radius=10):
    """按圆角几何的期望跨度统计四条边覆盖率：
    圆角矩形半径 r 时，竖边直线段只覆盖 y∈[by0+r-2, by1-r+2]，
    横边直线段只覆盖 x∈[bx0+r-2, bx1-r+2]，超出该跨度属于圆弧区域（由角检查负责）。"""
    v0 = min(by1, by0 + radius - 2)
    v1 = max(v0, by1 - radius + 2)
    h0 = min(bx1, bx0 + radius - 2)
    h1 = max(h0, bx1 - radius + 2)

    def rows_with_ink(x_lo, x_hi):
        hits = 0
        total = 0
        strengths = []
        for y in range(v0, v1 + 1):
            total += 1
            s = max(img.ink(x, y, thr) and 0 or ink_strength(img, x, y) for x in range(x_lo, x_hi + 1))
            if s >= thr:
                hits += 1
            strengths.append(s)
        return hits, total, strengths

    def cols_with_ink(y_lo, y_hi):
        hits = 0
        total = 0
        strengths = []
        for x in range(h0, h1 + 1):
            total += 1
            s = max(ink_strength(img, cx, y) for cx in range(x - 0, x + 1) for y in range(y_lo, y_hi + 1))
            s = max(ink_strength(img, x, y) for y in range(y_lo, y_hi + 1))
            if s >= thr:
                hits += 1
            strengths.append(s)
        return hits, total, strengths

    left = rows_with_ink(bx0, min(bx0 + 2, img.w - 1))
    right = rows_with_ink(max(0, bx1 - 2), bx1)
    top = cols_with_ink(by0, min(by0 + 2, img.h - 1))
    bottom = cols_with_ink(max(0, by1 - 2), by1)
    return {'left': left, 'right': right, 'top': top, 'bottom': bottom}


def ink_strength(img, x, y):
    i = y * img.stride + x * img.c
    r, g, b = img.px[i], img.px[i + 1], img.px[i + 2]
    return max(255 - r, 255 - g, 255 - b)


def median(values):
    s = sorted(values)
    n = len(s)
    if n == 0:
        return 0.0
    return s[n // 2] if n % 2 else (s[n // 2 - 1] + s[n // 2]) / 2


def corner_arc_ok(img, cx, cy, r, start_deg, thr=8):
    """
    沿圆角四分之一圆弧采样检查是否有墨迹（四角闭合）。
    cx,cy = 圆心；start_deg = 该角圆弧的起始角度（度，屏幕坐标 y 向下）：
      top-left 180 / top-right 270 / bottom-right 0 / bottom-left 90
    """
    import math
    hits = 0
    steps = 16
    for k in range(steps + 1):
        a = math.radians(start_deg + 90 * k / steps)
        px = cx + r * math.cos(a)
        py = cy + r * math.sin(a)
        found = any(img.ink(x, y, thr)
                    for x in range(int(px) - 2, int(px) + 3)
                    for y in range(int(py) - 2, int(py) + 3))
        hits += 1 if found else 0
    return hits, steps + 1


def main():
    path = sys.argv[1]
    x0, y0, x1, y1 = (int(v) for v in sys.argv[2:6])
    thr = int(sys.argv[6]) if len(sys.argv) > 6 else 8
    radius = int(sys.argv[7]) if len(sys.argv) > 7 else 10
    img = Img(path)
    box = find_bbox(img, x0, y0, x1, y1, thr)
    if box is None:
        print('区域内未发现描边墨迹')
        return
    bx0, by0, bx1, by1 = box
    print(f'描边包围盒 ({bx0},{by0})-({bx1},{by1})  w={bx1-bx0+1} h={by1-by0+1}  '
          f'thr={thr} radius={radius}')

    cov = edge_coverage(img, bx0, by0, bx1, by1, thr, radius)
    ok = True
    medians = []
    for name in ('left', 'right', 'top', 'bottom'):
        got, total, strengths = cov[name]
        pct = got * 100 / total
        med = median(strengths)          # 中位数：抗内部文字/图标干扰
        mx = max(strengths)
        passed = pct >= 95
        ok &= passed
        medians.append(med)
        print(f'  {"OK " if passed else "!! "}{name:6s} 覆盖率 {got}/{total} = {pct:5.1f}%   '
              f'墨迹强度(中位) {med:4.1f}  峰值 {mx}')

    spread = max(medians) - min(medians)
    uniform = spread <= max(4.0, 0.25 * (sum(medians) / 4))
    print(f'  四边中位强度 {[round(m,1) for m in medians]}  极差 {spread:.1f}  '
          f'{"等粗 OK" if uniform else "粗细不均 !!"}')
    ok &= uniform

    corners = {
        'top-left':     (bx0 + radius, by0 + radius, 180),
        'top-right':    (bx1 - radius, by0 + radius, 270),
        'bottom-right': (bx1 - radius, by1 - radius, 0),
        'bottom-left':  (bx0 + radius, by1 - radius, 90),
    }
    for name, (ccx, ccy, start) in corners.items():
        hits, total = corner_arc_ok(img, ccx, ccy, radius, start, thr)
        closed = hits >= total - 1
        ok &= closed
        print(f'  角 {name:13s} 圆弧墨迹 {hits}/{total}  {"闭合 OK" if closed else "断裂 !!"}')

    print('结论:', 'PASS 四边等粗 + 四角闭合 + 边框完整' if ok else 'CHECK 仍需修')


if __name__ == '__main__':
    main()
