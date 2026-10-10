// probe_dxgi2.cpp - two questions the architecture depends on:
//  1) does per-monitor-DPI awareness change the reported cursor size / coordinates?
//  2) can MORE THAN ONE process hold a Desktop Duplication session on the same output?
//     (OBS "Display Capture" uses Desktop Duplication, so this decides whether CursorTrap
//      can coexist with a running OBS display capture.)
// argv[1] = seconds to hold the duplication open (default 0).
#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <shellscalingapi.h>
#include <stdio.h>
#include <string.h>
#include <vector>
#include <string>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "shcore.lib")
#pragma comment(lib, "advapi32.lib")

static const char* OUTDIR = "C:\\ai-projects\\cursor-trap\\probe\\out\\";

int main(int argc, char** argv) {
    int hold = (argc > 1) ? atoi(argv[1]) : 0;
    printf("=== DXGI probe 2 (hold=%ds) ===\n", hold);

    printf("--- BEFORE dpi awareness ---\n");
    printf("  GetDpiForSystem=%u SM_CXSCREEN=%d SM_CYSCREEN=%d\n", GetDpiForSystem(),
        GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
    POINT p0 = {}; GetCursorPos(&p0);
    printf("  GetCursorPos=(%ld,%ld)\n", p0.x, p0.y);

    BOOL sok = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    printf("--- SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2) -> %d gle=%lu ---\n", sok, sok ? 0 : GetLastError());
    printf("  GetDpiForSystem=%u SM_CXSCREEN=%d SM_CYSCREEN=%d\n", GetDpiForSystem(),
        GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
    POINT p1 = {}; GetCursorPos(&p1);
    printf("  GetCursorPos=(%ld,%ld)\n", p1.x, p1.y);

    CURSORINFO ci = {}; ci.cbSize = sizeof(ci); GetCursorInfo(&ci);
    printf("  GetCursorInfo: flags=0x%lX CURSOR_SHOWING=%d pos=(%ld,%ld)\n",
        ci.flags, (ci.flags & CURSOR_SHOWING) ? 1 : 0, ci.ptScreenPos.x, ci.ptScreenPos.y);
    if (ci.hCursor) {
        ICONINFO ii = {}; GetIconInfo(ci.hCursor, &ii);
        BITMAP bm = {};
        if (ii.hbmColor) { GetObject(ii.hbmColor, sizeof(bm), &bm); printf("  live hbmColor=%ldx%ld hotspot=(%ld,%ld)\n", bm.bmWidth, bm.bmHeight, ii.xHotspot, ii.yHotspot); }
        if (ii.hbmColor) DeleteObject(ii.hbmColor);
        if (ii.hbmMask) DeleteObject(ii.hbmMask);
    }
    DWORD base = 0, cb = sizeof(base), ty = 0;
    RegGetValueA(HKEY_CURRENT_USER, "Control Panel\\Cursors", "CursorBaseSize", RRF_RT_REG_DWORD, &ty, &base, &cb);
    printf("  CursorBaseSize=%lu\n", base);

    ID3D11Device* dev = nullptr; ID3D11DeviceContext* ctx = nullptr; D3D_FEATURE_LEVEL fl;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &dev, &fl, &ctx);
    if (FAILED(hr)) { printf("D3D11CreateDevice failed 0x%lX\n", hr); return 1; }

    IDXGIDevice* dxgiDev = nullptr; dev->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgiDev);
    IDXGIAdapter* adapter = nullptr; dxgiDev->GetAdapter(&adapter);
    IDXGIOutput* out = nullptr; IDXGIOutput1* out1 = nullptr;
    POINT pc = ci.ptScreenPos;
    for (UINT i = 0; adapter->EnumOutputs(i, &out) != DXGI_ERROR_NOT_FOUND; i++) {
        DXGI_OUTPUT_DESC od = {}; out->GetDesc(&od);
        bool contains = pc.x >= od.DesktopCoordinates.left && pc.x < od.DesktopCoordinates.right &&
                        pc.y >= od.DesktopCoordinates.top && pc.y < od.DesktopCoordinates.bottom;
        UINT dx = 0, dy = 0;
        // DPI of this output via its monitor handle: use MonitorFromPoint + GetDpiForMonitor
        POINT center = { (od.DesktopCoordinates.left + od.DesktopCoordinates.right) / 2,
                         (od.DesktopCoordinates.top + od.DesktopCoordinates.bottom) / 2 };
        HMONITOR hm = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
        GetDpiForMonitor(hm, MDT_EFFECTIVE_DPI, &dx, &dy);
        printf("  output[%u] %S rect=(%ld,%ld)-(%ld,%ld) dpi=%ux%u containsCursor=%d\n", i, od.DeviceName,
            od.DesktopCoordinates.left, od.DesktopCoordinates.top, od.DesktopCoordinates.right, od.DesktopCoordinates.bottom,
            dx, dy, contains);
        if (contains && !out1) { out->QueryInterface(__uuidof(IDXGIOutput1), (void**)&out1); break; }
        out->Release(); out = nullptr;
    }
    if (!out1) { printf("no output for cursor\n"); return 1; }
    out->Release();

    IDXGIOutputDuplication* dup = nullptr;
    hr = out1->DuplicateOutput(dev, &dup);
    printf("DuplicateOutput -> hr=0x%lX %s\n", hr,
        SUCCEEDED(hr) ? "OK" : (hr == DXGI_ERROR_NOT_CURRENTLY_AVAILABLE ? "(NOT_CURRENTLY_AVAILABLE: max duplicators reached)"
            : hr == DXGI_ERROR_UNSUPPORTED ? "(UNSUPPORTED)" : "(other)"));
    if (FAILED(hr)) { printf("RESULT: duplication DENIED\n"); return 2; }
    DXGI_OUTDUPL_DESC dd = {}; dup->GetDesc(&dd);
    printf("  desc: img=%ux%u fmt=%d rotation=%d\n", dd.ModeDesc.Width, dd.ModeDesc.Height, dd.ModeDesc.Format, dd.Rotation);

    for (int i = 0; i < 30; i++) {
        DXGI_OUTDUPL_FRAME_INFO fi = {}; IDXGIResource* res = nullptr;
        HRESULT ahr = dup->AcquireNextFrame(300, &fi, &res);
        if (ahr == DXGI_ERROR_WAIT_TIMEOUT) continue;
        if (FAILED(ahr)) { printf("  AcquireNextFrame hr=0x%lX\n", ahr); break; }
        printf("  frame: shapeBytes=%u PointerPosition=(%ld,%ld) Visible=%d\n", fi.PointerShapeBufferSize,
            fi.PointerPosition.Position.x, fi.PointerPosition.Position.y, fi.PointerPosition.Visible);
        if (fi.PointerShapeBufferSize > 0) {
            std::vector<BYTE> buf(fi.PointerShapeBufferSize); UINT need = 0;
            DXGI_OUTDUPL_POINTER_SHAPE_INFO psi = {};
            if (SUCCEEDED(dup->GetFramePointerShape((UINT)buf.size(), buf.data(), &need, &psi))) {
                printf("    shape: Type=%u Width=%u Height=%u Pitch=%u HotSpot=(%ld,%ld) bytes=%zu\n",
                    psi.Type, psi.Width, psi.Height, psi.Pitch, psi.HotSpot.x, psi.HotSpot.y, buf.size());
                int aZero = 0, aFull = 0, other = 0;
                for (size_t k = 3; k < buf.size(); k += 4) { BYTE a = buf[k]; if (a == 0) aZero++; else if (a == 255) aFull++; else other++; }
                printf("    alpha: 0=%d 255=%d intermediate=%d\n", aZero, aFull, other);
            }
        }
        if (res) res->Release();
        dup->ReleaseFrame();
        break;
    }
    if (hold > 0) { printf("  holding duplication for %d s ...\n", hold); Sleep(hold * 1000); }
    printf("RESULT: duplication OK\n=== done ===\n");
    return 0;
}
