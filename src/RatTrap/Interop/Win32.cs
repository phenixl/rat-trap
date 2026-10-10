using System.Runtime.InteropServices;

namespace RatTrap.Interop;

// ---------------------------------------------------------------------------------
// Structures
// ---------------------------------------------------------------------------------

/// <summary>
/// A Win32 POINT. Public because it is part of this program's own API surface (pointer
/// positions), not just an interop detail.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X;
    public int Y;
    public override readonly string ToString() => $"({X},{Y})";
}

/// <summary>
/// A Win32 RECT (exclusive right/bottom). Public because screen rectangles are part of this
/// program's own API surface.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public readonly bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom})";

    public static RECT FromSize(int left, int top, int width, int height)
        => new() { Left = left, Top = top, Right = left + width, Bottom = top + height };

    /// <summary>Clamps this rectangle so it fits inside <paramref name="bounds"/>, preserving size when possible.</summary>
    public readonly RECT ClampInside(RECT bounds)
    {
        int width = Math.Min(Width, bounds.Width);
        int height = Math.Min(Height, bounds.Height);
        int left = Math.Clamp(Left, bounds.Left, bounds.Right - width);
        int top = Math.Clamp(Top, bounds.Top, bounds.Bottom - height);
        return FromSize(left, top, width, height);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int cx;
    public int cy;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CURSORINFO
{
    public int cbSize;
    public int flags;
    public IntPtr hCursor;
    public POINT ptScreenPos;

    public const int CURSOR_SHOWING = 0x00000001;
    public const int CURSOR_SUPPRESSED = 0x00000002;

    public static CURSORINFO Create() => new() { cbSize = Marshal.SizeOf<CURSORINFO>() };
}

[StructLayout(LayoutKind.Sequential)]
internal struct ICONINFO
{
    public int fIcon;
    public int xHotspot;
    public int yHotspot;
    public IntPtr hbmMask;
    public IntPtr hbmColor;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAP
{
    public int bmType;
    public int bmWidth;
    public int bmHeight;
    public int bmWidthBytes;
    public ushort bmPlanes;
    public ushort bmBitsPixel;
    public IntPtr bmBits;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;

    public const uint BI_RGB = 0;
    public const uint BI_BITFIELDS = 3;

    public static BITMAPINFOHEADER Create(int width, int height, ushort bitCount) => new()
    {
        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
        biWidth = width,
        biHeight = height,
        biPlanes = 1,
        biBitCount = bitCount,
        biCompression = BI_RGB,
        biSizeImage = 0,
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFO
{
    public BITMAPINFOHEADER bmiHeader;
    // bmiColors follows; for 32bpp BI_RGB we never read it, but the OS writes up to
    // 3 DWORDs (masks) into the space after the header for BI_BITFIELDS. Reserving
    // room here keeps GetDIBits from smashing the stack.
    public uint bmiColors0;
    public uint bmiColors1;
    public uint bmiColors2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;

    public const uint MONITORINFOF_PRIMARY = 0x00000001;

    public static MONITORINFO Create() => new() { cbSize = Marshal.SizeOf<MONITORINFO>() };
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFOEXW
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    public unsafe fixed char szDevice[32];

    public static MONITORINFOEXW Create() => new() { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };

    /// <summary>
    /// The GDI device name, e.g. <c>\\.\DISPLAY1</c>. This is the same identifier that
    /// appears in <c>DXGI_OUTPUT_DESC.DeviceName</c>, which is how a monitor handle is
    /// matched to a DXGI output.
    /// </summary>
    public readonly unsafe string DeviceName
    {
        get
        {
            fixed (char* p = szDevice)
            {
                return new string(p);
            }
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAY_DEVICEW
{
    public int cb;
    public unsafe fixed char DeviceName[32];
    public unsafe fixed char DeviceString[128];
    public uint StateFlags;
    public unsafe fixed char DeviceID[128];
    public unsafe fixed char DeviceKey[128];
}

[StructLayout(LayoutKind.Sequential)]
internal struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public IntPtr lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public IntPtr hInstance;
    public IntPtr hIcon;
    public IntPtr hCursor;
    public IntPtr hbrBackground;
    public IntPtr lpszMenuName;
    public IntPtr lpszClassName;
    public IntPtr hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PAINTSTRUCT
{
    public IntPtr hdc;
    public int fErase;
    public RECT rcPaint;
    public int fRestore;
    public int fIncUpdate;
    public unsafe fixed byte rgbReserved[32];
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr hwnd;
    public uint message;
    public UIntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public POINT pt;
    public uint lPrivate;
}

// ---------------------------------------------------------------------------------
// user32.dll
// ---------------------------------------------------------------------------------

internal static partial class User32
{
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const int SM_CXCURSOR = 13;
    public const int SM_CYCURSOR = 14;
    public const int SM_CXICON = 11;
    public const int SM_CYICON = 12;

    [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    public static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "GetCursorInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorInfo(ref CURSORINFO pci);

    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll", EntryPoint = "SetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll", EntryPoint = "GetIconInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [LibraryImport("user32.dll", EntryPoint = "GetIconInfoExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetIconInfoExW(IntPtr hIcon, ref ICONINFOEXW piconinfo);

    [LibraryImport("user32.dll", EntryPoint = "CopyIcon")]
    public static partial IntPtr CopyIcon(IntPtr hIcon);

    [LibraryImport("user32.dll", EntryPoint = "DestroyIcon")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr hIcon);

    [LibraryImport("user32.dll", EntryPoint = "DrawIconEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth,
        uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    public const uint DI_NORMAL = 0x0003;
    public const uint DI_COMPAT = 0x0004;

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorFromFileW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr LoadCursorFromFileW(string lpFileName);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    public const uint IMAGE_CURSOR = 2;
    public const uint LR_LOADFROMFILE = 0x00000010;
    public const uint LR_DEFAULTSIZE = 0x00000040;
    public const uint LR_SHARED = 0x00008000;

    [LibraryImport("user32.dll", EntryPoint = "GetDC")]
    public static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseDC")]
    public static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    public static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    public static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoWEx(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    public const uint MONITOR_DEFAULTTONULL = 0x00000000;
    public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    public delegate int MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr data);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplayMonitors")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, IntPtr lpfnEnum, IntPtr dwData);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "SetProcessDpiAwarenessContext")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDpiAwarenessContext(IntPtr value);

    // DPI_AWARENESS_CONTEXT handles (they are negative pseudo-handles).
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = new(-2);

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    public static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForSystem")]
    public static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll", EntryPoint = "AdjustWindowRectExForDpi")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustWindowRectExForDpi(ref RECT lpRect, uint dwStyle, [MarshalAs(UnmanagedType.Bool)] bool bMenu, uint dwExStyle, uint dpi);

    // --- window plumbing ---------------------------------------------------------

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    public static partial ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial IntPtr DefWindowProcW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial IntPtr DispatchMessageW(ref MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "PostQuitMessage")]
    public static partial void PostQuitMessage(int nExitCode);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessageW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll", EntryPoint = "UpdateWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "InvalidateRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [LibraryImport("user32.dll", EntryPoint = "BeginPaint")]
    public static partial IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [LibraryImport("user32.dll", EntryPoint = "EndPaint")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [LibraryImport("user32.dll", EntryPoint = "GetClientRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", EntryPoint = "SetTimer")]
    public static partial UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [LibraryImport("user32.dll", EntryPoint = "KillTimer")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowTextW(IntPtr hWnd, string lpString);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetWindowTextW(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [LibraryImport("user32.dll", EntryPoint = "EnableWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnableWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bEnable);

    [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "WindowFromPoint")]
    public static partial IntPtr WindowFromPoint(POINT point);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassNameW(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "FillRect")]
    public static partial int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

    public const uint DT_LEFT = 0x00000000;
    public const uint DT_TOP = 0x00000000;
    public const uint DT_WORDBREAK = 0x00000010;
    public const uint DT_NOPREFIX = 0x00000800;

    [LibraryImport("user32.dll", EntryPoint = "GetDlgItem")]
    public static partial IntPtr GetDlgItem(IntPtr hDlg, int nIDDlgItem);

    [LibraryImport("user32.dll", EntryPoint = "MoveWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static partial IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    public static partial IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    /// <summary>
    /// The resource id that .NET's <c>ApplicationIcon</c> embeds the application icon under: it is
    /// <c>IDI_APPLICATION</c>, so loading this id returns this program's own icon rather than the
    /// generic system one.
    /// </summary>
    public static readonly IntPtr IDI_APPLICATION = new(32512);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "IsIconic")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(IntPtr hWnd);

    // --- global hot keys ---------------------------------------------------------------
    [LibraryImport("user32.dll", EntryPoint = "RegisterHotKey")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterHotKey")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint WM_HOTKEY = 0x0312;

    // --- shell and sound ---------------------------------------------------------------
    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr ShellExecuteW(IntPtr hwnd, string? lpOperation, string lpFile,
        string? lpParameters, string? lpDirectory, int nShowCmd);

    public const int SW_SHOWNORMAL = 1;

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PlaySoundW(string? pszSound, IntPtr hmod, uint fdwSound);

    public const uint SND_ALIAS = 0x00010000;
    public const uint SND_ASYNC = 0x00000001;
    public const uint SND_NODEFAULT = 0x00000002;
    public const uint SND_FILENAME = 0x00020000;

    // --- DWM ---------------------------------------------------------------------------
    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static partial int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out RECT pvAttribute, uint cbAttribute);

    public const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    public const int GWLP_USERDATA = -21;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_CREATE = 0x0001;
    public const uint WM_APP = 0x8000;
    public const uint WM_SETFONT = 0x0030;
    public const uint WM_GETDLGCODE = 0x0087;
    public const int SW_SHOW = 5;
    public const int SW_HIDE = 0;
    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_TABSTOP = 0x00010000;
    public const uint WS_BORDER = 0x00800000;
    public const uint WS_VSCROLL = 0x00200000;
    public const uint BS_PUSHBUTTON = 0x00000000;
    public const uint BS_GROUPBOX = 0x00000007;
    public const uint SS_LEFT = 0x00000000;
    public const uint ES_MULTILINE = 0x0004;
    public const uint ES_AUTOVSCROLL = 0x0040;
    public const uint ES_READONLY = 0x0800;
    public const uint CW_USEDEFAULT = 0x80000000;
    public const uint MB_OK = 0x00000000;
    public const uint MB_ICONERROR = 0x00000010;
    public const uint MB_ICONINFORMATION = 0x00000040;
    public const uint MB_SETFOREGROUND = 0x00010000;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ICONINFOEXW
{
    public int cbSize;
    public int fIcon;
    public int xHotspot;
    public int yHotspot;
    public IntPtr hbmMask;
    public IntPtr hbmColor;
    public ushort wResID;
    public unsafe fixed char szModName[260];
    public unsafe fixed char szResName[260];
}

// ---------------------------------------------------------------------------------
// gdi32.dll
// ---------------------------------------------------------------------------------

internal static partial class Gdi32
{
    public const int SRCCOPY = 0x00CC0020;
    public const int CAPTUREBLT = 0x40000000;
    public const int DIB_RGB_COLORS = 0;

    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")]
    public static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleBitmap")]
    public static partial IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateDIBSection")]
    public static partial IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [LibraryImport("gdi32.dll", EntryPoint = "SelectObject")]
    public static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr ho);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "BitBlt")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);

    [LibraryImport("gdi32.dll", EntryPoint = "StretchBlt")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);

    [LibraryImport("gdi32.dll", EntryPoint = "SetStretchBltMode")]
    public static partial int SetStretchBltMode(IntPtr hdc, int mode);

    public const int COLORONCOLOR = 3;
    public const int HALFTONE = 4;

    [LibraryImport("gdi32.dll", EntryPoint = "GetDIBits")]
    public static partial int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, IntPtr lpvBits, ref BITMAPINFO lpbmi, uint usage);

    [LibraryImport("gdi32.dll", EntryPoint = "StretchDIBits")]
    public static partial int StretchDIBits(IntPtr hdc, int xDest, int yDest, int destWidth, int destHeight,
        int xSrc, int ySrc, int srcWidth, int srcHeight, IntPtr lpBits, ref BITMAPINFO lpbmi, uint iUsage, int rop);

    [LibraryImport("gdi32.dll", EntryPoint = "SetDIBitsToDevice")]
    public static partial int SetDIBitsToDevice(IntPtr hdc, int xDest, int yDest, uint w, uint h, int xSrc, int ySrc,
        uint startScan, uint cLines, IntPtr lpvBits, ref BITMAPINFO lpbmi, uint colorUse);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    public static partial int GetObjectW(IntPtr h, int c, ref BITMAP pv);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateSolidBrush")]
    public static partial IntPtr CreateSolidBrush(int color);

    [LibraryImport("gdi32.dll", EntryPoint = "GetDeviceCaps")]
    public static partial int GetDeviceCaps(IntPtr hdc, int index);

    [LibraryImport("gdi32.dll", EntryPoint = "PatBlt")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PatBlt(IntPtr hdc, int x, int y, int w, int h, int rop);

    [LibraryImport("gdi32.dll", EntryPoint = "GetStockObject")]
    public static partial IntPtr GetStockObject(int i);

    public const int DEFAULT_GUI_FONT = 17;

    [LibraryImport("gdi32.dll", EntryPoint = "SetTextColor")]
    public static partial int SetTextColor(IntPtr hdc, int color);

    [LibraryImport("gdi32.dll", EntryPoint = "SetBkMode")]
    public static partial int SetBkMode(IntPtr hdc, int mode);

    public const int TRANSPARENT = 1;

    [LibraryImport("gdi32.dll", EntryPoint = "CreatePen")]
    public static partial IntPtr CreatePen(int style, int width, int color);

    public const int PS_SOLID = 0;

    [LibraryImport("gdi32.dll", EntryPoint = "MoveToEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveToEx(IntPtr hdc, int x, int y, IntPtr lppt);

    [LibraryImport("gdi32.dll", EntryPoint = "LineTo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LineTo(IntPtr hdc, int x, int y);

    public const int BLACKNESS = 0x00000042;
    public const int WHITENESS = 0x00FF0062;
}

// ---------------------------------------------------------------------------------
// shcore.dll
// ---------------------------------------------------------------------------------

internal static partial class Shcore
{
    public const int MDT_EFFECTIVE_DPI = 0;
    public const int MDT_ANGULAR_DPI = 1;
    public const int MDT_RAW_DPI = 2;

    [LibraryImport("shcore.dll", EntryPoint = "GetDpiForMonitor")]
    public static partial int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}

// ---------------------------------------------------------------------------------
// kernel32.dll
// ---------------------------------------------------------------------------------

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandleW(string? lpModuleName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetConsoleWindow")]
    public static partial IntPtr GetConsoleWindow();

    [LibraryImport("kernel32.dll", EntryPoint = "AttachConsole")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(uint dwProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "FreeConsole")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", EntryPoint = "SetConsoleOutputCP")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleOutputCP(uint wCodePageID);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "SetStdHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetStdHandle(int nStdHandle, IntPtr hHandle);

    public const int STD_INPUT_HANDLE = -10;
    public const int STD_OUTPUT_HANDLE = -11;
    public const int STD_ERROR_HANDLE = -12;

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;

    public const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
}

// ---------------------------------------------------------------------------------
// winmm.dll — used only to raise the timer resolution for capture pacing.
// ---------------------------------------------------------------------------------

internal static partial class Winmm
{
    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static partial uint TimeBeginPeriod(uint uPeriod);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static partial uint TimeEndPeriod(uint uPeriod);
}

