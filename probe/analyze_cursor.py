"""Numerically decide how Windows renders the enlarged cursor.

Compares the DXGI Desktop Duplication pointer shape (ground truth of what the system
renders) against candidate renderings, over every plausible crop offset.
Pure stdlib.
"""
import os
import sys

OUT = r"C:\ai-projects\cursor-trap\probe\out"


def load(name, w, h):
    with open(os.path.join(OUT, name), "rb") as f:
        d = f.read()
    if len(d) < w * h * 4:
        raise SystemExit("short file %s: %d < %d" % (name, len(d), w * h * 4))
    return d


def px(d, w, x, y):
    o = (y * w + x) * 4
    return d[o], d[o + 1], d[o + 2], d[o + 3]


def alpha_hist(d, w, h):
    hist = {}
    for i in range(w * h):
        a = d[i * 4 + 3]
        hist[a] = hist.get(a, 0) + 1
    top = sorted(hist.items(), key=lambda kv: -kv[1])[:8]
    return len(hist), top


def bbox_alpha(d, w, h, thresh=0):
    minx, miny, maxx, maxy = w, h, -1, -1
    for y in range(h):
        for x in range(w):
            if d[(y * w + x) * 4 + 3] > thresh:
                if x < minx: minx = x
                if x > maxx: maxx = x
                if y < miny: miny = y
                if y > maxy: maxy = y
    return minx, miny, maxx, maxy


def upscale_nearest(src, sw, sh, dw, dh):
    out = bytearray(dw * dh * 4)
    for y in range(dh):
        sy = min(sh - 1, y * sh // dh)
        for x in range(dw):
            sx = min(sw - 1, x * sw // dw)
            so = (sy * sw + sx) * 4
            do = (y * dw + x) * 4
            out[do:do + 4] = src[so:so + 4]
    return bytes(out)


def upscale_bilinear(src, sw, sh, dw, dh):
    """Premultiplied-aware bilinear, alpha-weighted."""
    out = bytearray(dw * dh * 4)
    for y in range(dh):
        fy = (y + 0.5) * sh / dh - 0.5
        y0 = max(0, min(sh - 1, int(fy)))
        y1 = max(0, min(sh - 1, y0 + 1))
        wy = fy - y0
        if wy < 0: wy = 0.0
        for x in range(dw):
            fx = (x + 0.5) * sw / dw - 0.5
            x0 = max(0, min(sw - 1, int(fx)))
            x1 = max(0, min(sw - 1, x0 + 1))
            wx = fx - x0
            if wx < 0: wx = 0.0
            acc = [0.0, 0.0, 0.0, 0.0]
            for (sx, sy, wt) in ((x0, y0, (1 - wx) * (1 - wy)), (x1, y0, wx * (1 - wy)),
                                 (x0, y1, (1 - wx) * wy), (x1, y1, wx * wy)):
                o = (sy * sw + sx) * 4
                a = src[o + 3] / 255.0
                acc[0] += src[o] * a * wt
                acc[1] += src[o + 1] * a * wt
                acc[2] += src[o + 2] * a * wt
                acc[3] += src[o + 3] * wt
            a = acc[3]
            do = (y * dw + x) * 4
            if a > 0:
                out[do] = min(255, int(acc[0] * 255.0 / a + 0.5))
                out[do + 1] = min(255, int(acc[1] * 255.0 / a + 0.5))
                out[do + 2] = min(255, int(acc[2] * 255.0 / a + 0.5))
            out[do + 3] = min(255, int(a + 0.5))
    return bytes(out)


def compare(shape, sw, sh, cand, cw, ch, offs):
    """Mean alpha-weighted color error of cand placed at (ox,oy) inside shape."""
    best = None
    for oy in offs:
        for ox in offs:
            tot = 0.0
            n = 0
            bad = 0
            for y in range(ch):
                sy = y + oy
                if sy < 0 or sy >= sh:
                    continue
                for x in range(cw):
                    sx = x + ox
                    if sx < 0 or sx >= sw:
                        continue
                    ca = cand[(y * cw + x) * 4 + 3]
                    sa = shape[(sy * sw + sx) * 4 + 3]
                    if ca == 0 and sa == 0:
                        continue
                    # compare colour only where either side is visible
                    d = 0
                    for k in range(3):
                        d += abs(cand[(y * cw + x) * 4 + k] - shape[(sy * sw + sx) * 4 + k])
                    d += abs(ca - sa)
                    tot += d
                    n += 1
                    if d > 90:
                        bad += 1
            if n == 0:
                continue
            mae = tot / (n * 4.0)
            if best is None or mae < best[0]:
                best = (mae, ox, oy, n, bad)
    return best


def main():
    print("=" * 78)
    print("DXGI pointer shape analysis")
    print("=" * 78)

    arrow = load("dxgi_shape_0_89x87_t2.bgra", 89, 87)
    ibeam = load("dxgi_shape_1_80x80_t4.bgra", 80, 80)

    for nm, d, w, h in (("arrow 89x87 t2", arrow, 89, 87), ("ibeam 80x80 t4", ibeam, 80, 80)):
        nd, top = alpha_hist(d, w, h)
        bb = bbox_alpha(d, w, h, 0)
        print("\n%s: distinct alpha values = %d" % (nm, nd))
        print("   top alpha values:", ", ".join("a=%d:%d" % kv for kv in top))
        print("   alpha>0 bbox = %s -> %dx%d" % (bb, bb[2] - bb[0] + 1, bb[3] - bb[1] + 1))

    # how opaque is the arrow's white body / is it premultiplied?
    print("\narrow samples (x,y,b,g,r,a):")
    for (x, y) in ((5, 5), (10, 20), (20, 30), (40, 40), (88, 86), (60, 60)):
        b, g, r, a = px(arrow, 89, x, y)
        print("   (%2d,%2d) b=%3d g=%3d r=%3d a=%3d" % (x, y, b, g, r, a))

    # candidates for the arrow at 80 px
    cand_a = load("sz_aero_arrow.cur_80.bgra", 80, 80)          # LoadImage cz=80 (system resample of the .cur)
    src32 = load("file_arrow_lrdefault_color.bgra", 32, 32)     # .cur's 32x32 (== what GetIconInfo gives)
    cand_n = upscale_nearest(src32, 32, 32, 80, 80)
    cand_b = upscale_bilinear(src32, 32, 32, 80, 80)
    for nm, c in (("cand_n", cand_n), ("cand_b", cand_b)):
        with open(os.path.join(OUT, "cand_%s_80.bgra" % nm), "wb") as f:
            f.write(c)

    offs = list(range(-8, 12))
    print("\n" + "-" * 78)
    print("Arrow: match of each candidate inside the 89x87 duplicated shape")
    print("-" * 78)
    for label, cand in (("LoadImage(cx=80) resample of .cur  [system's own scaler]", cand_a),
                        ("nearest-neighbour upscale of .cur's 32x32", cand_n),
                        ("bilinear (alpha-weighted) upscale of .cur's 32x32", cand_b)):
        r = compare(arrow, 89, 87, cand, 80, 80, offs)
        if r:
            print("  %-58s MAE=%6.2f at offset (%3d,%3d) n=%d bad=%d" % (label, r[0], r[1], r[2], r[3], r[4]))

    # Also: how big is the real cursor inside the padded shape?  bbox of the strong-alpha core
    core = bbox_alpha(arrow, 89, 87, 200)
    print("\narrow strong-alpha(a>200) core bbox = %s -> %dx%d" % (core, core[2] - core[0] + 1, core[3] - core[1] + 1))
    print("duplicated shape is %dx%d, reported hotspot=(3,3)" % (89, 87))


if __name__ == "__main__":
    main()
