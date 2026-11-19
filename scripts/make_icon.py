"""Generate the Universal Search Launcher icon without external dependencies.

The mark is a rounded gradient tile with a white magnifier. Rendering is done
with signed-distance shapes and supersampling so Windows tray and installer
icons stay crisp.
"""
from __future__ import annotations

import math
import struct
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "assets"


def clamp(value: float, low: float = 0.0, high: float = 1.0) -> float:
    return max(low, min(high, value))


def mix(a, b, t: float):
    t = clamp(t)
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(4))


def rounded_box_distance(x: float, y: float, half: float, radius: float) -> float:
    qx = abs(x) - half + radius
    qy = abs(y) - half + radius
    outside = math.hypot(max(qx, 0.0), max(qy, 0.0))
    inside = min(max(qx, qy), 0.0)
    return outside + inside - radius


def segment_distance(px: float, py: float, ax: float, ay: float, bx: float, by: float) -> float:
    vx, vy = bx - ax, by - ay
    wx, wy = px - ax, py - ay
    length = vx * vx + vy * vy
    t = 0.0 if length == 0 else clamp((wx * vx + wy * vy) / length)
    return math.hypot(px - (ax + t * vx), py - (ay + t * vy))


def render(size: int) -> bytes:
    """Render one square RGBA image with 4x4 supersampling."""
    scale = 4
    n = size * scale
    rgba = bytearray(size * size * 4)
    inv = 1.0 / n

    for py in range(size):
        for px in range(size):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sy in range(scale):
                for sx in range(scale):
                    x = (px * scale + sx + 0.5) * inv
                    y = (py * scale + sy + 0.5) * inv
                    cx, cy = x - 0.5, y - 0.5

                    color = (0.0, 0.0, 0.0, 0.0)
                    tile_distance = rounded_box_distance(cx, cy, 0.445, 0.135)
                    if tile_distance <= 0.0:
                        diagonal = clamp((x + y) * 0.5)
                        base = mix((0.38, 0.31, 0.96, 1.0), (0.02, 0.67, 0.91, 1.0), diagonal)
                        highlight = clamp((1.0 - (x * 0.7 + y * 0.9)) * 0.30)
                        base = mix(base, (0.94, 0.95, 1.0, 1.0), highlight * 0.18)
                        bottom_glow = clamp((x * 0.4 + y - 0.35) * 0.4)
                        base = mix(base, (0.09, 0.16, 0.42, 1.0), bottom_glow * 0.35)
                        color = base

                        # Soft shadow behind the glass.
                        lens_x, lens_y = -0.035, -0.045
                        lens_r = 0.195
                        shadow = max(
                            lens_r + 0.036 - math.hypot(cx - lens_x, cy - lens_y),
                            0.095 - segment_distance(cx, cy, 0.10, 0.08, 0.275, 0.255),
                        )
                        if shadow > 0.0:
                            color = mix(color, (0.02, 0.05, 0.16, 1.0), clamp(shadow / 0.016) * 0.35)

                        # Magnifier lens ring.
                        lens_d = abs(math.hypot(cx - lens_x, cy - lens_y) - lens_r)
                        ring = 1.0 - clamp((lens_d - 0.052) / 0.012)
                        if ring > 0.0:
                            ring_color = mix((0.94, 0.97, 1.0, 1.0), (0.75, 0.91, 1.0, 1.0), y)
                            color = mix(color, ring_color, ring)

                        # Handle with rounded endpoints.
                        handle_distance = segment_distance(cx, cy, 0.105, 0.095, 0.285, 0.275)
                        handle = 1.0 - clamp((handle_distance - 0.049) / 0.014)
                        if handle > 0.0:
                            color = mix(color, (0.96, 0.98, 1.0, 1.0), handle)
                            inner = 1.0 - clamp((handle_distance - 0.026) / 0.012)
                            if inner > 0.0:
                                color = mix(color, (0.83, 0.91, 1.0, 1.0), inner * 0.4)

                        # Small glint inside the lens.
                        glint = 1.0 - clamp((math.hypot(cx + 0.10, cy + 0.11) - 0.043) / 0.014)
                        if glint > 0.0 and math.hypot(cx - lens_x, cy - lens_y) < lens_r - 0.02:
                            color = mix(color, (0.92, 0.98, 1.0, 1.0), glint * 0.58)

                    acc[0] += color[0]
                    acc[1] += color[1]
                    acc[2] += color[2]
                    acc[3] += color[3]

            samples = scale * scale
            offset = (py * size + px) * 4
            rgba[offset] = round(clamp(acc[0] / samples) * 255)
            rgba[offset + 1] = round(clamp(acc[1] / samples) * 255)
            rgba[offset + 2] = round(clamp(acc[2] / samples) * 255)
            rgba[offset + 3] = round(clamp(acc[3] / samples) * 255)

    return bytes(rgba)


def png(rgba: bytes, size: int) -> bytes:
    raw = bytearray()
    stride = size * 4
    for y in range(size):
        raw.append(0)
        raw.extend(rgba[y * stride : (y + 1) * stride])

    def chunk(kind: bytes, payload: bytes) -> bytes:
        return (
            struct.pack(">I", len(payload))
            + kind
            + payload
            + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)
        )

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def main() -> None:
    ASSETS.mkdir(exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = {}
    for size in sizes:
        images[size] = png(render(size), size)
        print(f"rendered {size}x{size}")

    (ASSETS / "app-256.png").write_bytes(images[256])
    (ASSETS / "app-64.png").write_bytes(images[64])

    header = struct.pack("<HHH", 0, 1, len(sizes))
    entries = bytearray()
    payload = bytearray()
    offset = 6 + 16 * len(sizes)
    for size in sizes:
        data = images[size]
        entries.extend(
            struct.pack(
                "<BBBBHHII",
                0 if size == 256 else size,
                0 if size == 256 else size,
                0,
                0,
                1,
                32,
                len(data),
                offset + len(payload),
            )
        )
        payload.extend(data)
    (ASSETS / "app.ico").write_bytes(header + entries + payload)
    print(f"wrote {ASSETS / 'app.ico'} ({len(header + entries + payload)} bytes)")


if __name__ == "__main__":
    main()
