using RatTrap.Imaging;
using RatTrap.Interop;

namespace RatTrap.Capture;

/// <summary>
/// Screen capture through GDI: <c>BitBlt</c> from the screen device context into a
/// top-down 32bpp DIB section.
/// </summary>
/// <remarks>
/// <para>
/// Properties that make this the indispensable fallback and a first-class capture source:
/// </para>
/// <list type="bullet">
///   <item>It is always available - no GPU, no D3D device, no duplication slot.</item>
///   <item>It reads the <b>desktop image as the user sees it, cursor included</b>, which
///     is the composite <c>D</c> the reconstruction solver needs.</item>
///   <item>It costs nothing extra to read only the requested rectangle, because the
///     source coordinates are passed straight to <c>BitBlt</c>.</item>
/// </list>
/// <para>
/// Limitations, which is why the DXGI backend exists alongside it:
/// </para>
/// <list type="bullet">
///   <item>Under exclusive fullscreen the desktop composition is bypassed, so <c>BitBlt</c>
///     returns stale or black content.</item>
///   <item>DRM-protected output is blacked out.</item>
///   <item>The screen DC's coordinate space is anchored at the primary monitor's top-left;
///     monitors left of or above the primary legitimately use negative coordinates, which
///     is handled by passing desktop coordinates through unchanged.</item>
/// </list>
/// </remarks>
public sealed class GdiScreenSource : ICaptureSource
{
    private readonly object _sync = new();

    private IntPtr _memoryDc;
    private IntPtr _dibSection;
    private IntPtr _previousBitmap;
    private IntPtr _bits;
    private int _regionWidth;
    private int _regionHeight;
    private bool _disposed;

    public GdiScreenSource(MonitorDescriptor monitor)
    {
        Monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
    }

    public string Name => "GDI BitBlt";

    public MonitorDescriptor Monitor { get; }

    /// <inheritdoc />
    /// <remarks>
    /// GDI <c>BitBlt</c> of the screen DC returns the composed desktop, and the system
    /// cursor is part of that composition, so the returned pixels contain the pointer.
    /// </remarks>
    public bool IncludesPointer => true;

    public CaptureResult Capture(RECT region, ArgbBitmap destination, int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CaptureSupport.ValidateRegion(Monitor, region, destination);

        lock (_sync)
        {
            if (!EnsureTarget(region.Width, region.Height, out string? setupError))
            {
                return new CaptureResult(CaptureStatus.Failed, setupError);
            }

            IntPtr screenDc = User32.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return new CaptureResult(CaptureStatus.Failed,
                    $"GetDC(NULL) failed with Win32 error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}.");
            }

            try
            {
                // CAPTUREBLT is required for layered/transparent windows to appear in the
                // result; without it many game overlays silently vanish.
                bool ok = Gdi32.BitBlt(_memoryDc, 0, 0, region.Width, region.Height,
                    screenDc, region.Left, region.Top, Gdi32.SRCCOPY | Gdi32.CAPTUREBLT);
                if (!ok)
                {
                    return new CaptureResult(CaptureStatus.Failed,
                        $"BitBlt failed with Win32 error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}.");
                }
            }
            finally
            {
                User32.ReleaseDC(IntPtr.Zero, screenDc);
            }

            unsafe
            {
                var source = new ReadOnlySpan<byte>((void*)_bits, destination.Pixels.Length);
                source.CopyTo(destination.Pixels);
            }

            CaptureSupport.ForceOpaque(destination);
            return new CaptureResult(CaptureStatus.Captured);
        }
    }

    public void Reinitialize()
    {
        lock (_sync)
        {
            ReleaseTarget();
        }
    }

    private bool EnsureTarget(int width, int height, out string? error)
    {
        error = null;
        if (_memoryDc != IntPtr.Zero && _regionWidth == width && _regionHeight == height)
        {
            return true;
        }

        ReleaseTarget();

        IntPtr screenDc = User32.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            error = "GetDC(NULL) failed while creating the GDI capture target.";
            return false;
        }

        try
        {
            _memoryDc = Gdi32.CreateCompatibleDC(screenDc);
            if (_memoryDc == IntPtr.Zero)
            {
                error = "CreateCompatibleDC failed.";
                return false;
            }

            // Negative height requests a TOP-DOWN DIB, so the memory layout already
            // matches ArgbBitmap and no row flip is needed anywhere.
            var info = new BITMAPINFO { bmiHeader = BITMAPINFOHEADER.Create(width, -height, 32) };
            _dibSection = Gdi32.CreateDIBSection(screenDc, ref info, Gdi32.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            if (_dibSection == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                error = "CreateDIBSection failed.";
                ReleaseTarget();
                return false;
            }

            _previousBitmap = Gdi32.SelectObject(_memoryDc, _dibSection);
            _regionWidth = width;
            _regionHeight = height;
            return true;
        }
        finally
        {
            User32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void ReleaseTarget()
    {
        if (_memoryDc != IntPtr.Zero)
        {
            if (_previousBitmap != IntPtr.Zero)
            {
                Gdi32.SelectObject(_memoryDc, _previousBitmap);
                _previousBitmap = IntPtr.Zero;
            }
            Gdi32.DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }

        if (_dibSection != IntPtr.Zero)
        {
            Gdi32.DeleteObject(_dibSection);
            _dibSection = IntPtr.Zero;
        }

        _bits = IntPtr.Zero;
        _regionWidth = 0;
        _regionHeight = 0;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseTarget();
        }
    }
}
