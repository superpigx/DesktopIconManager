# -*- coding: utf-8 -*-
"""生成 app.ico：绿圆角方块 + 白色网格点（与托盘图标同一设计语言）"""
import struct, zlib, os


def round_rect_mask(w, h, radius):
    """圆角矩形 alpha 蒙版"""
    m = [[0] * w for _ in range(h)]
    for y in range(h):
        for x in range(w):
            dx = min(x, w - 1 - x)
            dy = min(y, h - 1 - y)
            if dx >= radius or dy >= radius:
                m[y][x] = 255
            else:
                ddx, ddy = radius - dx, radius - dy
                m[y][x] = 255 if ddx * ddx + ddy * ddy <= radius * radius else 0
    return m


def render(size):
    """渲染 RGBA 像素：绿色圆角底 + 2x2 白色圆点网格"""
    green = (0x22, 0xCB, 0x64)
    white = (0xFF, 0xFF, 0xFF)
    alpha = round_rect_mask(size, size, max(2, size * 28 // 256))
    px = [[(0, 0, 0, 0)] * size for _ in range(size)]

    # 底色
    for y in range(size):
        for x in range(size):
            a = alpha[y][x]
            if a:
                px[y][x] = (green[0], green[1], green[2], a)

    # 白色 2x2 圆点（四象限网格），点位随尺寸缩放
    def dot(cx, cy, r):
        for y in range(max(0, cy - r), min(size, cy + r + 1)):
            for x in range(max(0, cx - r), min(size, cx + r + 1)):
                if (x - cx) ** 2 + (y - cy) ** 2 <= r * r:
                    px[y][x] = (white[0], white[1], white[2], 255)

    u = size / 256.0
    r = max(1, int(22 * u))
    for qx in (86, 170):
        for qy in (86, 170):
            dot(int(qx * u), int(qy * u), r)

    return px


def encode_png(px, w, h):
    raw = b''.join(b'\x00' + b''.join(struct.pack('4B', *px[y][x]) for x in range(w))
                   for y in range(h))

    def chunk(typ, data):
        c = struct.pack('>I', len(data)) + typ + data
        return c + struct.pack('>I', zlib.crc32(typ + data) & 0xFFFFFFFF)

    ihdr = struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)
    return (b'\x89PNG\r\n\x1a\n'
            + chunk(b'IHDR', ihdr)
            + chunk(b'IDAT', zlib.compress(raw, 9))
            + chunk(b'IEND', b''))


def build_ico(path):
    sizes = [16, 32, 48, 256]
    pngs = []
    for s in sizes:
        px = render(s)
        pngs.append((s, encode_png(px, s, s)))

    out = struct.pack('<HHH', 0, 1, len(pngs))
    offset = 6 + 16 * len(pngs)
    for s, data in pngs:
        b = 0 if s >= 256 else s
        out += struct.pack('<BBBBHHII', b, b, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in pngs:
        out += data

    open(path, 'wb').write(out)
    return [s for s, _ in pngs]


if __name__ == '__main__':
    p = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'app.ico')
    sizes = build_ico(p)
    print('生成 %s（%d 字节，尺寸 %s）' % (p, os.path.getsize(p), sizes))
