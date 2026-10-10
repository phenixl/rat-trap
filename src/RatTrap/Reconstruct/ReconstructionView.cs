using System.Diagnostics;
using RatTrap.Imaging;
using RatTrap.Interop;

namespace RatTrap.Reconstruct;

/// <summary>
/// A refreshable, colour-coded picture of what the reconstruction currently believes.
/// </summary>
/// <remarks>
/// <para>
/// This is the data half of the observation view: it owns the panel bitmaps and knows how to fill them from a
/// live reconstructor. It knows nothing about windows, drawing or input. Keeping it here rather than in the UI
/// means the property that actually matters - that the model fills in progressively, frame by frame - can be
/// asserted by a test with no window, no capture device and nobody looking at it.
/// </para>
/// <para>
/// All three panels share one grid: the captured region. That is a deliberate choice, not a convenience. The
/// model is accumulated in cursor-local offsets, so the same offset means a different screen pixel from one
/// frame to the next; sampling it through the current pointer position puts all three panels in the same
/// coordinates. That is what makes a replay comparable frame against frame, and what keeps a recording a fixed
/// size. The price is that the model panel shows the model's opinion of the pixels under the pointer rather
/// than the whole accumulated glyph at once.
/// </para>
/// <para>
/// Every buffer is allocated once and reused. Allocating three bitmaps per refresh would be a few hundred
/// kilobytes of garbage several times a second, for a view that only exists while someone is watching it.
/// </para>
/// </remarks>
internal sealed class ReconstructionView
{
    /// <summary>Refreshes between recomputations of the background panel.</summary>
    /// <remarks>
    /// The background panel is the expensive one: it asks for a per-pixel median over the whole region, which
    /// is thousands of small sorts. It is also the slowest-moving thing on screen, because a median over a
    /// fifteen-sample window barely changes between one frame and the next, so recomputing it every refresh
    /// would spend most of the view's budget redrawing a picture that is already right.
    /// </remarks>
    public const int BackgroundRefreshInterval = 4;

    /// <summary>Panel width, matching the captured region.</summary>
    public int Width => Composite?.Width ?? 0;

    public int Height => Composite?.Height ?? 0;

    /// <summary>The captured region as it arrived, cursor included.</summary>
    public ArgbBitmap? Composite { get; private set; }

    /// <summary>The model's background estimate over the same region.</summary>
    public ArgbBitmap? Background { get; private set; }

    /// <summary>Classification of each pixel, tinted by recovered alpha, at its offset from the pointer.</summary>
    public ArgbBitmap? Model { get; private set; }

    public RECT RoiRect { get; private set; }

    public int FramesObserved { get; private set; }

    public int CoveredPixels { get; private set; }

    public int SolvedPixels { get; private set; }

    public int AssumedPixels { get; private set; }

    public int UnresolvedPixels { get; private set; }

    public long PairsRecorded { get; private set; }

    /// <summary>
    /// Cursor-local offsets the model now covers.
    /// </summary>
    /// <remarks>
    /// This is the progress measure: it only grows as the pointer travels, so "the model is filling in" is this
    /// number rising. It is distinct from <see cref="ModelledPixels"/>, which is a geometric intersection and
    /// therefore goes down again whenever the pointer moves the region away from the model.
    /// </remarks>
    public int ModelCells { get; private set; }

    /// <summary>
    /// Panel pixels that happened to fall inside the model on this refresh.
    /// </summary>
    /// <remarks>
    /// A geometry figure, not a progress figure: it is the intersection of the panel with the model's extent, so
    /// it rises and falls as the pointer moves. Reported because it says how much of what you are looking at is
    /// currently accounted for, which is useful, but it must not be read as progress.
    /// </remarks>
    public int ModelledPixels { get; private set; }

    /// <summary>True when the solver produced a cursor on the last refresh.</summary>
    public bool HasCursor { get; private set; }

    public string Summary { get; private set; } = "no capture in progress";

    /// <summary>Wall-clock milliseconds the last refresh took, for the diagnostic readout.</summary>
    public double LastRefreshMilliseconds { get; private set; }

    /// <summary>Milliseconds the last background-panel rebuild took, or 0 when it was skipped.</summary>
    public double LastBackgroundMilliseconds { get; private set; }

    /// <summary>How many refreshes have rebuilt the background panel.</summary>
    public long BackgroundRebuilds { get; private set; }

    public long Refreshes { get; private set; }

    /// <summary>
    /// Raw classification of the model panel, one byte per pixel, or 255 where the model does not reach.
    /// </summary>
    /// <remarks>
    /// The panel bitmaps carry this information as colours, which cannot be read back. Exposing the arrays
    /// beside them means the recorder does not have to re-derive the offset mapping to store a frame; the
    /// mapping from cursor-local offsets to panel pixels exists in exactly one place.
    /// </remarks>
    public byte[] ModelKind { get; private set; } = [];

    /// <summary>Raw recovered alpha of the model panel, one byte per pixel, 0 where the model does not reach.</summary>
    public byte[] ModelAlpha { get; private set; } = [];

    /// <summary>
    /// Rebuilds the panels from the live reconstruction state.
    /// </summary>
    /// <param name="roi">The captured region, or null when nothing is being captured.</param>
    /// <param name="roiRect">Where <paramref name="roi"/> came from, in desktop coordinates.</param>
    /// <param name="reconstructor">The solver to read, or null when no capture is running.</param>
    /// <param name="hotspotX">Pointer hotspot X in desktop coordinates.</param>
    /// <param name="hotspotY">Pointer hotspot Y in desktop coordinates.</param>
    /// <returns>True when there was something to show.</returns>
    /// <remarks>
    /// Reading only: every accessor it uses is documented as not changing the model. That matters more than it
    /// sounds. If drawing the picture advanced the background window, then watching a reconstruction would
    /// change it, and what is on screen would no longer be what would have happened without a viewer.
    /// </remarks>
    public bool Refresh(ArgbBitmap? roi, RECT roiRect, CursorReconstructor? reconstructor,
        int hotspotX, int hotspotY)
    {
        long started = Stopwatch.GetTimestamp();
        Refreshes++;

        if (roi is null || reconstructor is null || roi.Width <= 0 || roi.Height <= 0)
        {
            Summary = "no capture in progress";
            HasCursor = false;
            Composite = null;
            Background = null;
            Model = null;
            LastRefreshMilliseconds = Elapsed(started);
            return false;
        }

        RoiRect = roiRect;
        FramesObserved = reconstructor.FramesObserved;

        Composite = Resize(Composite, roi.Width, roi.Height);
        roi.AsSpan().CopyTo(Composite.AsSpan());

        long backgroundStarted = Stopwatch.GetTimestamp();
        LastBackgroundMilliseconds = 0;
        bool rebuildBackground = Background is null ||
                                 Background.Width != roi.Width ||
                                 Background.Height != roi.Height ||
                                 Refreshes % BackgroundRefreshInterval == 1;
        if (rebuildBackground)
        {
            Background = Resize(Background, roi.Width, roi.Height);
            FillBackground(Background, roiRect, reconstructor);
            LastBackgroundMilliseconds = Elapsed(backgroundStarted);
            BackgroundRebuilds++;
        }

        ReconstructionResult result = reconstructor.Solve();
        ReconstructionReport report = result.Report;
        CoveredPixels = report.CoveredPixels;
        SolvedPixels = report.AlphaSolvedPixels + report.XorMaskedPixels;
        AssumedPixels = report.OpaqueAssumedPixels;
        UnresolvedPixels = report.UnresolvedPixels;
        PairsRecorded = report.PairsRecorded;
        ModelCells = report.OffsetCells;
        HasCursor = result.Cursor is not null;

        Model = Resize(Model, roi.Width, roi.Height);
        if (ModelKind.Length != roi.Width * roi.Height)
        {
            ModelKind = new byte[roi.Width * roi.Height];
            ModelAlpha = new byte[roi.Width * roi.Height];
        }
        FillModel(Model, result, hotspotX - roiRect.Left, hotspotY - roiRect.Top);

        Summary = $"{report.FramesObserved} frames, {report.PairsRecorded} pairs, " +
                  $"{ModelledPixels} pixels under model: {report.AlphaSolvedPixels} solved, " +
                  $"{report.OpaqueAssumedPixels} assumed, {report.UnresolvedPixels} unresolved";

        LastRefreshMilliseconds = Elapsed(started);
        return true;
    }

    /// <summary>Paints the model's estimate for each pixel, and marks the ones with no samples.</summary>
    /// <remarks>
    /// A pixel with no estimate is drawn as near-black rather than as the last known value, so that "the model
    /// has not seen this yet" is visible instead of being disguised as "the background is black here".
    /// </remarks>
    private static void FillBackground(ArgbBitmap panel, RECT roiRect, CursorReconstructor reconstructor)
    {
        Span<byte> pixels = panel.AsSpan();
        for (int y = 0; y < panel.Height; y++)
        {
            int row = y * panel.Stride;
            int screenY = roiRect.Top + y;
            for (int x = 0; x < panel.Width; x++)
            {
                int offset = row + (x * 4);
                if (reconstructor.TryGetBackgroundEstimate(roiRect.Left + x, screenY, out ArgbPixel estimate, out _))
                {
                    pixels[offset] = estimate.B;
                    pixels[offset + 1] = estimate.G;
                    pixels[offset + 2] = estimate.R;
                }
                else
                {
                    pixels[offset] = 0x12;
                    pixels[offset + 1] = 0x12;
                    pixels[offset + 2] = 0x12;
                }
                pixels[offset + 3] = 255;
            }
        }
    }

    /// <summary>Paints each pixel by how the model classified it and how transparent it turned out to be.</summary>
    /// <remarks>
    /// Hue is the classification and brightness is the recovered alpha, so one panel answers both questions:
    /// which pixels the solver thinks are cursor, and how much background shows through each of them. Alpha as
    /// brightness is the point - the alpha channel filling in as the run proceeds is what "progressively
    /// completed" looks like.
    /// </remarks>
    private void FillModel(ArgbBitmap panel, ReconstructionResult result, int hotspotInRoiX, int hotspotInRoiY)
    {
        Span<byte> pixels = panel.AsSpan();
        ModelledPixels = 0;

        if (result.MapWidth <= 0 || result.KindMap.Count == 0)
        {
            // Nothing solved yet, or the solver refused. A flat void beats leaving the previous frame's picture
            // on screen, which would read as "this is the current state" when it is not.
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 0x18;
                pixels[i + 1] = 0x18;
                pixels[i + 2] = 0x18;
                pixels[i + 3] = 255;
            }
            Array.Fill(ModelKind, (byte)255);
            Array.Clear(ModelAlpha);
            return;
        }

        bool hasAlpha = result.AlphaMap.Count == result.KindMap.Count;

        for (int y = 0; y < panel.Height; y++)
        {
            int row = y * panel.Stride;
            int ry = y - hotspotInRoiY;
            for (int x = 0; x < panel.Width; x++)
            {
                int offset = row + (x * 4);
                int rx = x - hotspotInRoiX;

                int mapX = rx - result.MapOriginRx;
                int mapY = ry - result.MapOriginRy;
                int cell = (y * panel.Width) + x;
                if (mapX < 0 || mapY < 0 || mapX >= result.MapWidth || mapY >= result.MapHeight)
                {
                    pixels[offset] = 0x18;
                    pixels[offset + 1] = 0x18;
                    pixels[offset + 2] = 0x18;
                    pixels[offset + 3] = 255;
                    ModelKind[cell] = 255;
                    ModelAlpha[cell] = 0;
                    continue;
                }

                int index = (mapY * result.MapWidth) + mapX;
                PixelModelKind kind = result.KindMap[index];
                byte alpha = hasAlpha ? result.AlphaMap[index] : (byte)255;
                ModelledPixels++;
                ModelKind[cell] = (byte)kind;
                ModelAlpha[cell] = alpha;

                ArgbPixel colour = PanelColour(kind, alpha);
                pixels[offset] = colour.B;
                pixels[offset + 1] = colour.G;
                pixels[offset + 2] = colour.R;
                pixels[offset + 3] = 255;
            }
        }
    }

    /// <summary>
    /// The colour the model panel paints a classification and a recovered alpha in.
    /// </summary>
    /// <remarks>
    /// Shared with the window, which has to paint the same thing when it replays a recorded frame. The replayed
    /// frames are stored as classification and alpha bytes, not as colours, precisely so that this mapping
    /// exists in one place: a replay painted by a second copy of the mapping would drift from the live view the
    /// first time either one changed, and a diagnostics view that disagrees with itself is worse than none.
    /// </remarks>
    public static ArgbPixel PanelColour(PixelModelKind kind, byte alpha)
    {
        (byte b, byte g, byte r) = kind switch
        {
            PixelModelKind.AlphaSolved => ((byte)0x40, (byte)0xE0, (byte)0x40),   // green
            PixelModelKind.XorMasked => ((byte)0xE0, (byte)0xD0, (byte)0x30),     // cyan
            PixelModelKind.OpaqueAssumed => ((byte)0x40, (byte)0x90, (byte)0xF0), // orange
            PixelModelKind.Unresolved => ((byte)0x50, (byte)0x50, (byte)0xE0),    // red
            _ => ((byte)0x30, (byte)0x30, (byte)0x30),                            // not covered
        };

        if (kind == PixelModelKind.Transparent)
        {
            return new ArgbPixel(b, g, r, 255);
        }

        // Brightness is alpha, with a floor so an almost-transparent edge is still visible as an edge rather
        // than vanishing into the panel background.
        int brightness = 40 + ((alpha * 215) / 255);
        return new ArgbPixel((byte)((b * brightness) / 255), (byte)((g * brightness) / 255),
            (byte)((r * brightness) / 255), 255);
    }

    /// <summary>Reuses a panel bitmap when it is already the right size, and allocates one when it is not.</summary>
    private static ArgbBitmap Resize(ArgbBitmap? panel, int width, int height)
        => panel is not null && panel.Width == width && panel.Height == height
            ? panel
            : new ArgbBitmap(width, height);

    private static double Elapsed(long started)
        => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
}




