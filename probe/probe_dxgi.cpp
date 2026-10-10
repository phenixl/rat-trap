// probe_dxgi.cpp - Does DXGI Desktop Duplication hand out the REAL cursor shape with alpha,
// and at what size?  GetFramePointerShape is the only documented API that returns a cursor
// bitmap + position outside of the icon/cursor APIs.
#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <stdio.h>
#include <string.h>
#include <vector>
#include <string>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "advapi32.lib")

static const char* OUTDIR = "C:\\ai-projects\\cursor-trap\\probe\\out\\";

static unsigned long long Hash(const BYTE* p, size_t n) {
    unsigned long long f = 1469598103934665603ULL;
    for (size_t i = 0; i < n; i++) { f ^= p[i]; f *= 1099511628211ULL; }
    return f;
}

int main() {
    CreateDirectoryA("C:\\ai-projects\\cursor-trap\\probe\\out", NULL);
    DWORD base = 0, cb = sizeof(base), type = 0;
    RegGetValueA(HKEY_CURRENT_USER, "Control Panel\\Cursors", "CursorBaseSize", RRF_RT_REG_DWORD, &type, &base, &cb);
    printf("=== DXGI Desktop Duplication pointer-shape probe ===\n");
    printf("CursorBaseSize = %lu ; SM_CXCURSOR = %d\n", base, GetSystemMetrics(SM_CXCURSOR));

    CURSORINFO ci = {}; ci.cbSize = sizeof(ci); GetCursorInfo(&ci);
    printf("GetCursorInfo: flags=0x%lX pos=(%ld,%ld) hCursor=%p\n", ci.flags, ci.ptScreenPos.x, ci.ptScreenPos.y, (void*)ci.hCursor);
    if (ci.hCursor) {
        ICONINFO ii = {};
        if (GetIconInfo(ci.hCursor, &ii)) {
            BITMAP bm = {};
            if (ii.hbmColor) { GetObject(ii.hbmColor, sizeof(bm), &bm); printf("  GetIconInfo hbmColor = %ldx%ld hotspot=(%ld,%ld)\n", bm.bmWidth, bm.bmHeight, ii.xHotspot, ii.yHotspot); }
            if (ii.hbmColor) DeleteObject(ii.hbmColor);
            if (ii.hbmMask) DeleteObject(ii.hbmMask);
        }
    }

    ID3D11Device* dev = nullptr; ID3D11DeviceContext* ctx = nullptr;
    D3D_FEATURE_LEVEL fl;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
        D3D11_SDK_VERSION, &dev, &fl, &ctx);
    printf("D3D11CreateDevice hr=0x%lX\n", hr);
    if (FAILED(hr)) return 1;

    // Find the output whose desktop contains the cursor.
    IDXGIDevice* dxgiDev = nullptr; dev->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgiDev);
    IDXGIAdapter* adapter = nullptr; dxgiDev->GetAdapter(&adapter);
    IDXGIOutput* out = nullptr; IDXGIOutput1* out1 = nullptr;
    for (UINT i = 0; adapter->EnumOutputs(i, &out) != DXGI_ERROR_NOT_FOUND; i++) {
        DXGI_OUTPUT_DESC od = {}; out->GetDesc(&od);
        POINT p = ci.ptScreenPos;
        bool contains = p.x >= od.DesktopCoordinates.left && p.x < od.DesktopCoordinates.right &&
                        p.y >= od.DesktopCoordinates.top && p.y < od.DesktopCoordinates.bottom;
        printf("output[%u] %S rect=(%ld,%ld)-(%ld,%ld) containsCursor=%d\n", i, od.DeviceName,
            od.DesktopCoordinates.left, od.DesktopCoordinates.top, od.DesktopCoordinates.right, od.DesktopCoordinates.bottom, contains);
        if (contains && !out1) { out->QueryInterface(__uuidof(IDXGIOutput1), (void**)&out1); break; }
        out->Release(); out = nullptr;
    }
    if (!out1) { printf("no output found for the cursor point\n"); return 1; }
    out->Release();

    IDXGIOutputDuplication* dup = nullptr;
    hr = out1->DuplicateOutput(dev, &dup);
    printf("DuplicateOutput hr=0x%lX\n", hr);
    if (FAILED(hr)) { printf("  (0x887A0004=UNSUPPORTED, 0x887A0022=NOT_CURRENTLY_AVAILABLE: another app holds it)\n"); return 1; }

    DXGI_OUTDUPL_DESC dd = {}; dup->GetDesc(&dd);
    printf("duplication desc: mode=%d rotation=%d desktopImage=%ux%u\n", dd.ModeDesc.Format, dd.Rotation,
        dd.ModeDesc.Width, dd.ModeDesc.Height);

    int shapes = 0;
    unsigned long long lastShapeHash = 0;
    for (int i = 0; i < 40 && shapes < 6; i++) {
        DXGI_OUTDUPL_FRAME_INFO fi = {};
        IDXGIResource* res = nullptr;
        HRESULT ahr = dup->AcquireNextFrame(200, &fi, &res);
        if (ahr == DXGI_ERROR_WAIT_TIMEOUT) { printf("  [%d] AcquireNextFrame timeout\n", i); continue; }
        if (FAILED(ahr)) { printf("  [%d] AcquireNextFrame hr=0x%lX\n", i, ahr); break; }
        printf("[%d] frame: PointerShapeBufferSize=%u PointerPosition=(%ld,%ld) LastMouseUpdate=%lld AccumulatedFrames=%u\n",
            i, fi.PointerShapeBufferSize, fi.PointerPosition.Position.x, fi.PointerPosition.Position.y,
            (long long)fi.LastMouseUpdateTime.QuadPart, fi.AccumulatedFrames);

        if (fi.PointerShapeBufferSize > 0) {
            std::vector<BYTE> buf(fi.PointerShapeBufferSize);
            UINT needed = 0;
            DXGI_OUTDUPL_POINTER_SHAPE_INFO psi = {};
            HRESULT shr = dup->GetFramePointerShape((UINT)buf.size(), buf.data(), &needed, &psi);
            printf("      GetFramePointerShape hr=0x%lX needed=%u Type=%u Width=%u Height=%u Pitch=%u HotSpot=(%ld,%ld)\n",
                shr, needed, psi.Type, psi.Width, psi.Height, psi.Pitch, psi.HotSpot.x, psi.HotSpot.y);
            if (SUCCEEDED(shr)) {
                size_t expect = (size_t)psi.Pitch * psi.Height * (psi.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME ? 2 : 1);
                unsigned long long h = Hash(buf.data(), buf.size() < expect ? buf.size() : expect);
                printf("      shapeHash=%016llX distinctFromPrevious=%d bufferBytes=%zu expectedBytes=%zu\n",
                    h, (lastShapeHash && h != lastShapeHash) ? 1 : 0, buf.size(), expect);
                lastShapeHash = h;
                char nm[128]; sprintf_s(nm, "dxgi_shape_%d_%ux%u_t%u", shapes, psi.Width, psi.Height, psi.Type);
                FILE* f = fopen((std::string(OUTDIR) + nm + ".bgra").c_str(), "wb");
                if (f) { fwrite(buf.data(), 1, buf.size() < expect ? buf.size() : expect, f); fclose(f); printf("      saved %s\n", nm); }
                shapes++;
            }
        }
        if (res) res->Release();
        dup->ReleaseFrame();
    }
    if (shapes == 0) printf("!! no pointer shape was ever reported\n");
    printf("=== done ===\n");
    return 0;
}
