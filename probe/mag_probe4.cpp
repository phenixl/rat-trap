// mag_probe4.cpp - decisive fidelity experiment.
// ONE magnifier window (multi-window rendering was unreliable), no filter list.
// magOutput (magnifier's rendering of the source rect)  vs  screenBitBlt (the same rect
// read straight from the screen DC).  A screen BitBlt does NOT contain the cursor, and the
// magnifier DOES draw the magnified cursor, so the difference is exactly the cursor the
// system renders - at its true size and scale.
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

static const int TILE = 256;
static const char* OUTDIR = "C:\\ai-projects\\cursor-trap\\probe\\out\\";
static HWND g_host = nullptr, g_mag = nullptr;

static LRESULT CALLBACK HostProc(HWND h, UINT m, WPARAM w, LPARAM l) { return DefWindowProcA(h, m, w, l); }

static void Pump(int ms) {
    DWORD end = GetTickCount() + ms; MSG msg;
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
    FILE* f = fopen((std::string(OUTDIR) + name + ".bgra").c_str(), "wb");
    if (!f) { printf("  !! cannot write %s\n", name.c_str()); return; }
    for (int y = 0; y < h; y++) fwrite(px.data() + (size_t)y * w * 4, 1, (size_t)w * 4, f);
    fclose(f);
    printf("  saved %s (%dx%d)\n", name.c_str(), w, h);
}

static bool RenderCursor(HCURSOR hc, int w, int h, std::vector<BYTE>& out) {
    out.assign((size_t)w * h * 4, 0);
    HDC hdc = CreateCompatibleDC(NULL);
    BITMAPINFO bi = {};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = w; bi.bmiHeader.biHeight = -h;
    bi.bmiHeader.biPlanes = 1; bi.bmiHeader.biBitCount = 32; bi.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HBITMAP dib = CreateDIBSection(hdc, &bi, DIB_RGB_COLORS, &bits, NULL, 0);
    HGDIOBJ old = SelectObject(hdc, dib);
    memset(bits, 0, (size_t)w * h * 4);
    BOOL ok = DrawIconEx(hdc, 0, 0, hc, w, h, 0, NULL, DI_NORMAL);
    GdiFlush();
    if (ok) memcpy(out.data(), bits, (size_t)w * h * 4);
    SelectObject(hdc, old); DeleteObject(dib); DeleteDC(hdc);
    return ok != 0;
}

int main() {
    CreateDirectoryA("C:\\ai-projects\\cursor-trap\\probe\\out", NULL);
    printf("=== MagProbe4: magnifier output minus screen BitBlt = the real cursor ===\n");
    printf("MagInitialize -> %d\n", MagInitialize());

    WNDCLASSA wc = {};
    wc.lpfnWndProc = HostProc; wc.hInstance = GetModuleHandleA(NULL); wc.lpszClassName = "CursorTrapMagHost4";
    RegisterClassA(&wc);

    CURSORINFO ci = {}; ci.cbSize = sizeof(ci); GetCursorInfo(&ci);
    POINT cp = ci.ptScreenPos;
    ICONINFO li = {}; GetIconInfo(ci.hCursor, &li);
    printf("cursor at (%ld,%ld) hCursor=%p hotspot=(%ld,%ld)\n", cp.x, cp.y, (void*)ci.hCursor, li.xHotspot, li.yHotspot);
    if (li.hbmColor) DeleteObject(li.hbmColor);
    if (li.hbmMask) DeleteObject(li.hbmMask);

    // Reference renderings for later numeric comparison.
    std::vector<BYTE> t;
    if (RenderCursor(ci.hCursor, 32, 32, t)) SaveBgra("p4_cand_live32", t, 32, 32);
    if (RenderCursor(ci.hCursor, 80, 80, t)) SaveBgra("p4_cand_live_to_80", t, 80, 80);
    if (RenderCursor(ci.hCursor, 96, 96, t)) SaveBgra("p4_cand_live_to_96", t, 96, 96);
    HCURSOR big = (HCURSOR)LoadImageA(NULL, "C:\\WINDOWS\\cursors\\aero_link.cur", IMAGE_CURSOR, 0, 0, LR_LOADFROMFILE);
    if (big) {
        ICONINFO bi2 = {}; GetIconInfo(big, &bi2);
        BITMAP bm = {}; if (bi2.hbmColor) GetObject(bi2.hbmColor, sizeof(bm), &bm);
        printf("largest aero_link entry: %ldx%ld hotspot=(%ld,%ld)\n", bm.bmWidth, bm.bmHeight, bi2.xHotspot, bi2.yHotspot);
        if (RenderCursor(big, (int)bm.bmWidth, (int)bm.bmHeight, t)) SaveBgra("p4_cand_big_native", t, (int)bm.bmWidth, (int)bm.bmHeight);
        if (RenderCursor(big, 80, 80, t)) SaveBgra("p4_cand_big_to_80", t, 80, 80);
        if (bi2.hbmColor) DeleteObject(bi2.hbmColor);
        if (bi2.hbmMask) DeleteObject(bi2.hbmMask);
        DestroyCursor(big);
    }

    HMONITOR hm = MonitorFromPoint(cp, MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi = {}; mi.cbSize = sizeof(mi); GetMonitorInfoA(hm, &mi);

    int srcx = cp.x - TILE / 2, srcy = cp.y - TILE / 2;
    if (srcx < mi.rcMonitor.left) srcx = mi.rcMonitor.left;
    if (srcy < mi.rcMonitor.top) srcy = mi.rcMonitor.top;
    if (srcx + TILE > mi.rcMonitor.right) srcx = mi.rcMonitor.right - TILE;
    if (srcy + TILE > mi.rcMonitor.bottom) srcy = mi.rcMonitor.bottom - TILE;
    RECT srcrect = { srcx, srcy, srcx + TILE, srcy + TILE };

    int cand[4][2] = { { mi.rcMonitor.left, mi.rcMonitor.top }, { mi.rcMonitor.right - TILE, mi.rcMonitor.top },
                       { mi.rcMonitor.left, mi.rcMonitor.bottom - TILE }, { mi.rcMonitor.right - TILE, mi.rcMonitor.bottom - TILE } };
    int hx = cand[0][0], hy = cand[0][1];
    for (int i = 0; i < 4; i++) {
        RECT r = { cand[i][0], cand[i][1], cand[i][0] + TILE, cand[i][1] + TILE }, u = {};
        if (!IntersectRect(&u, &r, &srcrect)) { hx = cand[i][0]; hy = cand[i][1]; break; }
    }
    printf("monitor=(%ld,%ld)-(%ld,%ld) source=(%ld,%ld,%ld,%ld) host=(%d,%d)\n",
        mi.rcMonitor.left, mi.rcMonitor.top, mi.rcMonitor.right, mi.rcMonitor.bottom,
        srcrect.left, srcrect.top, srcrect.right, srcrect.bottom, hx, hy);

    g_host = CreateWindowExA(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "CursorTrapMagHost4", "maghost4",
        WS_POPUP | WS_VISIBLE, hx, hy, TILE, TILE, NULL, NULL, wc.hInstance, NULL);
    g_mag = CreateWindowExA(0, WC_MAGNIFIERA, "mag", WS_CHILD | WS_VISIBLE | MS_SHOWMAGNIFIEDCURSOR,
        0, 0, TILE, TILE, g_host, NULL, wc.hInstance, NULL);
    printf("host=%p mag=%p err=%lu style=0x%08lX\n", (void*)g_host, (void*)g_mag, GetLastError(),
        g_mag ? (DWORD)GetWindowLongPtrA(g_mag, GWL_STYLE) : 0);
    if (!g_host || !g_mag) { printf("FATAL\n"); return 1; }
    RECT mr = {}; GetWindowRect(g_mag, &mr);
    printf("mag window rect=(%ld,%ld)-(%ld,%ld)\n", mr.left, mr.top, mr.right, mr.bottom);

    printf("MagSetWindowSource -> %d\n", MagSetWindowSource(g_mag, srcrect));
    Pump(1500);

    for (int pass = 0; pass < 3; pass++) {
        std::vector<BYTE> magOut, srcOut;
        GrabScreen(hx, hy, TILE, TILE, magOut);            // what the magnifier draws
        GrabScreen(srcrect.left, srcrect.top, TILE, TILE, srcOut);  // the real screen (no cursor)
        char nm[64]; sprintf_s(nm, "p4_magOut%d", pass); SaveBgra(nm, magOut, TILE, TILE);
        if (pass == 0) SaveBgra("p4_srcActual", srcOut, TILE, TILE);

        int minx = TILE, miny = TILE, maxx = -1, maxy = -1, n = 0;
        for (int y = 0; y < TILE; y++) for (int x = 0; x < TILE; x++) {
            size_t o = ((size_t)y * TILE + x) * 4;
            if (memcmp(magOut.data() + o, srcOut.data() + o, 3) != 0) {
                n++;
                if (x < minx) minx = x; if (x > maxx) maxx = x;
                if (y < miny) miny = y; if (y > maxy) maxy = y;
            }
        }
        printf("[pass%d] magOut-vs-screen diff pixels=%d bbox=", pass, n);
        if (maxx < 0) { printf("<none - magnifier draws no cursor>\n"); continue; }
        printf("(%d,%d)-(%d,%d) => %dx%d\n", minx, miny, maxx + 1, maxy + 1, maxx + 1 - minx, maxy + 1 - miny);
        printf("        cursor position in tile = (%ld,%ld); diff origin relative to cursor = (%d,%d)\n",
            cp.x - srcrect.left, cp.y - srcrect.top, minx - (int)(cp.x - srcrect.left), miny - (int)(cp.y - srcrect.top));
        if (pass == 0) {
            int pad = 16;
            int x0 = max(0, minx - pad), y0 = max(0, miny - pad);
            int x1 = min(TILE, maxx + 1 + pad), y1 = min(TILE, maxy + 1 + pad);
            int pw = x1 - x0, ph = y1 - y0;
            std::vector<BYTE> patch((size_t)pw * ph * 4), patchSrc((size_t)pw * ph * 4);
            for (int y = 0; y < ph; y++) {
                memcpy(patch.data() + (size_t)y * pw * 4, magOut.data() + ((size_t)(y0 + y) * TILE + x0) * 4, (size_t)pw * 4);
                memcpy(patchSrc.data() + (size_t)y * pw * 4, srcOut.data() + ((size_t)(y0 + y) * TILE + x0) * 4, (size_t)pw * 4);
            }
            char pn[128];
            sprintf_s(pn, "p4_cursorPatch_x%d_y%d", x0, y0); SaveBgra(pn, patch, pw, ph);
            sprintf_s(pn, "p4_underPatch_x%d_y%d", x0, y0); SaveBgra(pn, patchSrc, pw, ph);
            printf("        patch origin in tile = (%d,%d)\n", x0, y0);
        }
        Pump(600);
    }

    printf("MagShowSystemCursor(FALSE) -> %d\n", MagShowSystemCursor(FALSE));
    Pump(600);
    { std::vector<BYTE> a, b; GrabScreen(hx, hy, TILE, TILE, a); GrabScreen(srcrect.left, srcrect.top, TILE, TILE, b);
      int n = 0; for (size_t i = 0; i < a.size(); i += 4) if (memcmp(a.data() + i, b.data() + i, 3)) n++;
      printf("after MagShowSystemCursor(FALSE): magnifier-vs-screen diff pixels=%d\n", n);
      SaveBgra("p4_after_hide", a, TILE, TILE); }
    printf("MagShowSystemCursor(TRUE) -> %d\n=== done ===\n", MagShowSystemCursor(TRUE));
    return 0;
}
