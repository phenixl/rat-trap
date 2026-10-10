using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RatTrap.Interop;

namespace RatTrap.Capture;

/// <summary>An immutable description of one display monitor.</summary>
public sealed record MonitorDescriptor(
    int Index,
    IntPtr Handle,
    RECT Rect,
    RECT WorkRect,
    uint DpiX,
    uint DpiY,
    bool IsPrimary,
    string DeviceName)
{
    public int Width => Rect.Width;
    public int Height => Rect.Height;

    /// <summary>Display scale factor (1.0 at 96 DPI).</summary>
    public double Scale => DpiX / 96.0;

    public bool Contains(int screenX, int screenY) => Rect.Contains(screenX, screenY);

    public override string ToString()
        => $"[{Index}] {DeviceName} {Rect} {Width}x{Height} dpi={DpiX}x{DpiY}{(IsPrimary ? " primary" : string.Empty)}";
}

/// <summary>Enumerates monitors and resolves the monitor under a point.</summary>
public static class Monitors
{
    // EnumDisplayMonitors is a synchronous, callback-driven API. The callback is a
    // [UnmanagedCallersOnly] function pointer (the only AOT-safe way to hand a managed
    // function to native code), so it cannot capture state — it appends to this static
    // list instead. Enumeration is therefore not reentrant; all callers live on the
    // single UI/capture thread, and the lock makes a violation fail loudly rather than
    // silently interleave.
    private static readonly object Gate = new();
    private static List<MonitorDescriptor>? _collector;
    private static bool _enumerating;

    public static IReadOnlyList<MonitorDescriptor> Enumerate()
    {
        lock (Gate)
        {
            if (_enumerating)
            {
                throw new InvalidOperationException("Monitor enumeration is already in progress on another thread.");
            }
            _enumerating = true;
            _collector = new List<MonitorDescriptor>(4);
        }

        try
        {
            unsafe
            {
                delegate* unmanaged[Stdcall]<IntPtr, IntPtr, RECT*, IntPtr, int> callback = &EnumCallback;
                if (!User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr)callback, IntPtr.Zero))
                {
                    throw new InvalidOperationException(
                        $"EnumDisplayMonitors failed with Win32 error {Marshal.GetLastPInvokeError()}.");
                }
            }

            List<MonitorDescriptor> collected;
            lock (Gate)
            {
                collected = _collector!;
            }

            // Order by position so Index matches the visual left-to-right, top-to-bottom
            // layout. Enumeration order is not defined by the API.
            var ordered = collected
                .OrderBy(m => m.Rect.Top)
                .ThenBy(m => m.Rect.Left)
                .ToList();

            var result = new List<MonitorDescriptor>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                result.Add(ordered[i] with { Index = i });
            }
            return result;
        }
        finally
        {
            lock (Gate)
            {
                _collector = null;
                _enumerating = false;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int EnumCallback(IntPtr hMonitor, IntPtr hdc, RECT* lprc, IntPtr data)
    {
        try
        {
            var info = MONITORINFOEXW.Create();
            if (!User32.GetMonitorInfoWEx(hMonitor, ref info))
            {
                // Not fatal: keep enumerating the other monitors.
                return 1;
            }

            uint dpiX = 96, dpiY = 96;
            // GetDpiForMonitor needs a per-monitor-aware process (we are PMv2 via the
            // application manifest). If it fails we assume 96 DPI rather than lying.
            if (Shcore.GetDpiForMonitor(hMonitor, Shcore.MDT_EFFECTIVE_DPI, out uint dx, out uint dy) == 0)
            {
                dpiX = dx == 0 ? 96 : dx;
                dpiY = dy == 0 ? 96 : dy;
            }

            var descriptor = new MonitorDescriptor(
                Index: -1, // assigned after sorting
                Handle: hMonitor,
                Rect: info.rcMonitor,
                WorkRect: info.rcWork,
                DpiX: dpiX,
                DpiY: dpiY,
                IsPrimary: (info.dwFlags & MONITORINFO.MONITORINFOF_PRIMARY) != 0,
                DeviceName: info.DeviceName);

            _collector!.Add(descriptor);
        }
        catch
        {
            // An exception must never cross the native callback boundary; swallowing it
            // and continuing leaves the remaining monitors discoverable.
        }

        return 1; // continue enumeration
    }

    /// <summary>Monitor containing the point, or the nearest one, or the primary.</summary>
    public static MonitorDescriptor FromPoint(int screenX, int screenY)
    {
        var all = Enumerate();
        if (all.Count == 0)
        {
            throw new InvalidOperationException("No monitors were reported by the system.");
        }

        foreach (var m in all)
        {
            if (m.Contains(screenX, screenY))
            {
                return m;
            }
        }

        // Nearest by clamped distance, so a point in the "dead space" of an L-shaped
        // multi-monitor arrangement still resolves to something sensible.
        MonitorDescriptor best = all[0];
        long bestDistance = long.MaxValue;
        foreach (var m in all)
        {
            long dx = 0, dy = 0;
            if (screenX < m.Rect.Left) dx = m.Rect.Left - screenX;
            else if (screenX >= m.Rect.Right) dx = screenX - m.Rect.Right + 1;
            if (screenY < m.Rect.Top) dy = m.Rect.Top - screenY;
            else if (screenY >= m.Rect.Bottom) dy = screenY - m.Rect.Bottom + 1;

            long distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = m;
            }
        }
        return best;
    }

    public static MonitorDescriptor Primary()
    {
        var all = Enumerate();
        foreach (var m in all)
        {
            if (m.IsPrimary)
            {
                return m;
            }
        }
        return all.Count > 0
            ? all[0]
            : throw new InvalidOperationException("No monitors were reported by the system.");
    }
}
