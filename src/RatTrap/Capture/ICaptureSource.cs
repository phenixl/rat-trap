using RatTrap.Imaging;
using RatTrap.Interop;

namespace RatTrap.Capture;

/// <summary>Which screen-capture implementation to use.</summary>
public enum CaptureBackend
{
    /// <summary>Pick the best available backend for the target monitor.</summary>
    Auto = 0,
    /// <summary>DXGI Desktop Duplication. Captures exclusive-fullscreen games; excludes the pointer.</summary>
    DxgiDuplication = 1,
    /// <summary>GDI BitBlt of the screen DC. Always available; includes the pointer; fails on exclusive fullscreen.</summary>
    GdiBitBlt = 2,
}

/// <summary>The pointer shape as reported by the composition engine.</summary>
/// <remarks>
/// This is the single highest-value piece of data in the whole program: it is the
/// compositor's own copy of the cursor (already scaled for the user's pointer-size and
/// DPI settings), together with its type and hotspot. When it is available there is
/// nothing to reconstruct. It is absent whenever the cursor is hidden
/// (<c>CURSOR_SHAPE</c> not reported) or when the app under the pointer draws its own
/// cursor - which is exactly the case rat-trap's reconstruction solver exists for.
/// </remarks>
public readonly record struct PointerShapeSnapshot(
    uint ShapeType,
    int Width,
    int Height,
    int Pitch,
    int HotspotX,
    int HotspotY,
    byte[] Buffer,
    long TimestampMs,
    bool Visible);

/// <summary>A capture backend that can also report the compositor's pointer shape.</summary>
public interface IPointerShapeProvider
{
    /// <summary>
    /// True when a pointer shape has been observed. <paramref name="snapshot"/> is the
    /// most recent one, which may be identical to a previous call.
    /// </summary>
    bool TryGetPointerShape(out PointerShapeSnapshot snapshot);
}

/// <summary>Public view of the duplication session's image geometry.</summary>
public readonly record struct DuplicationInfo(
    uint Width,
    uint Height,
    int Rotation,
    bool DesktopImageInSystemMemory);

/// <summary>Outcome of a single capture attempt.</summary>
public enum CaptureStatus
{
    /// <summary>New pixels were written to the destination.</summary>
    Captured = 0,
    /// <summary>No new frame within the timeout; the destination was not modified.</summary>
    NoNewFrame = 1,
    /// <summary>The backend needs to be rebuilt (e.g. DXGI access was lost). Call <see cref="ICaptureSource.Reinitialize"/>.</summary>
    NeedsReinitialize = 2,
    /// <summary>Hard failure; see <see cref="CaptureResult.Error"/>.</summary>
    Failed = 3,
}

public readonly record struct CaptureResult(CaptureStatus Status, string? Error = null, bool IsCached = false)
{
    public bool HasPixels => Status == CaptureStatus.Captured;
    public static readonly CaptureResult NoNewFrame = new(CaptureStatus.NoNewFrame);
}

/// <summary>
/// A per-monitor screen grabber that can fill an arbitrary sub-rectangle of its monitor.
/// </summary>
/// <remarks>
/// Implementations intentionally only ever copy the requested rectangle out of the
/// monitor image. A 4K monitor frame is ~33 MB; sampling a 128x128 patch around the
/// pointer at 60 Hz would otherwise move gigabytes per second for nothing.
/// </remarks>
public interface ICaptureSource : IDisposable
{
    /// <summary>Human-readable backend name for logs and the UI.</summary>
    string Name { get; }

    /// <summary>The monitor this source captures.</summary>
    MonitorDescriptor Monitor { get; }

    /// <summary>
    /// True when <b>pixels returned by this source include the composited cursor</b>.
    /// </summary>
    /// <remarks>
    /// This is the property that decides which role a backend plays in the solver:
    /// an including source yields the composite <c>D</c> (used as the primary
    /// observation), an excluding source yields a clean background plate <c>B</c>.
    /// </remarks>
    bool IncludesPointer { get; }

    /// <summary>
    /// Copies <paramref name="region"/> (desktop coordinates, must lie inside
    /// <see cref="Monitor"/>'s rectangle) into <paramref name="destination"/>, which must
    /// be exactly <c>region.Width x region.Height</c>.
    /// </summary>
    CaptureResult Capture(RECT region, ArgbBitmap destination, int timeoutMs);

    /// <summary>Rebuilds the backend after <see cref="CaptureStatus.NeedsReinitialize"/>.</summary>
    void Reinitialize();
}

/// <summary>Shared helpers for capture backends.</summary>
internal static class CaptureSupport
{
    /// <summary>
    /// Forces every pixel opaque.
    /// </summary>
    /// <remarks>
    /// Neither GDI <c>BitBlt</c> nor the Desktop Duplication desktop image defines the
    /// alpha byte: GDI leaves it at 0 and the duplication image may hold a constant
    /// 255. Screen content is opaque by definition, so normalising here keeps a
    /// meaningless channel from leaking into the solver and the PNG writer.
    /// </remarks>
    public static void ForceOpaque(ArgbBitmap bitmap)
    {
        var pixels = bitmap.Pixels;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }
    }

    /// <summary>Validates a capture request against a monitor.</summary>
    public static void ValidateRegion(MonitorDescriptor monitor, in RECT region, ArgbBitmap destination)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(region), $"{region} has non-positive size.");
        }
        if (!(region.Left >= monitor.Rect.Left && region.Top >= monitor.Rect.Top &&
              region.Right <= monitor.Rect.Right && region.Bottom <= monitor.Rect.Bottom))
        {
            throw new ArgumentOutOfRangeException(nameof(region),
                $"Region {region} is not fully inside monitor {monitor.Rect}.");
        }
        if (destination.Width != region.Width || destination.Height != region.Height)
        {
            throw new ArgumentException(
                $"Destination is {destination.Width}x{destination.Height} but region is {region.Width}x{region.Height}.",
                nameof(destination));
        }
    }
}
