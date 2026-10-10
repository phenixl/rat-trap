// mag_probe.cpp - can the Magnification API be used to observe/isolate the REAL cursor?
// Experiment matrix: filter-list variants x readback methods, plus the deprecated
// MagSetImageScalingCallback path.
#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <magnification.h>
#include <stdio.h>
#include <string.h>
#include <vector>
#include <string>
#include <map>

#pragma comment(lib, "Magnification.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")

static const int MAGW = 256, MAGH = 256;
static const char* OUTDIR = "C:\\ai-projects\\cursor-trap\\probe\\out\\";
static HWND g_host = nullptr, g_mag = nullptr;
static std::string g_variant = "init";
static volatile LONG g_cbCount = 0;

static void GuidStr(const GUID& g, char* buf, size_t n) {
    sprintf_s(buf, n, "%08lX-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X",
        (unsigned long)g.Data1, g.Data2, g.Data3,
        g.Data4[0], g.Data4[1], g.Data4[2], g.Data4[3], g.Data4[4], g.Data4[5], g.Data4[6], g.Data4[7]);
}

static BOOL CALLBACK MagScalingCB(HWND hwnd, void* srcdata, MAGIMAGEHEADER src,
    void* destdata, MAGIMAGEHEADER dst, RECT unclipped, RECT clipped, HRGN dirty) {
    LONG n = InterlockedIncrement(&g_cbCount);
    char gs[64] = { 0 }, gd[64] = { 0 };
    GuidStr(src.format, gs, sizeof(gs));
    GuidStr(dst.format, gd, sizeof(gd));
    printf("    [scalingCB #%ld] variant=%s\n", n, g_variant.c_str());
    printf("        src  %ux%u stride=%u off=%u cb=%zu fmt=%s\n", src.width, src.height, src.stride, src.offset, (size_t)src.cbSize, gs);
    printf("        dest %ux%u stride=%u off=%u cb=%zu fmt=%s\n", dst.width, dst.height, dst.stride, dst.offset, (size_t)dst.cbSize, gd);
    printf("        unclipped=(%ld,%ld,%ld,%ld) clipped=(%ld,%ld,%ld,%ld) srcdata=%p destdata=%p\n",
        unclipped.left, unclipped.top, unclipped.right, unclipped.bottom,
        clipped.left, clipped.top, clipped.right, clipped.bottom, srcdata, destdata);
    if (destdata && n <= 2 && dst.width && dst.height) {
        char name[512];
        sprintf_s(name, "%scb_%s_%ld.bgra", OUTDIR, g_variant.c_str(), n);
        FILE* f = fopen(name, "wb");
        if (f) {
            size_t row = dst.stride ? dst.stride : (size_t)dst.width * 4;
            for (UINT y = 0; y < dst.height; y++)
                fwrite((const BYTE*)destdata + dst.offset + (size_t)y * row, 1, (size_t)dst.width * 4, f);
            fclose(f);
            printf("        dumped %s (%ux%u)\n", name, dst.width, dst.height);
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

struct Img { int w = 0, h = 0; std::vector<BYTE> px; };

static bool GrabScreen(int x, int y, int w, int h, Img& out) {
    out.w = w; out.h = h;
    out.px.assign((size_t)w * h * 4, 0);
    HDC hdcScreen = GetDC(NULL);
    if (!hdcScreen) return false;
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
    memcpy(out.px.data(), bits, (size_t)w * h * 4);
    SelectObject(hdcMem, old);
    DeleteObject(dib);
    DeleteDC(hdcMem);
    ReleaseDC(NULL, hdcScreen);
    return true;
}

static bool GrabPrintWindow(HWND hwnd, int w, int h, Img& out) {
    out.w = w; out.h = h;
    out.px.assign((size_t)w * h * 4, 0);
    HDC hdcScreen = GetDC(NULL);
    BITMAPINFO bi = {};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = w; bi.bmiHeader.biHeight = -h;
    bi.bmiHeader.biPlanes = 1; bi.bmiHeader.biBitCount = 32; bi.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HBITMAP dib = CreateDIBSection(hdcScreen, &bi, DIB_RGB_COLORS, &bits, NULL, 0);
    HDC hdcMem = CreateCompatibleDC(hdcScreen);
    HGDIOBJ old = SelectObject(hdcMem, dib);
    BOOL ok = PrintWindow(hwnd, hdcMem, 2 /*PW_RENDERFULLCONTENT*/);
    GdiFlush();
    memcpy(out.px.data(), bits, (size_t)w * h * 4);
    SelectObject(hdcMem, old);
    DeleteObject(dib);
    DeleteDC(hdcMem);
    ReleaseDC(NULL, hdcScreen);
    printf("      PrintWindow(PW_RENDERFULLCONTENT) ok=%d\n", ok);
    return ok != 0;
}

static void Save(const std::string& name, const Img& im) {
    std::string p = std::string(OUTDIR) + name + ".bgra";
    FILE* f = fopen(p.c_str(), "wb");
    if (!f) { printf("      !! cannot write %s\n", p.c_str()); return; }
    for (int y = 0; y < im.h; y++) fwrite(im.px.data() + (size_t)y * im.w * 4, 1, (size_t)im.w * 4, f);
    fclose(f);
    printf("      saved %s (%dx%d)\n", p.c_str(), im.w, im.h);
}

// Report the bounding box of pixels that differ from the most common (background) color.
static void Analyze(const char* tag, const Img& im) {
    std::map<DWORD, int> hist;
    for (size_t i = 0; i < (size_t)im.w * im.h; i++) {
        DWORD c = ((DWORD)im.px[i * 4 + 2] << 16) | ((DWORD)im.px[i * 4 + 1] << 8) | im.px[i * 4];
        hist[c]++;
    }
    DWORD bg = 0; int bgc = -1; int distinct = 0;
    for (auto& kv : hist) { if (kv.second > bgc) { bgc = kv.second; bg = kv.first; } }
    distinct = (int)hist.size();
    int minx = im.w, miny = im.h, maxx = -1, maxy = -1;
    for (int y = 0; y < im.h; y++) for (int x = 0; x < im.w; x++) {
        size_t i = (size_t)y * im.w + x;
        DWORD c = ((DWORD)im.px[i * 4 + 2] << 16) | ((DWORD)im.px[i * 4 + 1] << 8) | im.px[i * 4];
        if (c != bg) { if (x < minx) minx = x; if (x > maxx) maxx = x; if (y < miny) miny = y; if (y > maxy) maxy = y; }
    }
    printf("      [%s] distinctColors=%d dominant=#%06lX (%d px = %.1f%%)  nonBgBox=",
        tag, distinct, (unsigned long)bg, bgc, 100.0 * bgc / ((double)im.w * im.h));
    if (maxx < 0) printf("<empty>\n");
    else printf("(%d,%d)-(%d,%d) => %dx%d\n", minx, miny, maxx + 1, maxy + 1, maxx + 1 - minx, maxy + 1 - miny);
}

static std::vector<HWND> g_excl;

static void ApplyVariant(const char* name, DWORD mode, int count, const HWND* wins) {
    g_variant = name;
    printf("  === variant %s : mode=0x%lX (%s%s%s) count=%d\n", name, mode,
        (mode & 1) ? "INCLUDE|SHOWMAGNIFIEDCURSOR" : "EXCLUDE",
        (mode & 2) ? "|CLIPAROUNDCURSOR" : "", (mode & 4) ? "|INVERTCOLORS" : "", count);
    BOOL ok = MagSetWindowFilterList(g_mag, mode, count, const_cast<HWND*>(wins));
    printf("      MagSetWindowFilterList -> %d gle=%lu\n", ok, ok ? 0 : GetLastError());
    DWORD gotMode = 0xDEADBEEF; HWND gotWins[8] = {}; 
    int n = MagGetWindowFilterList(g_mag, &gotMode, 8, gotWins);
    printf("      MagGetWindowFilterList -> count=%d mode=0x%lX", n, gotMode);
    for (int i = 0; i < n && i < 8; i++) printf(" hwnd[%d]=%p", i, (void*)gotWins[i]);
    printf("\n");

    // Re-center source rect on the live cursor so the magnified cursor is always inside.
    CURSORINFO ci = {}; ci.cbSize = sizeof(ci);
    GetCursorInfo(&ci);
    POINT p = ci.ptScreenPos;
    HMONITOR hm = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi = {}; mi.cbSize = sizeof(mi); GetMonitorInfoA(hm, &mi);
    int sx = p.x - MAGW / 2, sy = p.y - MAGH / 2;
    if (sx < mi.rcMonitor.left) sx = mi.rcMonitor.left;
    if (sy < mi.rcMonitor.top) sy = mi.rcMonitor.top;
    if (sx + MAGW > mi.rcMonitor.right) sx = mi.rcMonitor.right - MAGW;
    if (sy + MAGH > mi.rcMonitor.bottom) sy = mi.rcMonitor.bottom - MAGH;
    RECT src = { sx, sy, sx + MAGW, sy + MAGH };
    BOOL okSrc = MagSetWindowSource(g_mag, src);
    RECT back = {}; MagGetWindowSource(g_mag, &back);
    printf("      cursor=(%ld,%ld) MagSetWindowSource=(%ld,%ld,%ld,%ld) -> %d ; MagGetWindowSource=(%ld,%ld,%ld,%ld)\n",
        p.x, p.y, src.left, src.top, src.right, src.bottom, okSrc, back.left, back.top, back.right, back.bottom);
    printf("      cursor offset inside mag client = (%ld,%ld)\n", p.x - back.left, p.y - back.top);

    LONG before = g_cbCount;
    Pump(500);
    printf("      scalingCB invocations during this variant = %ld\n", g_cbCount - before);

    RECT hr = {}; GetWindowRect(g_host, &hr);
    Img im;
    if (GrabScreen(hr.left, hr.top, MAGW, MAGH, im)) {
        Analyze("screenBitBlt", im);
        Save(std::string("mag_") + name + "_screen", im);
    }
    Img im2;
    if (GrabPrintWindow(g_host, MAGW, MAGH, im2)) {
        Analyze("printWindow", im2);
        Save(std::string("mag_") + name + "_pw", im2);
    }
}

int main() {
    CreateDirectoryA("C:\\ai-projects\\cursor-trap\\probe\\out", NULL);
    printf("=== MagProbe: can Magnification API isolate the real cursor? ===\n");
    printf("MagInitialize -> %d\n", MagInitialize());

    WNDCLASSA wc = {};
    wc.lpfnWndProc = HostProc; wc.hInstance = GetModuleHandleA(NULL);
    wc.lpszClassName = "CursorTrapMagHost"; wc.hCursor = NULL;
    RegisterClassA(&wc);

    CURSORINFO ci = {}; ci.cbSize = sizeof(ci); GetCursorInfo(&ci);
    POINT cp = ci.ptScreenPos;
    HMONITOR hm = MonitorFromPoint(cp, MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi = {}; mi.cbSize = sizeof(mi); GetMonitorInfoA(hm, &mi);

    // Place the host so that it does NOT overlap the source rect we will magnify.
    int srcx = cp.x - MAGW / 2, srcy = cp.y - MAGH / 2;
    int cand[4][2] = {
        { mi.rcMonitor.left, mi.rcMonitor.top },
        { mi.rcMonitor.right - MAGW, mi.rcMonitor.top },
        { mi.rcMonitor.left, mi.rcMonitor.bottom - MAGH },
        { mi.rcMonitor.right - MAGW, mi.rcMonitor.bottom - MAGH }
    };
    int hx = cand[0][0], hy = cand[0][1];
    for (int i = 0; i < 4; i++) {
        RECT r = { cand[i][0], cand[i][1], cand[i][0] + MAGW, cand[i][1] + MAGH };
        RECT s = { srcx, srcy, srcx + MAGW, srcy + MAGH };
        RECT u = {};
        if (!IntersectRect(&u, &r, &s)) { hx = cand[i][0]; hy = cand[i][1]; break; }
    }
    printf("monitor=(%ld,%ld)-(%ld,%ld) cursor=(%ld,%ld) host at (%d,%d)\n",
        mi.rcMonitor.left, mi.rcMonitor.top, mi.rcMonitor.right, mi.rcMonitor.bottom, cp.x, cp.y, hx, hy);

    g_host = CreateWindowExA(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "CursorTrapMagHost", "maghost",
        WS_POPUP | WS_VISIBLE, hx, hy, MAGW, MAGH, NULL, NULL, wc.hInstance, NULL);
    printf("host=%p err=%lu\n", (void*)g_host, GetLastError());
    g_mag = CreateWindowExA(0, WC_MAGNIFIERA, "mag", WS_CHILD | WS_VISIBLE, 0, 0, MAGW, MAGH, g_host, NULL, wc.hInstance, NULL);
    printf("mag=%p err=%lu\n", (void*)g_mag, GetLastError());
    if (!g_mag) { printf("FATAL: magnifier window creation failed\n"); return 1; }

    printf("MagSetImageScalingCallback -> %d (gle=%lu)\n", MagSetImageScalingCallback(g_mag, MagScalingCB), GetLastError());
    Pump(200);

    HWND under = WindowFromPoint(cp);
    HWND underRoot = under ? GetAncestor(under, GA_ROOT) : NULL;
    printf("window under cursor: %p root=%p desktop=%p host=%p\n", (void*)under, (void*)underRoot, (void*)GetDesktopWindow(), (void*)g_host);

    // Variant matrix
    HWND w1[1] = { underRoot };
    HWND w2[1] = { GetDesktopWindow() };
    HWND w3[1] = { g_host };
    ApplyVariant("A_nofilter", MW_FILTERMODE_EXCLUDE, 0, nullptr);
    ApplyVariant("B_excl_under_showm agcursor", MW_FILTERMODE_EXCLUDE | MS_SHOWMAGNIFIEDCURSOR, 1, w1);
    ApplyVariant("C_excl_under_plain", MW_FILTERMODE_EXCLUDE, 1, w1);
    ApplyVariant("D_excl_desktop_showmagcursor", MW_FILTERMODE_EXCLUDE | MS_SHOWMAGNIFIEDCURSOR, 1, w2);
    ApplyVariant("E_excl_under_showmag_clip", MW_FILTERMODE_EXCLUDE | MS_SHOWMAGNIFIEDCURSOR | MS_CLIPAROUNDCURSOR, 1, w1);
    ApplyVariant("F_incl_under", MW_FILTERMODE_INCLUDE, 1, w1);
    ApplyVariant("G_excl_host_showmagcursor", MW_FILTERMODE_EXCLUDE | MS_SHOWMAGNIFIEDCURSOR, 1, w3);

    printf("\nMagShowSystemCursor(FALSE) -> %d gle=%lu\n", MagShowSystemCursor(FALSE), GetLastError());
    Pump(300);
    printf("MagShowSystemCursor(TRUE) -> %d gle=%lu\n", MagShowSystemCursor(TRUE), GetLastError());

    printf("\ntotal scalingCB invocations = %ld\n", g_cbCount);
    printf("=== done ===\n");
    return 0;
}
