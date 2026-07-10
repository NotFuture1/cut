#!/usr/bin/env python3
"""generate src/Assets/cut.ico - dark rounded square with a broken red diagonal (a cut).
pure stdlib, no pillow. ico embeds png images (valid on vista+)."""
import math
import struct
import zlib
from pathlib import Path

BG = (20, 20, 22)
ACCENT = (224, 67, 67)
RADIUS = 0.22
LINE_HALF = 0.055
# two segments of one diagonal with a gap in the middle: the cut
SEGS = [((0.20, 0.80), (0.44, 0.56)), ((0.56, 0.44), (0.80, 0.20))]


def seg_dist(px, py, a, b):
    ax, ay = a
    bx, by = b
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def sample(x, y):
    """color at unit coords, or None if transparent"""
    dx = max(abs(x - 0.5) - (0.5 - RADIUS), 0.0)
    dy = max(abs(y - 0.5) - (0.5 - RADIUS), 0.0)
    if math.hypot(dx, dy) > RADIUS:
        return None
    for a, b in SEGS:
        if seg_dist(x, y, a, b) <= LINE_HALF:
            return ACCENT
    return BG


def render(size, ss=4):
    rows = []
    for j in range(size):
        row = []
        for i in range(size):
            r = g = b = a = 0
            for sj in range(ss):
                for si in range(ss):
                    x = (i + (si + 0.5) / ss) / size
                    y = (j + (sj + 0.5) / ss) / size
                    c = sample(x, y)
                    if c is not None:
                        r += c[0]
                        g += c[1]
                        b += c[2]
                        a += 255
            n = ss * ss
            cov = a // n
            if cov:
                row.append((r * 255 // a, g * 255 // a, b * 255 // a, cov))
            else:
                row.append((0, 0, 0, 0))
        rows.append(row)
    return rows


def to_png(size, rows):
    def chunk(typ, data):
        return (struct.pack(">I", len(data)) + typ + data
                + struct.pack(">I", zlib.crc32(typ + data) & 0xFFFFFFFF))

    raw = b"".join(b"\x00" + bytes(v for px in row for v in px) for row in rows)
    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def main():
    sizes = [16, 24, 32, 48, 64, 256]
    pngs = [to_png(s, render(s)) for s in sizes]

    out = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    for s, png in zip(sizes, pngs):
        out += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(png), offset)
        offset += len(png)
    out += b"".join(pngs)

    path = Path(__file__).resolve().parent.parent / "src" / "Assets" / "cut.ico"
    path.write_bytes(out)
    print(f"wrote {path} ({len(out)} bytes)")


if __name__ == "__main__":
    main()
