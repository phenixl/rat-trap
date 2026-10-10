"""Convert raw top-down BGRA files produced by cursor_probe into PNGs.

Usage: python bgra2png.py <file.bgra> <width> <height> [out.png]
Or:    python bgra2png.py --dir <outdir> <width> <height>   (converts every .bgra)
Pure stdlib (zlib) PNG writer - no Pillow required.
"""
import os
import struct
import sys
import zlib


def write_png(path, width, height, rgba_rows):
    raw = b"".join(b"\x00" + row for row in rgba_rows)

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 9))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def convert(bgra_path, width, height, out_path, opaque=False):
    with open(bgra_path, "rb") as f:
        data = f.read()
    expected = width * height * 4
    if len(data) < expected:
        print("skip %s: %d bytes < %d expected" % (bgra_path, len(data), expected))
        return False
    rows = []
    for y in range(height):
        row = data[y * width * 4:(y + 1) * width * 4]
        # BGRA -> RGBA
        px = bytearray(width * 4)
        px[0::4] = row[2::4]
        px[1::4] = row[1::4]
        px[2::4] = row[0::4]
        px[3::4] = b"\xff" * width if opaque else row[3::4]
        rows.append(bytes(px))
    write_png(out_path, width, height, rows)
    print("wrote %s (%dx%d)" % (out_path, width, height))
    return True


if __name__ == "__main__":
    argv = [a for a in sys.argv[1:] if a != "--opaque"]
    opaque = "--opaque" in sys.argv
    if len(argv) >= 3 and argv[0] == "--dir":
        d, w, h = argv[1], int(argv[2]), int(argv[3])
        for name in sorted(os.listdir(d)):
            if name.endswith(".bgra"):
                convert(os.path.join(d, name), w, h,
                        os.path.join(d, name[:-5] + ".png"), opaque)
    elif len(argv) >= 3:
        out = argv[3] if len(argv) > 3 else argv[0][:-5] + ".png"
        convert(argv[0], int(argv[1]), int(argv[2]), out, opaque)
    else:
        print(__doc__)
