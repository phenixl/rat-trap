// cursor_probe.cpp - CursorTrap Phase 1 empirical probe
// Measures what the Windows cursor APIs actually return on this machine.
#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <shellscalingapi.h>
#include <stdio.h>
#include <string.h>
#include <vector>
#include <string>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "shcore.lib")

static const char* OUTDIR = "C:\\ai-projects\\cursor-trap\\probe\\out\\";

static void SaveBGRA(const std::string& name, const BYTE* px, int w, int h, int stride) {
    std::string path = std::string(OUTDIR) + name + ".bgra";
    FILE* f = fopen(path.c_str(), "wb");
    if (!f) { printf("    !! cannot write %s\n", path.c_str()); return; }
    for (int y = 0; y < h; y++) fwrite(px + (size_t)y * stride, 1, (size_t)w * 4, f);
    fclose(f);
    printf("    saved %s (%dx%d raw BGRA top-down)\n", path.c_str(), w, h);
}

struct BmpData { int w = 0, h = 0; std::vector<BYTE> px; bool ok = false; const char* how = "none"; };

static BmpData ReadHBITMAP32(HBITMAP hbm) {
    BmpData d;
    BITMAP bm = {};
    if (!hbm || !GetObject(hbm, sizeof(bm), &bm)) return d;
    d.w = bm.bmWidth; d.h = bm.bmHeight;
    if (bm.bmBits && bm.bmBitsPixel == 32) {
        int stride = bm.bmWidthBytes;
        d.px.resize((size_t)d.w * d.h * 4);
        for (int y = 0; y < d.h; y++)
            memcpy(d.px.data() + (size_t)y * d.w * 4, (BYTE*)bm.bmBits + (size_t)y * stride, (size_t)d.w * 4);
        d.ok = true; d.how = "direct DIB section bits";
        return d;
    }
    BITMAPINFO bi = {};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = d.w;
    bi.bmiHeader.biHeight = -d.h;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    d.px.resize((size_t)d.w * d.h * 4);
    HDC hdc = GetDC(NULL);
    int got = GetDIBits(hdc, hbm, 0, d.h, d.px.data(), &bi, DIB_RGB_COLORS);
    ReleaseDC(NULL, hdc);
    d.ok = (got != 0); d.how = "GetDIBits";
    return d;
}

static void AlphaStats(const char* tag, const BmpData& d) {
    if (!d.ok) { printf("    %s: no pixel data\n", tag); return; }
    size_t n = (size_t)d.w * d.h;
    size_t aNonZero = 0, aFull = 0, aZero = 0;
    int aMin = 255, aMax = 0;
    for (size_t i = 0; i < n; i++) {
        BYTE a = d.px[i * 4 + 3];
        if (a) aNonZero++; else aZero++;
        if (a == 255) aFull++;
        if (a < aMin) aMin = a; if (a > aMax) aMax = a;
    }
    printf("    %s alpha: nonzero=%zu/%zu full255=%zu min=%d max=%d\n", tag, aNonZero, n, aFull, aMin, aMax);
}

static void DumpBitmap(const char* tag, const char* savename, HBITMAP hbm) {
    BITMAP bm = {};
    if (!hbm) { printf("  %s: NULL bitmap\n", tag); return; }
    if (!GetObject(hbm, sizeof(bm), &bm)) { printf("  %s: GetObject failed gle=%lu\n", tag, GetLastError()); return; }
    printf("  %s: %ldx%ld bpp=%u planes=%u bmBits=%s bmWidthBytes=%ld\n",
        tag, bm.bmWidth, bm.bmHeight, bm.bmBitsPixel, bm.bmPlanes,
        bm.bmBits ? "non-null(DIB section)" : "null(DDB)", bm.bmWidthBytes);
    if (savename) {
        BmpData d = ReadHBITMAP32(hbm);
        printf("    reader: %s\n", d.how);
        AlphaStats(tag, d);
        if (d.ok) SaveBGRA(savename, d.px.data(), d.w, d.h, d.w * 4);
    }
}

static void RegDword(HKEY root, const char* sub, const char* name) {
    DWORD v = 0, cb = sizeof(v), type = 0;
    LSTATUS st = RegGetValueA(root, sub, name, RRF_RT_REG_DWORD, &type, &v, &cb);
    if (st == ERROR_SUCCESS) printf("  %s\\%s = %lu\n", sub, name, v);
    else printf("  %s\\%s = <not present, status=%ld>\n", sub, name, st);
}

static void ProbeIconInfo(const char* tag, HCURSOR hcur, const char* savePrefix) {
    ICONINFO ii = {};
    if (!GetIconInfo(hcur, &ii)) { printf("  [%s] GetIconInfo failed gle=%lu\n", tag, GetLastError()); return; }
    printf("  [%s] hotspot=(%ld,%ld) fIcon=%d\n", tag, ii.xHotspot, ii.yHotspot, ii.fIcon);
    DumpBitmap("hbmColor", savePrefix ? (std::string(savePrefix) + "_color").c_str() : nullptr, ii.hbmColor);
    DumpBitmap("hbmMask", nullptr, ii.hbmMask);
    if (ii.hbmColor) DeleteObject(ii.hbmColor);
    if (ii.hbmMask) DeleteObject(ii.hbmMask);
}

// ---------- .cur / .ico directory dump ----------
static void DumpIconDir(const char* path) {
    FILE* f = fopen(path, "rb");
    if (!f) { printf("  cannot open %s\n", path); return; }
    fseek(f, 0, SEEK_END); long sz = ftell(f); fseek(f, 0, SEEK_SET);
    if (sz < 6) { fclose(f); printf("  %s: too small\n", path); return; }
    std::vector<BYTE> b((size_t)sz);
    if (fread(b.data(), 1, (size_t)sz, f) != (size_t)sz) { fclose(f); printf("  %s: read error\n", path); return; }
    fclose(f);
    if (b[0] != 0 || b[1] != 0 || (b[2] != 2 && b[2] != 1)) { printf("  %s: not ICO/CUR\n", path); return; }
    int type = b[2];
    int count = b[4] | (b[5] << 8);
    printf("  FILE %s  type=%d (%s) entries=%d size=%ld\n", path, type,
        type == 2 ? "CUR" : "ICO", count, sz);
    for (int i = 0; i < count; i++) {
        const BYTE* e = b.data() + 6 + 16 * i;
        int w = e[0] ? e[0] : 256, h = e[1] ? e[1] : 256;
        DWORD bytes = *(DWORD*)(e + 8), off = *(DWORD*)(e + 12);
        unsigned planes = *(WORD*)(e + 4), bpp = *(WORD*)(e + 6);
        const char* kind = "BMP";
        if (off + 8 <= (DWORD)sz && b[off] == 0x89 && b[off + 1] == 'P') kind = "PNG";
        if (type == 2)
            printf("    [%d] %dx%d hotspot=(%u,%u) rawPlanes=%u rawBitCount=%u %s bytes=%lu off=%lu\n",
                i, w, h, planes, bpp, planes, bpp, kind, bytes, off);
        else
            printf("    [%d] %dx%d bpp=%u %s bytes=%lu off=%lu\n", i, w, h, bpp, kind, bytes, off);
    }
}

// ---------- .ani (RIFF/ACON) parser ----------
static const char* FourCC(DWORD v) {
    static char buf[5];
    buf[0] = (char)(v & 0xff); buf[1] = (char)((v >> 8) & 0xff);
    buf[2] = (char)((v >> 16) & 0xff); buf[3] = (char)((v >> 24) & 0xff); buf[4] = 0;
    for (int i = 0; i < 4; i++) if (buf[i] < 32 || buf[i] > 126) buf[i] = '.';
    return buf;
}

static void DumpAni(const char* path) {
    FILE* f = fopen(path, "rb");
    if (!f) { printf("  cannot open %s\n", path); return; }
    fseek(f, 0, SEEK_END); long sz = ftell(f); fseek(f, 0, SEEK_SET);
    std::vector<BYTE> b((size_t)sz);
    if (fread(b.data(), 1, (size_t)sz, f) != (size_t)sz) { fclose(f); return; }
    fclose(f);
    if (sz < 12 || memcmp(b.data(), "RIFF", 4) || memcmp(b.data() + 8, "ACON", 4)) {
        printf("  %s: not RIFF/ACON\n", path); return;
    }
    printf("  FILE %s  RIFF/ACON size=%ld\n", path, sz);
    DWORD pos = 12;
    int frames = 0, steps = 0;
    std::vector<DWORD> rates, seq;
    while (pos + 8 <= (DWORD)sz) {
        DWORD id = *(DWORD*)(b.data() + pos);
        DWORD len = *(DWORD*)(b.data() + pos + 4);
        printf("    chunk '%s' len=%lu at %lu\n", FourCC(id), len, pos);
        const BYTE* body = b.data() + pos + 8;
        if (id == *(DWORD*)"anih" && len >= 36) {
            DWORD cbSize = *(DWORD*)(body + 0);
            DWORD cFrames = *(DWORD*)(body + 4);
            DWORD cSteps = *(DWORD*)(body + 8);
            DWORD cx = *(DWORD*)(body + 12), cy = *(DWORD*)(body + 16);
            DWORD cBitCount = *(DWORD*)(body + 20), cPlanes = *(DWORD*)(body + 24);
            DWORD jifRate = *(DWORD*)(body + 28), flags = *(DWORD*)(body + 32);
            frames = (int)cFrames; steps = (int)cSteps;
            printf("      ANIH: cbSize=%lu cFrames=%lu cSteps=%lu cx=%lu cy=%lu cBitCount=%lu cPlanes=%lu jifRate=%lu flags=0x%lX (%s%s)\n",
                cbSize, cFrames, cSteps, cx, cy, cBitCount, cPlanes, jifRate, flags,
                (flags & 1) ? "AF_ICON " : "", (flags & 2) ? "AF_SEQUENCE" : "");
            printf("      => default frame duration = %lu jiffies = %.1f ms\n", jifRate, jifRate * 1000.0 / 60.0);
        }
        else if (id == *(DWORD*)"rate") {
            DWORD n = len / 4;
            printf("      RATE:");
            for (DWORD i = 0; i < n; i++) { DWORD r = *(DWORD*)(body + 4 * i); rates.push_back(r); printf(" %lu(%.1fms)", r, r * 1000.0 / 60.0); }
            printf("\n");
        }
        else if (id == *(DWORD*)"seq ") {
            DWORD n = len / 4;
            printf("      SEQ:");
            for (DWORD i = 0; i < n; i++) { DWORD s = *(DWORD*)(body + 4 * i); seq.push_back(s); printf(" %lu", s); }
            printf("\n");
        }
        else if (id == *(DWORD*)"LIST") {
            DWORD listType = *(DWORD*)body;
            printf("      LIST type='%s'; scanning 'fram' subchunks\n", FourCC(listType));
            DWORD p = pos + 12;
            DWORD end = pos + 8 + len;
            int idx = 0;
            while (p + 8 <= end) {
                DWORD sid = *(DWORD*)(b.data() + p);
                DWORD slen = *(DWORD*)(b.data() + p + 4);
                if (sid == *(DWORD*)"fram") {
                    const BYTE* fb = b.data() + p + 8;
                    printf("        fram[%d] len=%lu", idx, slen);
                    if (slen >= 16) {
                        DWORD picSize = *(DWORD*)(fb + 0);
                        DWORD picOffset = *(DWORD*)(fb + 4);
                        DWORD x = *(DWORD*)(fb + 8), y = *(DWORD*)(fb + 12);
                        printf("  ICONFILE(picSize=%lu picOffset=%lu x=%lu y=%lu)", picSize, picOffset, x, y);
                        // parse embedded ICO/CUR dir
                        size_t abs = p + 8 + 16;
                        if (abs + 6 <= (size_t)sz && *(WORD*)(b.data() + abs + 2) == 2) {
                            int n = *(WORD*)(b.data() + abs + 4);
                            printf(" embeddedCURentries=%d", n);
                            for (int k = 0; k < n && abs + 6 + 16 * (k + 1) <= (size_t)sz; k++) {
                                const BYTE* e = b.data() + abs + 6 + 16 * k;
                                printf(" {%dx%d hs=(%u,%u)}", e[0] ? e[0] : 256, e[1] ? e[1] : 256, *(WORD*)(e + 4), *(WORD*)(e + 6));
                            }
                        }
                    }
                    printf("\n");
                    idx++;
                }
                else {
                    printf("        subchunk '%s' len=%lu\n", FourCC(sid), slen);
                }
                p += 8 + slen + (slen & 1);
            }
        }
        pos += 8 + len + (len & 1);
    }
    printf("    summary: %d frames, %d steps, %zu rates, %zu seq entries\n", frames, steps, rates.size(), seq.size());
}

// ---------- DrawIconEx alpha fidelity test ----------
static void DrawTest(const char* tag, HCURSOR hcur, int drawW, int drawH) {
    HDC hdc = CreateCompatibleDC(NULL);
    BITMAPINFO bi = {};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = drawW;
    bi.bmiHeader.biHeight = -drawH;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HBITMAP dib = CreateDIBSection(hdc, &bi, DIB_RGB_COLORS, &bits, NULL, 0);
    HGDIOBJ old = SelectObject(hdc, dib);
    memset(bits, 0, (size_t)drawW * drawH * 4);
    BOOL ok = DrawIconEx(hdc, 0, 0, hcur, drawW, drawH, 0, NULL, DI_NORMAL);
    printf("  [DrawTest %s] DrawIconEx(%dx%d) ok=%d gle=%lu\n", tag, drawW, drawH, ok, ok ? 0 : GetLastError());
    BmpData d; d.w = drawW; d.h = drawH; d.px.assign((BYTE*)bits, (BYTE*)bits + (size_t)drawW * drawH * 4); d.ok = true; d.how = "dibsection";
    AlphaStats(tag, d);
    SaveBGRA(std::string("draw_") + tag, d.px.data(), d.w, d.h, d.w * 4);
    SelectObject(hdc, old);
    DeleteObject(dib);
    DeleteDC(hdc);
}

int main() {
    CreateDirectoryA("C:\\ai-projects\\cursor-trap\\probe", NULL);
    CreateDirectoryA(OUTDIR, NULL);
    printf("=== CursorTrap probe ===\n");

    printf("\n--- 1. system metrics ---\n");
    printf("  SM_CXCURSOR=%d SM_CYCURSOR=%d SM_CXICON=%d SM_CYICON=%d SM_CXSMICON=%d SM_CYSMICON=%d\n",
        GetSystemMetrics(SM_CXCURSOR), GetSystemMetrics(SM_CYCURSOR),
        GetSystemMetrics(SM_CXICON), GetSystemMetrics(SM_CYICON),
        GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON));
    printf("  GetDpiForSystem=%u\n", GetDpiForSystem());

    printf("\n--- 2. registry cursor settings ---\n");
    RegDword(HKEY_CURRENT_USER, "Control Panel\\Cursors", "CursorBaseSize");
    RegDword(HKEY_CURRENT_USER, "Control Panel\\Cursors", "Scheme Source");
    RegDword(HKEY_CURRENT_USER, "Software\\Microsoft\\Accessibility", "CursorSize");
    RegDword(HKEY_CURRENT_USER, "Control Panel\\Desktop", "LogPixels");

    printf("\n--- 3. monitors ---\n");
    static int monIdx = 0;
    EnumDisplayMonitors(NULL, NULL, [](HMONITOR hm, HDC, LPRECT r, LPARAM lp) -> BOOL {
        MONITORINFOEXA mi = {}; mi.cbSize = sizeof(mi);
        GetMonitorInfoA(hm, &mi);
        UINT dx = 0, dy = 0;
        HRESULT hr = GetDpiForMonitor(hm, MDT_EFFECTIVE_DPI, &dx, &dy);
        DEVMODEA dm = {}; dm.dmSize = sizeof(dm);
        EnumDisplaySettingsA(mi.szDevice, ENUM_CURRENT_SETTINGS, &dm);
        printf("  [%d] %s rect=(%ld,%ld)-(%ld,%ld) %ldx%ld dpi=%ux%u (hr=0x%lX) primary=%d\n",
            monIdx++, mi.szDevice, r->left, r->top, r->right, r->bottom,
            r->right - r->left, r->bottom - r->top, dx, dy, hr,
            (mi.dwFlags & MONITORINFOF_PRIMARY) ? 1 : 0);
        return TRUE;
    }, 0);

    printf("\n--- 4. live cursor (GetCursorInfo) ---\n");
    for (int attempt = 0; attempt < 3; attempt++) {
        CURSORINFO ci = {}; ci.cbSize = sizeof(ci);
        if (!GetCursorInfo(&ci)) { printf("  GetCursorInfo failed gle=%lu\n", GetLastError()); break; }
        printf("  flags=0x%lX (CURSOR_SHOWING=%d) ptScreenPos=(%ld,%ld) hCursor=%p\n",
            ci.flags, (ci.flags & CURSOR_SHOWING) ? 1 : 0, ci.ptScreenPos.x, ci.ptScreenPos.y, (void*)ci.hCursor);
        if (!ci.hCursor) { printf("  hCursor NULL\n"); Sleep(400); continue; }

        POINT p = ci.ptScreenPos;
        HMONITOR hm = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        UINT dx = 0, dy = 0; GetDpiForMonitor(hm, MDT_EFFECTIVE_DPI, &dx, &dy);
        printf("  monitor under cursor dpi=%ux%u ; CursorBaseSize*scale = 80*%u/96 = %.1f\n", dx, dy, dx, 80.0 * dx / 96.0);

        printf("  -- direct GetIconInfo on live hCursor --\n");
        ProbeIconInfo("live-direct", ci.hCursor, "live_direct");

        printf("  -- CopyIcon then GetIconInfo (owned handle) --\n");
        HCURSOR hcopy = CopyIcon(ci.hCursor);
        if (hcopy) {
            ProbeIconInfo("live-copyicon", hcopy, "live_copyicon");
            // DrawIconEx alpha fidelity at native size and at 80x80
            ICONINFO ii = {};
            if (GetIconInfo(hcopy, &ii)) {
                BITMAP bm = {};
                if (ii.hbmColor && GetObject(ii.hbmColor, sizeof(bm), &bm)) {
                    DrawTest("live_native", hcopy, bm.bmWidth, bm.bmHeight);
                    DrawTest("live_80", hcopy, 80, 80);
                    DrawTest("live_160", hcopy, 160, 160);
                }
                if (ii.hbmColor) DeleteObject(ii.hbmColor);
                if (ii.hbmMask) DeleteObject(ii.hbmMask);
            }
            DestroyCursor(hcopy);
        } else printf("  CopyIcon failed gle=%lu\n", GetLastError());

        Sleep(300);
        if (attempt == 0) { printf("  (mouse may be over console - move it and we resample)\n"); }
    }

    printf("\n--- 5. theme cursor files on disk ---\n");
    DumpIconDir("C:\\Windows\\Cursors\\aero_arrow.cur");
    DumpIconDir("C:\\Windows\\Cursors\\aero_link.cur");
    DumpAni("C:\\Windows\\Cursors\\aero_busy.ani");
    DumpAni("C:\\Windows\\Cursors\\aero_working.ani");

    printf("\n--- 6. LoadCursorFromFile / LoadImage sizing ---\n");
    const char* files[] = { "C:\\Windows\\Cursors\\aero_arrow.cur", "C:\\Windows\\Cursors\\aero_busy.ani" };
    for (int i = 0; i < 2; i++) {
        printf("  file: %s\n", files[i]);
        HCURSOR hc = (HCURSOR)LoadImageA(NULL, files[i], IMAGE_CURSOR, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
        if (hc) {
            printf("    LoadImage(LR_DEFAULTSIZE) ok\n");
            ProbeIconInfo("loadimage-defaultsize", hc, i == 0 ? "file_arrow_lrdefault" : "file_busy_lrdefault");
            DestroyCursor(hc);
        } else printf("    LoadImage(LR_DEFAULTSIZE) failed gle=%lu\n", GetLastError());
        hc = (HCURSOR)LoadImageA(NULL, files[i], IMAGE_CURSOR, 0, 0, LR_LOADFROMFILE);
        if (hc) {
            printf("    LoadImage(no flags) ok\n");
            ProbeIconInfo("loadimage-plain", hc, i == 0 ? "file_arrow_plain" : "file_busy_plain");
            DestroyCursor(hc);
        } else printf("    LoadImage(no flags) failed gle=%lu\n", GetLastError());
    }

    printf("\n--- done ---\n");
    return 0;
}
