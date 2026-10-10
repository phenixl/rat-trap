// mag_probe3.cpp - cleanest isolation of the system-rendered cursor.
// No filter list, no scaling callback. Three magnifier tiles with IDENTICAL source rect;
// the only difference is the window style bit. Same-frame screen grab, then diff.
// Also fingerprints the live cursor against the 15 HKCU theme cursor entries.
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
#pragma comment(lib, "advapi32.lib")

static const int TILE = 192;
static const int GAP = 8;
static const char* OUTDIR = "C:\\ai-projects\\cursor-trap\\probe\\out\\";
static HWND g_host = nullptr, g_magA = nullptr, g_magB = nullptr, g_magC = nullptr;

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
    FILE* f = fopen((std::string(OUTDIR) + name + ".bgra").c_str(), "wb");
    if (!f) return;
    for (int y = 0; y < h; y++) fwrite(px.data() + (size_t)y * w * 4, 1, (size_t)w * 4, f);
    fclose(f);
    printf("    saved %s (%dx%d)\n", name.c_str(), w, h);
}

// Render an HCURSOR into a 32bpp DIB via DrawIconEx (system scaling) and return pixels.
static bool RenderCursor(HCURSOR hc, int w, int h, std::vector<BYTE>& out) {
    out.assign((size_t)w * h * 4, 0);
    ICONINFO ii = {};
    if (!GetIconInfo(hc, &ii)) return false;
    if (ii.hbmColor) DeleteObject(ii.hbmColor);
    if (ii.hbmMask) DeleteObject(ii.hbmMask);
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

// FNV-1a over the RGB+alpha of a rendered cursor, ignoring fully transparent pixels.
static unsigned long long Fingerprint(const std::vector<BYTE>& px) {
    unsigned long long f = 1469598103934665603ULL;
    for (size_t i = 0; i < px.size(); i += 4) {
        if (px[i + 3] == 0) continue;              // transparent -> ignore
        for (int k = 0; k < 4; k++) { f ^= px[i + k]; f *= 1099511628211ULL; }
    }
    return f;
}

static const char* VALNAMES[] = { "Arrow","Help","AppStarting","Wait","Crosshair","IBeam","NWPen","No",
    "SizeNS","SizeWE","SizeNWSE","SizeNESW","SizeAll","UpArrow","Hand","Pin","Person" };

static void IdentifyCursor(HCURSOR live) {
    printf("\n--- cursor identity fingerprint ---\n");
    std::vector<BYTE> livePx;
    RenderCursor(live, 32, 32, livePx);
    unsigned long long lf = Fingerprint(livePx);
    ICONINFO li = {}; GetIconInfo(live, &li);
    printf("  live cursor hotspot=(%ld,%ld) fingerprint=%016llX\n", li.xHotspot, li.yHotspot, lf);
    if (li.hbmColor) DeleteObject(li.hbmColor);
    if (li.hbmMask) DeleteObject(li.hbmMask);
    SaveBgra("id_live_32", livePx, 32, 32);

    HKEY k = nullptr;
    if (RegOpenKeyExA(HKEY_CURRENT_USER, "Control Panel\\Cursors", 0, KEY_READ, &k) != ERROR_SUCCESS) {
        printf("  cannot open HKCU\\Control Panel\\Cursors\n"); return;
    }
    for (const char* name : VALNAMES) {
        char path[MAX_PATH] = { 0 }; DWORD cb = sizeof(path) - 1, type = 0;
        if (RegQueryValueExA(k, name, NULL, &type, (LPBYTE)path, &cb) != ERROR_SUCCESS || !path[0]) continue;
        // expand REG_EXPAND_SZ
        char exp[MAX_PATH] = { 0 };
        ExpandEnvironmentStringsA(path, exp, MAX_PATH);
        HCURSOR hc = (HCURSOR)LoadImageA(NULL, exp, IMAGE_CURSOR, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
        if (!hc) { printf("  %-12s %s  <load failed gle=%lu>\n", name, exp, GetLastError()); continue; }
        std::vector<BYTE> px;
        RenderCursor(hc, 32, 32, px);
        unsigned long long f = Fingerprint(px);
        ICONINFO ii = {}; GetIconInfo(hc, &ii);
        printf("  %-12s hs=(%ld,%ld) fp=%016llX %s  %s\n", name, ii.xHotspot, ii.yHotspot, f,
            (f == lf) ? "  <== MATCH" : "", exp);
        if (ii.hbmColor) DeleteObject(ii.hbmColor);
        if (ii.hbmMask) DeleteObject(ii.hbmMask);
        DestroyCursor(hc);
    }
    RegCloseKey(k);
}

int main() {
    CreateDirectoryA("C:\\ai-projects\\cursor-trap\\probe\\out", NULL);
    printf("=== MagProbe3: isolate the system-rendered cursor by style-bit diff ===\n");
    printf("MagInitialize -> %d\n", MagInitialize());

    WNDCLASSA wc = {};
    wc.lpfnWndProc = HostProc; wc.hInstance = GetModuleHandleA(NULL);
    wc.lpszClassName = "CursorTrapMagHost3";
    RegisterClassA(&wc);

    CURSORINFO ci = {}; ci.cbSize = sizeof(ci); GetCursorInfo(&ci);
    POINT cp = ci.ptScreenPos;
    printf("cursor at (%ld,%ld) flags=0x%lX\n", cp.x, cp.y, ci.flags);
    if (ci.hCursor) IdentifyCursor(ci.hCursor);

    HMONITOR hm = MonitorFromPoint(cp, MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi = {}; mi.cbSize = sizeof(mi); GetMonitorInfoA(hm, &mi);
    UINT dpiX = 96, dpiY = 96;
    {
        HMODULE sh = LoadLibraryA("shcore.dll");
        if (sh) { typedef HRESULT(WINAPI* PFN)(HMONITOR, int, UINT*, UINT*); PFN f = (PFN)GetProcAddress(sh, "GetDpiForMonitor"); if (f) f(hm, 0, &dpiX, &dpiY); }
    }
    printf("monitor=(%ld,%ld)-(%ld,%ld) dpi=%ux%u\n", mi.rcMonitor.left, mi.rcMonitor.top, mi.rcMonitor.right, mi.rcMonitor.bottom, dpiX, dpiY);

    int hostW = TILE * 3 + GAP * 2, hostH = TILE;
    int srcx = cp.x - TILE / 2, srcy = cp.y - TILE / 2;
    if (srcx < mi.rcMonitor.left) srcx = mi.rcMonitor.left;
    if (srcy < mi.rcMonitor.top) srcy = mi.rcMonitor.top;
    if (srcx + TILE > mi.rcMonitor.right) srcx = mi.rcMonitor.right - TILE;
    if (srcy + TILE > mi.rcMonitor.bottom) srcy = mi.rcMonitor.bottom - TILE;
    RECT srcrect = { srcx, srcy, srcx + TILE, srcy + TILE };

    int cand[4][2] = { { mi.rcMonitor.left, mi.rcMonitor.top }, { mi.rcMonitor.right - hostW, mi.rcMonitor.top },
                       { mi.rcMonitor.left, mi.rcMonitor.bottom - hostH }, { mi.rcMonitor.right - hostW, mi.rcMonitor.bottom - hostH } };
    int hx = cand[0][0], hy = cand[0][1];
    for (int i = 0; i < 4; i++) {
        RECT r = { cand[i][0], cand[i][1], cand[i][0] + hostW, cand[i][1] + hostH }, u = {};
        if (!IntersectRect(&u, &r, &srcrect)) { hx = cand[i][0]; hy = cand[i][1]; break; }
    }
    printf("source=(%ld,%ld,%ld,%ld) host=(%d,%d) size=%dx%d\n", srcrect.left, srcrect.top, srcrect.right, srcrect.bottom, hx, hy, hostW, hostH);

    g_host = CreateWindowExA(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "CursorTrapMagHost3", "maghost3",
        WS_POPUP | WS_VISIBLE, hx, hy, hostW, hostH, NULL, NULL, wc.hInstance, NULL);
    g_magA = CreateWindowExA(0, WC_MAGNIFIERA, "A", WS_CHILD | WS_VISIBLE, 0, 0, TILE, TILE, g_host, NULL, wc.hInstance, NULL);
    g_magB = CreateWindowExA(0, WC_MAGNIFIERA, "B", WS_CHILD | WS_VISIBLE | MS_SHOWMAGNIFIEDCURSOR,
        TILE + GAP, 0, TILE, TILE, g_host, NULL, wc.hInstance, NULL);
    g_magC = CreateWindowExA(0, WC_MAGNIFIERA, "C", WS_CHILD | WS_VISIBLE | MS_SHOWMAGNIFIEDCURSOR | MS_CLIPAROUNDCURSOR,
        (TILE + GAP) * 2, 0, TILE, TILE, g_host, NULL, wc.hInstance, NULL);
    printf("host=%p A=%p B=%p C=%p\n", (void*)g_host, (void*)g_magA, (void*)g_magB, (void*)g_magC);
    if (!g_host || !g_magA || !g_magB || !g_magC) { printf("FATAL window creation\n"); return 1; }

    for (HWND h : { g_magA, g_magB, g_magC })
        printf("  MagSetWindowSource(%p) -> %d gle=%lu\n", (void*)h, MagSetWindowSource(h, srcrect), GetLastError());

    Pump(1200);
    for (int pass = 0; pass < 3; pass++) {
        std::vector<BYTE> full;
        GrabScreen(hx, hy, hostW, hostH, full);
        char nm[64]; sprintf_s(nm, "mag3_pass%d", pass);
        SaveBgra(nm, full, hostW, hostH);
        // per-tile diffs
        auto tile = [&](const std::vector<BYTE>& f, int idx, std::vector<BYTE>& out) {
            out.assign((size_t)TILE * TILE * 4, 0);
            for (int y = 0; y < TILE; y++)
                memcpy(out.data() + (size_t)y * TILE * 4, f.data() + ((size_t)y * hostW + idx * (TILE + GAP)) * 4, (size_t)TILE * 4);
        };
        std::vector<BYTE> A, B, C;
        tile(full, 0, A); tile(full, 1, B); tile(full, 2, C);
        if (pass == 0) { SaveBgra("mag3_A_plain", A, TILE, TILE); SaveBgra("mag3_B_show", B, TILE, TILE); SaveBgra("mag3_C_showclip", C, TILE, TILE); }
        auto diff = [&](const char* tag, const std::vector<BYTE>& X) {
            int minx = TILE, miny = TILE, maxx = -1, maxy = -1, n = 0;
            unsigned long long sad = 0;
            for (int y = 0; y < TILE; y++) for (int x = 0; x < TILE; x++) {
                size_t o = ((size_t)y * TILE + x) * 4;
                if (memcmp(X.data() + o, A.data() + o, 3) != 0) {
                    n++; sad += abs((int)X[o] - A[o]) + abs((int)X[o + 1] - A[o + 1]) + abs((int)X[o + 2] - A[o + 2]);
                    if (x < minx) minx = x; if (x > maxx) maxx = x;
                    if (y < miny) miny = y; if (y > maxy) maxy = y;
                }
            }
            printf("  [pass%d] %s vs A: diffPixels=%d SAD=%llu bbox=", pass, tag, n, sad);
            if (maxx < 0) printf("<none>\n");
            else printf("(%d,%d)-(%d,%d) => %dx%d\n", minx, miny, maxx + 1, maxy + 1, maxx + 1 - minx, maxy + 1 - miny);
            if (maxx >= 0 && pass == 0) {
                // extract padded patch from B (the one with the style bit)
                int pad = 8;
                int x0 = max(0, minx - pad), y0 = max(0, miny - pad);
                int x1 = min(TILE, maxx + 1 + pad), y1 = min(TILE, maxy + 1 + pad);
                int pw = x1 - x0, ph = y1 - y0;
                std::vector<BYTE> patch((size_t)pw * ph * 4);
                for (int y = 0; y < ph; y++)
                    memcpy(patch.data() + (size_t)y * pw * 4, B.data() + ((size_t)(y0 + y) * TILE + x0) * 4, (size_t)pw * 4);
                char pn[128]; sprintf_s(pn, "patch_%s_x%d_y%d", tag, x0, y0);
                SaveBgra(pn, patch, pw, ph);
                printf("      patch origin in tile=(%d,%d) size=%dx%d  (cursor origin expected at (%d,%d))\n",
                    x0, y0, pw, ph, (int)(cp.x - srcrect.left), (int)(cp.y - srcrect.top));
            }
        };
        diff("B", B);
        diff("C", C);
        // also dump a couple of background sample pixels for the record
        if (pass == 0) {
            size_t o = ((size_t)5 * TILE + 5) * 4;
            printf("  [pass0] A bg sample=(%u,%u,%u) B same=(%u,%u,%u)\n",
                A[o], A[o + 1], A[o + 2], B[o], B[o + 1], B[o + 2]);
        }
        Pump(700);
    }

    printf("\nMagShowSystemCursor(FALSE) -> %d\n", MagShowSystemCursor(FALSE));
    Pump(500);
    { std::vector<BYTE> f; GrabScreen(hx, hy, hostW, hostH, f); SaveBgra("mag3_after_hide", f, hostW, hostH); }
    printf("MagShowSystemCursor(TRUE) -> %d\n", MagShowSystemCursor(TRUE));
    printf("=== done ===\n");
    return 0;
}
