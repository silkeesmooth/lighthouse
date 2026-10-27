"""Draws src/Lighthouse/lighthouse.ico: the tray icon's two bars on a Payne's gray tile.

Stdlib only (PNG-in-ICO). Run from the repo root: python tools/make-icon.py
"""
import struct
import zlib
from pathlib import Path

BG = (0x2E, 0x61, 0x71)      # Payne's gray
CPU = (0xED, 0xF3, 0xF5)     # aquahaze
GPU = (0xF1, 0xE3, 0x8F)     # khaki
SIZES = [16, 24, 32, 48, 64, 256]
SS = 4  # supersampling per axis


def inside_rounded(x, y, size, radius):
    cx = min(max(x, radius), size - radius)
    cy = min(max(y, radius), size - radius)
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2


def render(size):
    radius = size * 0.22
    pad = size * 0.2
    gap = size * 0.1
    bar_w = (size - 2 * pad - gap) / 2
    top, bottom = pad, size - pad
    bars = [(pad, 0.6, CPU), (pad + bar_w + gap, 0.85, GPU)]
    rows = []
    for py in range(size):
        row = bytearray([0])  # PNG filter: none
        for px in range(size):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sy in range(SS):
                for sx in range(SS):
                    x = px + (sx + 0.5) / SS
                    y = py + (sy + 0.5) / SS
                    if not inside_rounded(x, y, size, radius):
                        continue
                    color, alpha = BG, 1.0
                    for left, fill, bar_color in bars:
                        if left <= x < left + bar_w and top <= y < bottom:
                            filled_top = bottom - fill * (bottom - top)
                            if y >= filled_top:
                                color = bar_color
                            else:
                                color = tuple(int(b * 0.72 + c * 0.28) for b, c in zip(BG, bar_color))
                    acc[0] += color[0] * alpha
                    acc[1] += color[1] * alpha
                    acc[2] += color[2] * alpha
                    acc[3] += alpha
            n = SS * SS
            a = acc[3] / n
            if a > 0:
                row += bytes([int(acc[0] / acc[3]), int(acc[1] / acc[3]), int(acc[2] / acc[3]), int(round(a * 255))])
            else:
                row += bytes([0, 0, 0, 0])
        rows.append(bytes(row))
    raw = b"".join(rows)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def main():
    images = [render(s) for s in SIZES]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, blobs = b"", b""
    for size, png in zip(SIZES, images):
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset)
        offset += len(png)
        blobs += png
    out = Path(__file__).resolve().parent.parent / "src" / "Lighthouse" / "lighthouse.ico"
    out.write_bytes(header + entries + blobs)
    print(f"wrote {out} ({out.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
