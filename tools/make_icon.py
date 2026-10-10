"""Generate app.ico for rat-trap.

The mark is a capture frame - four corner brackets - with the classic arrow cursor inside it,
which is literally what the program does. Everything is drawn analytically and supersampled 4x,
so the script has no dependencies beyond the standard library and can be rerun to change a colour.

Usage:
    python tools/make_icon.py src/RatTrap/app.ico
"""

import os
import struct
import sys
import zlib

# Sizes Windows actually asks for: 16 in the tree view and menus, 20/24 at some DPI scales,
# 32 in the taskbar, 48 in Explorer's medium icons, 256 in the extra-large view.
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

SUPERSAMPLE = 4

# Colours, as (r, g, b, a). The bracket colour is the accent; the arrow is the familiar white
# pointer with a near-black outline so it stays legible on both light and dark backgrounds.
BRACKET = (56, 189, 248, 255)     # sky blue
BODY = (250, 250, 252, 255)       # cursor fill
OUTLINE = (24, 28, 36, 255)       # cursor outline

# The arrow outline in a 32x32 design grid, tip at the origin so the hotspots of the real cursors
# this program recovers line up with what the icon depicts.
ARROW = [(1.0, 0.0), (1.0, 22.0), (6.0, 16.5), (10.0, 26.0),
         (14.0, 23.5), (10.0, 14.5), (18.5, 14.5)]


def point_in_polygon(x, y, polygon):
    """Even-odd rule."""
    inside = False
    count = len(polygon)
    for i in range(count):
        x1, y1 = polygon[i]
        x2, y2 = polygon[(i + 1) % count]
        if (y1 > y) != (y2 > y):
            x_at = x1 + ((y - y1) / (y2 - y1)) * (x2 - x1)
            if x < x_at:
                inside = not inside
    return inside


def polygon_bounds(polygon):
    xs = [p[0] for p in polygon]
    ys = [p[1] for p in polygon]
    return min(xs), min(ys), max(xs), max(ys)


def distance_to_polygon_edges(x, y, polygon):
    """Shortest distance from a point to the polygon's outline, used for the outline stroke."""
    best = float("inf")
    count = len(polygon)
    for i in range(count):
        x1, y1 = polygon[i]
        x2, y2 = polygon[(i + 1) % count]
        dx, dy = x2 - x1, y2 - y1
        length_squared = (dx * dx) + (dy * dy)
        if length_squared == 0:
            t = 0.0
        else:
            t = max(0.0, min(1.0, (((x - x1) * dx) + ((y - y1) * dy)) / length_squared))
        px, py = x1 + (t * dx), y1 + (t * dy)
        best = min(best, ((x - px) ** 2) + ((y - py) ** 2) ** 0.5)
    return best


def render(size):
    """Returns an RGBA bytearray of size x size, one byte per channel."""
    scale = size / 32.0
    arrow = [(x * scale, y * scale) for x, y in ARROW]
    _, _, arrow_right, arrow_bottom = polygon_bounds(arrow)

    # Supersample: average SUPERSAMPLE^2 sub-samples per output pixel.
    samples = max(1, SUPERSAMPLE)
    high = size * samples
    # Accumulate colour in linear-ish fashion: sum premultiplied rgb plus alpha.
    accum = [0.0] * (size * size * 4)

    step = 1.0 / samples
    for hy in range(high):
        y = (hy + 0.5) * step
        for hx in range(high):
            x = (hx + 0.5) * step

            colour = None

            # --- corner brackets -------------------------------------------------------
            # Each bracket is an L with two arms, inset from the corner and drawn with a
            # thickness proportional to the canvas so it survives at 16 px.
            arm = max(3.0, size * 0.26)
            thick = max(1.0, size * 0.075)
            inset = max(0.5, size * 0.03)

            for corner_x, corner_y, dir_x, dir_y in (
                (inset, inset, 1.0, 1.0),
                (size - inset, inset, -1.0, 1.0),
                (inset, size - inset, 1.0, -1.0),
                (size - inset, size - inset, -1.0, -1.0),
            ):
                # Horizontal arm.
                hx0 = min(corner_x, corner_x + (dir_x * arm))
                hx1 = max(corner_x, corner_x + (dir_x * arm))
                hy0 = corner_y if dir_y > 0 else corner_y - thick
                hy1 = hy0 + thick
                if hx0 <= x <= hx1 and hy0 <= y <= hy1:
                    colour = BRACKET
                    break

                # Vertical arm.
                vx0 = corner_x if dir_x > 0 else corner_x - thick
                vx1 = vx0 + thick
                vy0 = min(corner_y, corner_y + (dir_y * arm))
                vy1 = max(corner_y, corner_y + (dir_y * arm))
                if vx0 <= x <= vx1 and vy0 <= y <= vy1:
                    colour = BRACKET
                    break

            # --- arrow -----------------------------------------------------------------
            # The arrow wins over the brackets where they overlap, so the pointer reads clearly.
            if x <= arrow_right + 2 and y <= arrow_bottom + 2:
                if point_in_polygon(x, y, arrow):
                    colour = BODY
                elif distance_to_polygon_edges(x, y, arrow) <= max(0.9, size * 0.035):
                    colour = OUTLINE

            if colour is not None:
                index = (int(y) * size) + int(x)
                if index < size * size:
                    base = index * 4
                    accum[base] += colour[0]
                    accum[base + 1] += colour[1]
                    accum[base + 2] += colour[2]
                    accum[base + 3] += 255

    total = samples * samples
    out = bytearray(size * size * 4)
    for i in range(size * size):
        base = i * 4
        alpha = accum[base + 3] / total
        if alpha <= 0.5:
            out[base] = out[base + 1] = out[base + 2] = out[base + 3] = 0
            continue
        # Un-premultiply the averaged colour so the stored RGB is straight alpha.
        coverage = accum[base + 3] / 255.0
        out[base] = min(255, int(round(accum[base + 2] / coverage)))      # B
        out[base + 1] = min(255, int(round(accum[base + 1] / coverage)))  # G
        out[base + 2] = min(255, int(round(accum[base] / coverage)))      # R
        out[base + 3] = min(255, int(round(alpha)))
    return out


def bmp_entry(rgba, size):
    """A 32bpp bottom-up DIB with an AND mask, the classic ICO payload."""
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0,
                         size * size * 4, 0, 0, 0, 0)
    xor = bytearray()
    for y in range(size - 1, -1, -1):
        row = rgba[y * size * 4:(y + 1) * size * 4]
        # ICO stores BGRA, which is already the order render() produced.
        xor += row

    and_stride = ((size + 31) // 32) * 4
    and_mask = bytearray()
    for y in range(size - 1, -1, -1):
        row = bytearray(and_stride)
        for x in range(size):
            if rgba[((y * size) + x) * 4 + 3] == 0:
                row[x >> 3] |= 0x80 >> (x & 7)
        and_mask += row
    return header + bytes(xor) + bytes(and_mask)


def png_entry(rgba, size):
    """A PNG payload; Vista and later accept these, and it keeps the 256 px entry small."""
    raw = bytearray()
    for y in range(size):
        raw.append(0)
        for x in range(size):
            i = ((y * size) + x) * 4
            raw += bytes((rgba[i + 2], rgba[i + 1], rgba[i], rgba[i + 3]))  # RGBA

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data +
                struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    return (b"\x89PNG\r\n\x1a\n" +
            chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)) +
            chunk(b"IDAT", zlib.compress(bytes(raw), 9)) +
            chunk(b"IEND", b""))


def build(path):
    entries = []
    for size in SIZES:
        rgba = render(size)
        payload = png_entry(rgba, size) if size >= 128 else bmp_entry(rgba, size)
        entries.append((size, payload, rgba))

    out = bytearray(struct.pack("<HHH", 0, 1, len(entries)))
    offset = 6 + (16 * len(entries))
    for size, payload, _ in entries:
        dimension = 0 if size >= 256 else size
        out += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(payload), offset)
        offset += len(payload)
    for _, payload, _ in entries:
        out += payload

    with open(path, "wb") as handle:
        handle.write(out)

    # A quick self-check: the mark must have drawn something, and it must have transparency,
    # otherwise the icon would be a solid square in the taskbar.
    for size, payload, rgba in entries:
        alpha = [rgba[i] for i in range(3, len(rgba), 4)]
        opaque = sum(1 for a in alpha if a > 200)
        clear = sum(1 for a in alpha if a == 0)
        assert opaque > 0, "size %d rendered nothing" % size
        assert clear > 0, "size %d has no transparent pixels" % size
        assert opaque < len(alpha), "size %d is entirely opaque" % size
        # The arrow's body sits roughly a third of the way across and down; it must be solid,
        # which is what catches a polygon that failed to rasterise.
        mid = ((size * 3 // 10) * size) + (size * 3 // 10)
        assert rgba[(mid * 4) + 3] > 200, "size %d has no solid arrow body" % size

    print("wrote %s: %d entries (%s), %d bytes" %
          (path, len(entries), ", ".join(str(s) for s in SIZES), len(out)))


if __name__ == "__main__":
    target = sys.argv[1] if len(sys.argv) > 1 else os.path.join("src", "RatTrap", "app.ico")
    os.makedirs(os.path.dirname(target) or ".", exist_ok=True)
    build(target)
