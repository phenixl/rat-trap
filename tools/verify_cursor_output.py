"""Independently verify rat-trap's cursor output files.

This deliberately shares NO code with the Rust... sorry, C# implementation and no code with
its self-test. It re-derives the PNG, CUR/ICO and ANI layouts from the format definitions and
cross-checks the three artefacts against each other, so a bug that is consistent across the
writer and its own round-trip parser still shows up here.

Usage:
    python tools/verify_cursor_output.py <outdir>

Checks, in order:
  1. PNG: signature, IHDR, every chunk CRC, zlib stream, per-row filter reversal.
  2. CUR: ICONDIR/ICONDIRENTRY field ranges, hotspot from wPlanes/wBitCount, DIB header,
     biHeight == 2*height, 32bpp, bottom-up XOR and AND planes, AND-mask padding.
  3. CUR XOR pixels == PNG pixels, byte for byte (both are straight alpha).
  4. CUR AND mask == 1 exactly where PNG alpha == 0.
  5. ANI: RIFF/ACON size bookkeeping, anih fields, LIST/fram, and each `icon` chunk parsed as
     a nested CUR whose pixels equal the PNG.
Exit code 0 iff everything passes.
"""

import os
import struct
import sys
import zlib

failures = []
checks = 0


def check(ok, what, detail=""):
    global checks
    checks += 1
    if ok:
        print("  ok   %s" % what)
    else:
        print("FAIL %s%s" % (what, (": " + detail) if detail else ""))
        failures.append(what)


def read_png(path):
    """Minimal PNG reader for 8-bit colour type 6, non-interlaced."""
    with open(path, "rb") as f:
        data = f.read()

    check(data[:8] == b"\x89PNG\r\n\x1a\n", "png signature")

    pos = 8
    width = height = None
    idat = b""
    seen_iend = False
    chunk_index = 0
    while pos < len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        tag = data[pos + 4:pos + 8]
        payload = data[pos + 8:pos + 8 + length]
        (stored_crc,) = struct.unpack(">I", data[pos + 8 + length:pos + 12 + length])
        actual_crc = zlib.crc32(tag + payload) & 0xFFFFFFFF
        check(stored_crc == actual_crc, "png chunk %d (%s) CRC" % (chunk_index, tag.decode("ascii", "replace")),
              "stored %08X actual %08X" % (stored_crc, actual_crc))
        chunk_index += 1

        if tag == b"IHDR":
            w, h, depth, colour, comp, filt, interlace = struct.unpack(">IIBBBBB", payload)
            width, height = w, h
            check((depth, colour, comp, filt, interlace) == (8, 6, 0, 0, 0),
                  "png IHDR is 8-bit RGBA non-interlaced",
                  "got depth=%d colour=%d comp=%d filter=%d interlace=%d" % (depth, colour, comp, filt, interlace))
        elif tag == b"IDAT":
            idat += payload
        elif tag == b"IEND":
            seen_iend = True
        pos += 12 + length

    check(seen_iend, "png has IEND")

    raw = zlib.decompress(idat)
    stride = width * 4
    check(len(raw) == (stride + 1) * height, "png inflated size",
          "got %d expected %d" % (len(raw), (stride + 1) * height))

    out = bytearray(stride * height)
    prev = bytearray(stride)
    filters_used = []
    offset = 0
    for y in range(height):
        ftype = raw[offset]
        filters_used.append(ftype)
        line = bytearray(raw[offset + 1:offset + 1 + stride])
        offset += 1 + stride
        for i in range(stride):
            a = line[i - 4] if i >= 4 else 0
            b = prev[i]
            c = prev[i - 4] if i >= 4 else 0
            x = line[i]
            if ftype == 0:
                value = x
            elif ftype == 1:
                value = x + a
            elif ftype == 2:
                value = x + b
            elif ftype == 3:
                value = x + ((a + b) >> 1)
            elif ftype == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                value = x + pr
            else:
                raise ValueError("bad filter type %d on row %d" % (ftype, y))
            line[i] = value & 0xFF
        out[y * stride:(y + 1) * stride] = line
        prev = line

    print("    png %dx%d filters=%s" % (width, height, sorted(set(filters_used))))
    return width, height, bytes(out)


def parse_cur(data, label):
    """Parses a CUR/ICO container and returns (images, id_type, hotspot)."""
    reserved, id_type, count = struct.unpack("<HHH", data[:6])
    check(reserved == 0, "%s idReserved is 0" % label, "got %d" % reserved)
    check(id_type in (1, 2), "%s idType is 1 (icon) or 2 (cursor)" % label, "got %d" % id_type)
    check(count >= 1, "%s has at least one image" % label, "got %d" % count)

    images = []
    for i in range(count):
        entry = data[6 + (i * 16):6 + (i * 16) + 16]
        b_w, b_h, colours, _res, planes, bitcount, size, offset = struct.unpack("<BBBBHHII", entry)
        width = b_w or 256
        height = b_h or 256
        # For a cursor, the two ICON fields carry the hotspot instead.
        hotspot = (planes, bitcount) if id_type == 2 else (0, 0)

        check(offset + size <= len(data), "%s image %d payload inside file" % (label, i))
        payload = data[offset:offset + size]

        (header_size, bi_w, bi_h, bi_planes, bi_bits, bi_compression,
         bi_size_image) = struct.unpack("<IiiHHII", payload[:24])
        check(header_size == 40, "%s image %d BITMAPINFOHEADER size" % (label, i), "got %d" % header_size)
        check(bi_w == width and bi_h == 2 * height,
              "%s image %d geometry and doubled height" % (label, i),
              "biWidth=%d biHeight=%d for %dx%d" % (bi_w, bi_h, width, height))
        check(bi_planes == 1, "%s image %d biPlanes" % (label, i), "got %d" % bi_planes)
        check(bi_compression == 0, "%s image %d is BI_RGB" % (label, i), "got %d" % bi_compression)

        if bi_bits == 32:
            xor_stride = width * 4
            xor_bytes = xor_stride * height
            and_stride = ((width + 31) // 32) * 4
            and_bytes = and_stride * height
            check(bi_size_image == xor_bytes + and_bytes,
                  "%s image %d biSizeImage covers both planes" % (label, i),
                  "got %d expected %d" % (bi_size_image, xor_bytes + and_bytes))
            check(len(payload) >= 40 + xor_bytes + and_bytes,
                  "%s image %d payload length" % (label, i),
                  "got %d expected %d" % (len(payload), 40 + xor_bytes + and_bytes))

            # Bottom-up: the last stored row is the top row of the image.
            xor_raw = payload[40:40 + xor_bytes]
            pixels = bytearray(xor_bytes)
            for y in range(height):
                src = (height - 1 - y) * xor_stride
                pixels[y * xor_stride:(y + 1) * xor_stride] = xor_raw[src:src + xor_stride]

            and_raw = payload[40 + xor_bytes:40 + xor_bytes + and_bytes]
            and_bits = bytearray(and_bytes)
            for y in range(height):
                src = (height - 1 - y) * and_stride
                and_bits[y * and_stride:(y + 1) * and_stride] = and_raw[src:src + and_stride]

            images.append({
                "width": width, "height": height, "bits": 32,
                "pixels": bytes(pixels), "and": bytes(and_bits),
                "and_stride": and_stride, "hotspot": hotspot,
            })
        else:
            images.append({"width": width, "height": height, "bits": bi_bits, "payload": payload,
                           "hotspot": hotspot})

    return images, id_type, images[0]["hotspot"]


def parse_ani(data):
    check(data[:4] == b"RIFF", "ani starts with RIFF")
    (riff_size,) = struct.unpack("<I", data[4:8])
    check(data[8:12] == b"ACON", "ani form type is ACON", repr(data[8:12]))
    check(riff_size + 8 == len(data), "ani RIFF size matches the file",
          "riff_size=%d file=%d" % (riff_size, len(data)))

    chunks = []
    pos = 12
    while pos + 8 <= len(data):
        tag = data[pos:pos + 4]
        (size,) = struct.unpack("<I", data[pos + 4:pos + 8])
        payload = data[pos + 8:pos + 8 + size]
        chunks.append((tag, payload))
        pos += 8 + size + (size & 1)

    tags = [t for t, _ in chunks]
    check(b"anih" in tags, "ani has an anih chunk")
    check(b"LIST" in tags, "ani has a LIST chunk")

    anih = next(p for t, p in chunks if t == b"anih")
    (cb_size, frames, steps, cx, cy, bitcount, planes, jif_rate, flags) = struct.unpack("<9I", anih[:36])
    print("    anih: frames=%d steps=%d %dx%d bpp=%d planes=%d jifRate=%d flags=0x%X"
          % (frames, steps, cx, cy, bitcount, planes, jif_rate, flags))
    check(cb_size == 36, "ani anih cbSize", "got %d" % cb_size)
    check(flags & 0x1, "ani AF_ICON flag is set", "flags=0x%X" % flags)

    list_payload = next(p for t, p in chunks if t == b"LIST")
    check(list_payload[:4] == b"fram", "ani LIST type is 'fram'", repr(list_payload[:4]))

    icons = []
    pos = 4
    while pos + 8 <= len(list_payload):
        tag = list_payload[pos:pos + 4]
        (size,) = struct.unpack("<I", list_payload[pos + 4:pos + 8])
        if tag == b"icon":
            icons.append(list_payload[pos + 8:pos + 8 + size])
        pos += 8 + size + (size & 1)

    check(len(icons) == frames, "ani icon chunk count matches anih cFrames",
          "icons=%d frames=%d" % (len(icons), frames))
    return icons


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    outdir = sys.argv[1]
    png_path = os.path.join(outdir, "cursor.png")
    cur_path = os.path.join(outdir, "cursor.cur")
    ani_path = os.path.join(outdir, "cursor.ani")

    for path in (png_path, cur_path, ani_path):
        if not os.path.exists(path):
            print("FAIL missing %s" % path)
            return 1

    print("== PNG ==")
    width, height, rgba = read_png(png_path)

    print("== CUR ==")
    with open(cur_path, "rb") as f:
        cur_data = f.read()
    images, id_type, hotspot = parse_cur(cur_data, "cur")
    check(id_type == 2, "cur idType is 2 (cursor)", "got %d" % id_type)
    cur = images[0]
    print("    cur: %dx%d %dbpp hotspot=%s" % (cur["width"], cur["height"], cur["bits"], cur["hotspot"]))
    check(cur["bits"] == 32, "cur image is 32bpp", "got %d" % cur["bits"])
    check((cur["width"], cur["height"]) == (width, height), "cur size matches png",
          "%dx%d vs %dx%d" % (cur["width"], cur["height"], width, height))

    print("== CUR pixels == PNG pixels ==")
    differing = 0
    worst = 0
    for i in range(width * height * 4):
        a, b = cur["pixels"][i], rgba[i]
        if a != b:
            differing += 1
            worst = max(worst, abs(a - b))
    check(differing == 0, "cur XOR plane equals png RGBA byte for byte",
          "%d of %d bytes differ, worst delta %d" % (differing, len(rgba), worst))

    print("== CUR AND mask == png alpha == 0 ==")
    mismatched = 0
    stride = cur["and_stride"]
    for y in range(height):
        for x in range(width):
            bit = (cur["and"][y * stride + (x >> 3)] >> (7 - (x & 7))) & 1
            transparent = rgba[((y * width) + x) * 4 + 3] == 0
            if bit != (1 if transparent else 0):
                mismatched += 1
    check(mismatched == 0, "cur AND mask marks exactly the transparent pixels",
          "%d pixels disagree" % mismatched)

    print("== ANI ==")
    with open(ani_path, "rb") as f:
        ani_data = f.read()
    icons = parse_ani(ani_data)
    check(len(icons) >= 1, "ani has at least one frame")
    for index, payload in enumerate(icons):
        nested, nested_type, nested_hotspot = parse_cur(payload, "ani frame %d" % index)
        check(nested_type == 2, "ani frame %d is a cursor image" % index, "got %d" % nested_type)
        inner = nested[0]
        same = inner.get("pixels") == cur["pixels"]
        check(same, "ani frame %d is byte-identical to the .cur" % index)
        check(inner["hotspot"] == hotspot, "ani frame %d hotspot matches" % index,
              "%s vs %s" % (inner["hotspot"], hotspot))

    print()
    print("%d checks, %d failure(s)" % (checks, len(failures)))
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
