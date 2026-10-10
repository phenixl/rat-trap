using RatTrap.Imaging;
using RatTrap.Interop;

namespace RatTrap.Reconstruct;

/// <summary>Tuning for <see cref="CursorReconstructor"/>.</summary>
public sealed record ReconstructionOptions
{
    /// <summary>
    /// How far from the hotspot, in pixels, the cursor footprint is assumed to extend.
    /// Sets the size of the cursor-local offset grid (side = <c>2*R+1</c>).
    /// </summary>
    public int MaxOffsetRadius { get; init; } = 96;

    /// <summary>
    /// Per-channel absolute difference above which an observation is treated as foreground
    /// rather than background.
    /// </summary>
    /// <remarks>
    /// Too low and capture noise is mistaken for cursor coverage, which injects pairs whose
    /// composite is not actually a composite and biases the slope. Too high and the faint
    /// anti-aliased fringe of the cursor is treated as background, which biases the
    /// background model towards the cursor. 10/255 sits comfortably above the noise floor of
    /// both GDI and duplication capture on this machine.
    /// </remarks>
    public int ForegroundGate { get; init; } = 10;

    /// <summary>
    /// Number of recent samples per screen pixel used for the median background estimate.
    /// </summary>
    /// <remarks>
    /// This is the parameter that decides how much of the time a pixel may be covered before its
    /// background estimate degrades. A cursor occupies a small part of the area it traverses, so
    /// a window of 15 observations leaves the substantial majority of samples as genuine
    /// background. Larger windows are more robust but adapt more slowly after a scene change.
    /// </remarks>
    public int BackgroundHistoryLength { get; init; } = 15;

    /// <summary>Minimum samples in the window before a background estimate is used at all.</summary>
    public int MinBackgroundSamples { get; init; } = 5;

    /// <summary>
    /// Fraction of the window that must sit within <see cref="ForegroundGate"/> of the median for
    /// the estimate to count as confirmed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately a <b>strict majority</b> (above 0.5), because that is exactly the condition
    /// under which "the median is the background" is a valid statement. With a lower threshold a
    /// window holding, say, 8 cursor samples and 7 background samples passes the check while its
    /// median <i>is</i> the cursor; the estimate is then adopted as background and every later
    /// genuine background sample is paired against it inverted, producing a single phantom pair
    /// whose XOR difference is indistinguishable from a real masked-cursor pixel. That produced
    /// a spurious checkerboard of phantom pixels beside the synthetic cross.
    /// </para>
    /// <para>
    /// The cost is that a pixel the cursor <i>parks</i> on for most of the observation is reported
    /// as uncovered rather than as coverage. That is the conservative direction to fail in, and
    /// the report counts those pixels so the situation is visible instead of silent.
    /// </para>
    /// </remarks>
    public double MinBackgroundInlierFraction { get; init; } = 0.55;

    /// <summary>
    /// Minimum fraction of the times an offset was observed at which it looked covered, before that
    /// offset is treated as part of the cursor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the guard that separates a glyph from noise, and it is deliberately about information
    /// rather than about shape. A real cursor covers a given offset <b>every</b> time the pointer puts
    /// that offset on screen, so its coverage rate is near 1. Residue from a background model that was
    /// briefly wrong shows up as a handful of pairs against hundreds of ordinary observations.
    /// </para>
    /// <para>
    /// Measured on real hardware, which is where this became necessary: a desktop where nothing draws a
    /// cursor at all - duplication excludes the system cursor - still yielded 402 "covered" pixels
    /// scattered over a 160x162 box, because a scrolling terminal periodically disagreed with its own
    /// background estimate. Those offsets had a coverage rate around 0.2%, and the result was emitted as
    /// a cursor. Shape-based alternatives were all rejected: a crosshair is sparse, a move cursor is four
    /// disconnected arrows, and a hotspot can sit in a gap at the centre of its glyph, so any test on the
    /// geometry of the answer rejects legitimate cursors.
    /// </para>
    /// </remarks>
    public double MinCoverageRate { get; init; } = 0.25;

    /// <summary>
    /// Minimum pairs before a cursor-local offset is treated as part of the cursor at all.
    /// </summary>
    /// <remarks>
    /// A single pair can arise from a background estimate that was briefly wrong, and it is not
    /// evidence of coverage. Without this floor, one such pair extends the recovered cursor's
    /// bounding box by twenty pixels and the hotspot with it, so the output is not merely noisy
    /// but mispositioned.
    /// </remarks>
    public int MinCoverageEvidence { get; init; } = 3;

    /// <summary>Frames to skip accumulating pairs after a detected scene change.</summary>
    public int SceneChangeSettleFrames { get; init; } = 3;

    /// <summary>
    /// Fraction of tracked pixels that must change at once for the frame to count as a scene
    /// change (which invalidates the background model).
    /// </summary>
    public double SceneChangeFraction { get; init; } = 0.40;

    /// <summary>Minimum (background, composite) pairs before a pixel's model is fitted.</summary>
    public int MinPairs { get; init; } = 6;

    /// <summary>
    /// Minimum summed background variance across the three channels for the slope to be
    /// considered identified. Below this the background simply did not vary enough under the
    /// cursor, and alpha is not recoverable at that pixel.
    /// </summary>
    /// <remarks>
    /// Units are (8-bit level)^2 summed over three channels, so 300 is about a standard
    /// deviation of 10 levels per channel - enough for the slope to be meaningful.
    /// </remarks>
    public double MinBackgroundVariance { get; init; } = 300.0;

    /// <summary>Composite variance below which a pixel is called fully opaque.</summary>
    public double MaxOpaqueVariance { get; init; } = 4.0;

    /// <summary>Fraction of pairs that must agree on a constant XOR difference to classify a pixel as XOR-composited.</summary>
    public double XorAgreementThreshold { get; init; } = 0.95;

    /// <summary>Alpha below which a pixel is written out as fully transparent.</summary>
    public double TransparentAlphaThreshold { get; init; } = 0.02;

    /// <summary>Alpha above which a pixel is written out as fully opaque.</summary>
    public double OpaqueAlphaThreshold { get; init; } = 0.98;

    public static readonly ReconstructionOptions Default = new();

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxOffsetRadius, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxOffsetRadius, 512);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinPairs, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(BackgroundHistoryLength, 5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BackgroundHistoryLength, 63);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinBackgroundSamples, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MinBackgroundSamples, BackgroundHistoryLength);
    }
}

/// <summary>How a pixel's composite model was determined.</summary>
public enum PixelModelKind
{
    /// <summary>Never observed as foreground: the cursor does not cover this offset.</summary>
    Transparent = 0,
    /// <summary>Alpha-blended, alpha and colour recovered from the fitted slope.</summary>
    AlphaSolved = 1,
    /// <summary>Composite is constant regardless of background: treated as opaque.</summary>
    OpaqueAssumed = 2,
    /// <summary>Composite is the background XORed with a constant mask.</summary>
    XorMasked = 3,
    /// <summary>Not enough background variation or too few pairs: alpha is unknown.</summary>
    Unresolved = 4,
}

/// <summary>Per-offset outcome, for reporting and for diagnostics overlays.</summary>
public readonly record struct PixelModel(
    PixelModelKind Kind,
    double Alpha,
    ArgbPixel Colour,
    double Confidence)
{
    public static readonly PixelModel Transparent = new(PixelModelKind.Transparent, 0, ArgbPixel.Transparent, 1.0);
}

/// <summary>Summary of a reconstruction run.</summary>
public sealed record ReconstructionReport
{
    public required int FramesObserved { get; init; }
    public required int FramesAccumulated { get; init; }
    public required int SceneChanges { get; init; }
    public required int OffsetCells { get; init; }
    public required int TransparentPixels { get; init; }
    public required int AlphaSolvedPixels { get; init; }
    public required int OpaqueAssumedPixels { get; init; }
    public required int XorMaskedPixels { get; init; }
    public required int UnresolvedPixels { get; init; }
    public required int PairsRecorded { get; init; }
    public required RECT AccumulationBounds { get; init; }
    public required POINT HotspotMin { get; init; }
    public required POINT HotspotMax { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    public int CoveredPixels => AlphaSolvedPixels + OpaqueAssumedPixels + XorMaskedPixels + UnresolvedPixels;

    /// <summary>
    /// Fraction of covered pixels whose alpha and colour were actually solved from background
    /// variation, rather than assumed.
    /// </summary>
    public double SolvedFraction => CoveredPixels == 0 ? 0 : (double)AlphaSolvedPixels / CoveredPixels;

    public string Describe()
    {
        var lines = new List<string>
        {
            $"frames observed={FramesObserved} accumulated={FramesAccumulated} sceneChanges={SceneChanges}",
            $"cursor-local cells={OffsetCells} covered={CoveredPixels}",
            $"  alpha solved    = {AlphaSolvedPixels} ({SolvedFraction:P1} of covered)",
            $"  opaque assumed  = {OpaqueAssumedPixels}",
            $"  XOR masked      = {XorMaskedPixels}",
            $"  unresolved      = {UnresolvedPixels}",
            $"  transparent     = {TransparentPixels}",
            $"pairs recorded    = {PairsRecorded}",
            $"accumulation area = {AccumulationBounds}",
            $"hotspot range     = {HotspotMin} .. {HotspotMax}",
        };
        lines.AddRange(Notes.Select(n => $"note: {n}"));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>A reconstructed cursor plus everything needed to judge how much to trust it.</summary>
public sealed record ReconstructionResult(CursorImage? Cursor, ReconstructionReport Report)
{
    public bool Success => Cursor is not null;

    /// <summary>
    /// Per-pixel classification of the recovered image, row-major, sized
    /// <see cref="MapWidth"/> x <see cref="MapHeight"/>.
    /// </summary>
    /// <remarks>
    /// Exposed so that a caller can draw a diagnostics overlay - which pixels were solved from
    /// background variation, which were assumed opaque, and which could not be determined - and
    /// because that view is the only practical way to debug a reconstruction that looks wrong.
    /// It is also what pinned down the spurious-pair bug in the synthetic XOR case.
    /// </remarks>
    public IReadOnlyList<PixelModelKind> KindMap { get; init; } = [];

    public int MapWidth { get; init; }

    public int MapHeight { get; init; }

    /// <summary>Cursor-local offset of the top-left pixel of <see cref="KindMap"/>.</summary>
    public int MapOriginRx { get; init; }

    public int MapOriginRy { get; init; }

    /// <summary>Renders <see cref="KindMap"/> as ASCII, one character per pixel.</summary>
    public string DescribeKindMap()
    {
        if (KindMap.Count == 0 || MapWidth <= 0)
        {
            return "(no model map)";
        }

        var builder = new System.Text.StringBuilder();
        for (int y = 0; y < MapHeight; y++)
        {
            for (int x = 0; x < MapWidth; x++)
            {
                builder.Append(KindMap[(y * MapWidth) + x] switch
                {
                    PixelModelKind.Transparent => '.',
                    PixelModelKind.AlphaSolved => 'a',
                    PixelModelKind.OpaqueAssumed => 'O',
                    PixelModelKind.XorMasked => 'X',
                    _ => '?',
                });
            }
            builder.AppendLine();
        }
        return builder.ToString();
    }
}

