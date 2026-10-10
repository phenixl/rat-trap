// probe_sizes.cpp - what does LoadImage give for explicit cursor sizes, and does GetIconInfo
// follow the animation of an .ani cursor?
#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <vector>
#include <string>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")

static const char* OUTDIR = "C:\\ai-projects\\cursor-trap\\probe\\out\\";

static unsigned long long Fingerprint(const std::vector<BYTE>& px) {
    unsigned long long f = 1469598103934665603ULL;
    for (size_t i = 0; i < px.size(); i += 4) {
        if (px[i + 3] == 0) continue;
        for (int k = 0; k < 4; k++) { f ^= px[i + k]; f *= 1099511628211ULL; }
    }
    return f;
}

static bool Render(HCURSOR hc, int w, int h, std::vector<BYTE>& out) {
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

static void SaveBgra(const std::string& name, const std::vector<BYTE>& px, int w, int h) {
    FILE* f = fopen((std::string(OUTDIR) + name + ".bgra").c_str(), "wb");
    if (!f) return;
    for (int y = 0; y < h; y++) fwrite(px.data() + (size_t)y * w * 4, 1, (size_t)w * 4, f);
    fclose(f);
}

int main() {
    CreateDirectoryA("C:\\ai-projects\\cursor-trap\\probe\\out", NULL);
    printf("=== LoadImage sizing probe ===\n");
    printf("HKCU CursorBaseSize / SM_CXCURSOR: ");
    DWORD base = 0, cb = sizeof(base), ty = 0;
    RegGetValueA(HKEY_CURRENT_USER, "Control Panel\\Cursors", "CursorBaseSize", RRF_RT_REG_DWORD, &ty, &base, &cb);
    printf("%lu / %d\n", base, GetSystemMetrics(SM_CXCURSOR));

    const char* files[] = { "C:\\WINDOWS\\cursors\\aero_arrow.cur", "C:\\WINDOWS\\cursors\\aero_link.cur", "C:\\WINDOWS\\cursors\\aero_busy.ani" };
    int sizes[] = { 0, 16, 24, 32, 40, 48, 64, 80, 96, 128, 160, 256 };

    for (const char* path : files) {
        printf("\n--- %s ---\n", path);
        for (int s : sizes) {
            HCURSOR hc = (HCURSOR)LoadImageA(NULL, path, IMAGE_CURSOR, s, s, LR_LOADFROMFILE);
            if (!hc) { printf("  cxDesired=%4d -> LoadImage FAILED gle=%lu\n", s, GetLastError()); continue; }
            ICONINFO ii = {};
            if (!GetIconInfo(hc, &ii)) { printf("  cxDesired=%4d -> GetIconInfo FAILED\n", s); DestroyCursor(hc); continue; }
            BITMAP bm = {};
            long bw = 0, bh = 0;
            if (ii.hbmColor) { GetObject(ii.hbmColor, sizeof(bm), &bm); bw = bm.bmWidth; bh = bm.bmHeight; }
            std::vector<BYTE> px;
            Render(hc, 96, 96, px);   // render at a common size for fingerprint comparison
            printf("  cxDesired=%4d -> bitmap=%ldx%ld hotspot=(%ld,%ld) hbmColor=%s fp96=%016llX\n",
                s, bw, bh, ii.xHotspot, ii.yHotspot, ii.hbmColor ? "yes" : "NULL", Fingerprint(px));
            if (s == 80 || s == 96 || s == 64) {
                std::vector<BYTE> nat;
                if (bw > 0 && Render(hc, (int)bw, (int)bh, nat)) {
                    char nm[160]; sprintf_s(nm, "sz_%s_%ld", strrchr(path, '\\') + 1, bw);
                    SaveBgra(nm, nat, (int)bw, (int)bh);
                    printf("        saved %s (%ldx%ld)\n", nm, bw, bh);
                }
            }
            if (ii.hbmColor) DeleteObject(ii.hbmColor);
            if (ii.hbmMask) DeleteObject(ii.hbmMask);
            DestroyCursor(hc);
        }
    }

    printf("\n=== animated cursor: does GetIconInfo follow the animation? ===\n");
    const char* anis[] = { "C:\\WINDOWS\\cursors\\aero_busy.ani", "C:\\WINDOWS\\cursors\\aero_working.ani" };
    for (const char* path : anis) {
        HCURSOR hc = (HCURSOR)LoadImageA(NULL, path, IMAGE_CURSOR, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
        if (!hc) { printf("  %s load failed\n", path); continue; }
        printf("  %s : sampling GetIconInfo every 60 ms\n", path);
        unsigned long long prev = 0;
        int changes = 0, samples = 0;
        for (int i = 0; i < 25; i++) {
            std::vector<BYTE> px;
            Render(hc, 32, 32, px);
            unsigned long long f = Fingerprint(px);
            if (prev && f != prev) changes++;
            if (i == 0 || (prev && f != prev)) printf("    [%2d] fp=%016llX%s\n", i, f, prev ? "  <-- CHANGED" : "");
            prev = f; samples++;
            Sleep(60);
        }
        printf("    samples=%d frameChanges=%d => %s\n", samples, changes,
            changes ? "GetIconInfo DOES follow the .ani animation" : "GetIconInfo returns a STATIC frame");
        DestroyCursor(hc);
    }
    printf("=== done ===\n");
    return 0;
}
