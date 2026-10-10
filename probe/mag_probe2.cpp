// mag_probe2.cpp - corrected experiment: MS_* are WINDOW STYLES (magnification.h line 42-45).
// Side-by-side magnifier windows with identical source rect + filter list; the ONLY
// difference is the window style bit. Diffing the halves isolates exactly what the
// style bit adds -> the real, system-rendered cursor pixels.
#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <magnification.h>
#include <stdio.h>
#include <string.h>
#include <vector>
#include <string>

#pragma comment(lib, "Magnification.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")

static const int TILE = 192;      // each magnifier's client size
static const int GAP = 8;
static const char* OUTDIR = "C:\\ai-projects\\cursor-trap\\probe\\out\\";

static HWND g_host = nullptr;
static HWND g_magA = nullptr, g_magB = nullptr, g_magC = nullptr;
static volatile LONG g_cbCount = 0;

static void GuidStr(const GUID& g, char* buf, size_t n) {
    sprintf_s(buf, n, "%08lX-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X",
        (unsigned long)g.Data1, g.Data2, g.Data3, g.Data4[0], g.Data4[1], g.Data4[2],
        g.Data4[3], g.Data4[4], g.Data4[5], g.Data4[6], g.Data4[7]);
}

static BOOL CALLBACK MagScalingCB(HWND hwnd, void* srcdata, MAGIMAGEHEADER src,
    void* destdata, MAGIMAGEHEADER dst, RECT unclipped, RECT clipped, HRGN dirty) {
    LONG n = InterlockedIncrement(&g_cbCount);
    char gd[64] = { 0 }, gs[64] = { 0 };
    GuidStr(dst.format, gd, sizeof(gd));
    GuidStr(src.format, gs, sizeof(gs));
    printf("    [scalingCB #%ld] hwnd=%p (magA=%p magB=%p magC=%p)\n", n, (void*)hwnd, (void*)g_magA, (void*)g_magB, (void*)g_magC);
    printf("        src %ux%u stride=%u fmt=%s | dest %ux%u stride=%u off=%u cb=%zu fmt=%s\n",
        src.width, src.height, src.stride, gs, dst.width, dst.height, dst.stride, dst.offset, (size_t)dst.cbSize, gd);
    if (destdata && dst.width && dst.height && n <= 6) {
        char name[512];
        const char* which = (hwnd == g_magA) ? "A" : (hwnd == g_magB ? "B" : (hwnd == g_magC ? "C" : "X"));
        sprintf_s(name, "%scb2_%s_%ld.bgra", OUTDIR, which, n);
        FILE* f = fopen(name, "wb");
        if (f) {
            size_t row = dst.stride ? dst.stride : (size_t)dst.width * 4;
            for (UINT y = 0; y < dst.height; y++)
                fwrite((const BYTE*)destdata + dst.offset + (size_t)y * row, 1, (size_t)dst.width * 4, f);
            fclose(f);
            printf("        dumped %s\n", name);
        }
    }
    return TRUE;
}

static LRESULT CALLBACK HostProc(HWND h, UINT m, WPARAM w, LPARAM l) { return DefWindowProcA(h, m, w, l); }

static void Pump(int ms) {
    DWORD end = GetTickCount() + ms;
    MSG msg;
    while (GetTickCount() < end) {
        while (PeekMessageA(&msg, NULL, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageA(&msg); }
        Sleep(10);
    }
}

static bool GrabScreen(int x, int y, int w, int h, std::vector<BYTE>& px) {
    px.assign((size_t)w * h * 4, 0);
    HDC hdcScreen = GetDC(NULL);
    BITMAPINFO bi = {};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = w; bi.bmiHeader.biHeight = -h;
    bi.bmiHeader.biPlanes = 1; bi.bmiHeader.biBitCount = 32; bi.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HBITMAP dib = CreateDIBSection(hdcScreen, &bi, DIB_RGB_COLORS, &bits, NULL, 0);
    HDC hdcMem = CreateCompatibleDC(hdcScreen);
    HGDIOBJ old = SelectObject(hdcMem, dib);
    BitBlt(hdcMem, 0, 0, w, h, hdcScreen, x, y, SRCCOPY);
    GdiFlush();
    memcpy(px.data(), bits, (size_t)w * h * 4);
    SelectObject(hdcMem, old); DeleteObject(dib); DeleteDC(hdcMem); ReleaseDC(NULL, hdcScreen);
    return true;
}

static void SaveBgra(const std::string& name, const std::vector<BYTE>& px, int w, int h) {
    std::string p = std::string(OUTDIR) + name + ".bgra";
    FILE* f = fopen(p.c_str(), "wb");
    if (!f) { printf("    !! cannot write %s\n", p.c_str()); return; }
    for (int y = 0; y < h; y++) fwrite(px.data() + (size_t)y * w * 4, 1, (size_t)w * 4, f);
    fclose(f);
    printf("    saved %s (%dx%d)\n", p.c_str(), w, h);
}

struct Stats { int distinct = 0; DWORD bg = 0; int bgc = 0; };

static Stats Analyze(const char* tag, const std::vector<BYTE>& px, int w, int h) {
    Stats s;
    std::vector<DWORD> hist(1 << 24, 0);   // 64 MB - fine for a probe
    for (int i = 0; i < w * h; i++) {
        DWORD c = ((DWORD)px[i * 4 + 2] << 16) | ((DWORD)px[i * 4 + 1] << 8) | px[i * 4];
        if (hist[c] == 0) s.distinct++;
        hist[c]++;
    }
    for (size_t i = 0; i < hist.size(); i++) if ((int)hist[i] > s.bgc) { s.bgc = (int)hist[i]; s.bg = (DWORD)i; }
    printf("    [%s] distinctColors=%d dominant=#%06lX %d px (%.1f%%)\n", tag, s.distinct, (unsigned long)s.bg, s.bgc,
        100.0 * s.bgc / (double)(w * h));
    return s;
}

int main() {
    CreateDirectoryA("C:\\ai-projects\\cursor-trap\\probe\\out", NULL);
    printf("=== MagProbe2: MS_* as WINDOW STYLES ===\n");
    printf("MagInitialize -> %d\n", MagInitialize());

    WNDCLASSA wc = {};
    wc.lpfnWndProc = HostProc; wc.hInstance = GetModuleHandleA(NULL);
    wc.lpszClassName = "CursorTrapMagHost2";
    RegisterClassA(&wc);

    CURSORINFO ci = {}; ci.cbSize = sizeof(ci); GetCursorInfo(&ci);
    POINT cp = ci.ptScreenPos;
    HMONITOR hm = MonitorFromPoint(cp, MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi = {}; mi.cbSize = sizeof(mi); GetMonitorInfoA(hm, &mi);

    int hostW = TILE * 3 + GAP * 2, hostH = TILE;
    int srcx = cp.x - TILE / 2, srcy = cp.y - TILE / 2;
    if (srcx < mi.rcMonitor.left) srcx = mi.rcMonitor.left;
    if (srcy < mi.rcMonitor.top) srcy = mi.rcMonitor.top;
    if (srcx + TILE > mi.rcMonitor.right) srcx = mi.rcMonitor.right - TILE;
    if (srcy + TILE > mi.rcMonitor.bottom) srcy = mi.rcMonitor.bottom - TILE;

    int cand[4][2] = {
        { mi.rcMonitor.left, mi.rcMonitor.top },
        { mi.rcMonitor.right - hostW, mi.rcMonitor.top },
        { mi.rcMonitor.left, mi.rcMonitor.bottom - hostH },
        { mi.rcMonitor.right - hostW, mi.rcMonitor.bottom - hostH }
    };
    int hx = cand[0][0], hy = cand[0][1];
    RECT srcrect = { srcx, srcy, srcx + TILE, srcy + TILE };
    for (int i = 0; i < 4; i++) {
        RECT r = { cand[i][0], cand[i][1], cand[i][0] + hostW, cand[i][1] + hostH };
        RECT u = {};
        if (!IntersectRect(&u, &r, &srcrect)) { hx = cand[i][0]; hy = cand[i][1]; break; }
    }
    printf("monitor=(%ld,%ld)-(%ld,%ld) cursor=(%ld,%ld) source=(%ld,%ld,%ld,%ld) host at (%d,%d)\n",
        mi.rcMonitor.left, mi.rcMonitor.top, mi.rcMonitor.right, mi.rcMonitor.bottom, cp.x, cp.y,
        srcrect.left, srcrect.top, srcrect.right, srcrect.bottom, hx, hy);

    g_host = CreateWindowExA(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "CursorTrapMagHost2", "maghost2",
        WS_POPUP | WS_VISIBLE, hx, hy, hostW, hostH, NULL, NULL, wc.hInstance, NULL);
    if (!g_host) { printf("FATAL host\n"); return 1; }

    // A: plain child.  B: MS_SHOWMAGNIFIEDCURSOR style.  C: MS_SHOWMAGNIFIEDCURSOR|MS_CLIPAROUNDCURSOR.
    g_magA = CreateWindowExA(0, WC_MAGNIFIERA, "magA", WS_CHILD | WS_VISIBLE,
        0, 0, TILE, TILE, g_host, NULL, wc.hInstance, NULL);
    g_magB = CreateWindowExA(0, WC_MAGNIFIERA, "magB", WS_CHILD | WS_VISIBLE | MS_SHOWMAGNIFIEDCURSOR,
        TILE + GAP, 0, TILE, TILE, g_host, NULL, wc.hInstance, NULL);
    g_magC = CreateWindowExA(0, WC_MAGNIFIERA, "magC", WS_CHILD | WS_VISIBLE | MS_SHOWMAGNIFIEDCURSOR | MS_CLIPAROUNDCURSOR,
        (TILE + GAP) * 2, 0, TILE, TILE, g_host, NULL, wc.hInstance, NULL);
    printf("magA=%p magB=%p magC=%p err=%lu\n", (void*)g_magA, (void*)g_magB, (void*)g_magC, GetLastError());
    for (const char* n : { "A", "B", "C" }) {
        HWND h = (n[0] == 'A') ? g_magA : (n[0] == 'B' ? g_magB : g_magC);
        DWORD st = (DWORD)GetWindowLongPtrA(h, GWL_STYLE);
        printf("  style[%s]=0x%08lX SHOWMAGNIFIEDCURSOR(0x1)=%d CLIPAROUNDCURSOR(0x2)=%d INVERTCOLORS(0x4)=%d\n",
            n, st, (st & MS_SHOWMAGNIFIEDCURSOR) ? 1 : 0, (st & MS_CLIPAROUNDCURSOR) ? 1 : 0, (st & MS_INVERTCOLORS) ? 1 : 0);
    }

    printf("MagSetImageScalingCallback(B) -> %d\n", MagSetImageScalingCallback(g_magB, MagScalingCB));

    // Exclude every visible top-level window that intersects the source rect.
    std::vector<HWND> excl;
    struct EnumCtx { RECT r; std::vector<HWND>* v; } ec{ srcrect, &excl };
    EnumWindows([](HWND h, LPARAM lp) -> BOOL {
        EnumCtx* c = (EnumCtx*)lp;
        if (!IsWindowVisible(h)) return TRUE;
        RECT wr = {};
        if (!GetWindowRect(h, &wr)) return TRUE;
        RECT u = {};
        if (!IntersectRect(&u, &wr, &c->r)) return TRUE;
        HWND root = GetAncestor(h, GA_ROOT);
        bool dup = false;
        for (HWND e : *c->v) if (e == root) dup = true;
        if (!dup) c->v->push_back(root);
        return TRUE;
    }, (LPARAM)&ec);
    if (excl.empty()) excl.push_back(GetDesktopWindow());
    if (excl.size() > 32) excl.resize(32);
    printf("excluding %zu top-level windows\n", excl.size());
    for (size_t i = 0; i < excl.size(); i++) printf("   excl[%zu]=%p\n", i, (void*)excl[i]);

    for (HWND h : { g_magA, g_magB, g_magC }) {
        printf("  MagSetWindowSource(%p) -> %d\n", (void*)h, MagSetWindowSource(h, srcrect));
        printf("  MagSetWindowFilterList(%p, EXCLUDE, %zu) -> %d\n",
            (void*)h, excl.size(), MagSetWindowFilterList(h, MW_FILTERMODE_EXCLUDE, (int)excl.size(), excl.data()));
    }
    LONG cbBefore = g_cbCount;
    Pump(900);
    printf("scalingCB during pump = %ld\n", g_cbCount - cbBefore);

    // Both halves in one shot: same frame, so the diff is meaningful.
    std::vector<BYTE> full;
    GrabScreen(hx, hy, hostW, hostH, full);
    SaveBgra("mag2_full", full, hostW, hostH);

    auto half = [&](int idx) {
        std::vector<BYTE> out((size_t)TILE * TILE * 4);
        for (int y = 0; y < TILE; y++)
            memcpy(out.data() + (size_t)y * TILE * 4,
                full.data() + ((size_t)y * hostW + idx * (TILE + GAP)) * 4, (size_t)TILE * 4);
        return out;
    };
    std::vector<BYTE> A = half(0), B = half(1), C = half(2);
    SaveBgra("mag2_A_plain", A, TILE, TILE);
    SaveBgra("mag2_B_showcursor", B, TILE, TILE);
    SaveBgra("mag2_C_showcursor_clip", C, TILE, TILE);
    Analyze("A plain", A, TILE, TILE);
    Analyze("B showcursor", B, TILE, TILE);
    Analyze("C showcursor+clip", C, TILE, TILE);

    // Diff B vs A: pixels the style bit added.
    int minx = TILE, miny = TILE, maxx = -1, maxy = -1, diffCount = 0;
    for (int y = 0; y < TILE; y++) for (int x = 0; x < TILE; x++) {
        size_t o = ((size_t)y * TILE + x) * 4;
        if (memcmp(B.data() + o, A.data() + o, 3) != 0) {
            diffCount++;
            if (x < minx) minx = x; if (x > maxx) maxx = x;
            if (y < miny) miny = y; if (y > maxy) maxy = y;
        }
    }
    printf("  B-vs-A diff pixels=%d bbox=", diffCount);
    if (maxx < 0) printf("<none>\n");
    else printf("(%d,%d)-(%d,%d) => %dx%d\n", minx, miny, maxx + 1, maxy + 1, maxx + 1 - minx, maxy + 1 - miny);

    // Same for C vs A.
    minx = miny = TILE; maxx = maxy = -1; diffCount = 0;
    for (int y = 0; y < TILE; y++) for (int x = 0; x < TILE; x++) {
        size_t o = ((size_t)y * TILE + x) * 4;
        if (memcmp(C.data() + o, A.data() + o, 3) != 0) {
            diffCount++;
            if (x < minx) minx = x; if (x > maxx) maxx = x;
            if (y < miny) miny = y; if (y > maxy) maxy = y;
        }
    }
    printf("  C-vs-A diff pixels=%d bbox=", diffCount);
    if (maxx < 0) printf("<none>\n");
    else printf("(%d,%d)-(%d,%d) => %dx%d\n", minx, miny, maxx + 1, maxy + 1, maxx + 1 - minx, maxy + 1 - miny);

    printf("cursor pos=(%ld,%ld) expected cursor origin in tile=(%ld,%ld)\n", cp.x, cp.y, cp.x - srcrect.left, cp.y - srcrect.top);
    printf("total scalingCB = %ld\n=== done ===\n", g_cbCount);
    return 0;
}
