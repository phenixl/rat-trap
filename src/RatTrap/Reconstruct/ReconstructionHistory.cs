using RatTrap.Interop;

namespace RatTrap.Reconstruct;

/// <summary>One recorded frame's metadata. The panels themselves are read from the ring.</summary>
internal readonly record struct HistoryFrame(
    long FrameIndex,
    RECT RoiRect,
    int HotspotX,
    int HotspotY,
    int FramesObserved,
    int CoveredPixels,
    int SolvedPixels,
    int AssumedPixels,
    int UnresolvedPixels,
    long PairsRecorded,
    int Width,
    int Height,
    int Scale)
{
    /// <summary>Pointer hotspot expressed in panel pixels rather than desktop pixels.</summary>
    public int PanelHotspotX => (HotspotX - RoiRect.Left) / Math.Max(1, Scale);

    public int PanelHotspotY => (HotspotY - RoiRect.Top) / Math.Max(1, Scale);

    public string Describe()
        => $"frame {FrameIndex}  observed {FramesObserved}  pairs {PairsRecorded}  " +
           $"covered {CoveredPixels} (solved {SolvedPixels}, assumed {AssumedPixels}, " +
           $"unresolved {UnresolvedPixels})  1/{Scale} scale";
}

/// <summary>
/// A bounded recording of the reconstruction's progress, for replay.
/// </summary>
/// <remarks>
/// <para>
/// The recording exists because "watch the process" and "watch the current state" are different requests. A
/// live view only ever shows now, and the interesting moments - the first time a pixel's alpha separates from
/// its background, the frame where the coverage crosses the evidence threshold - are over before anyone can
/// look at them.
/// </para>
/// <para>
/// Three limits, because any one of them alone is wrong. A block count decides the <b>scale</b>: a 1024-pixel
/// crosshair region cannot be stored at full resolution for three hundred frames, and trying would be a
/// gigabyte. A byte budget decides the <b>capacity</b>: a small region can afford more frames than a large
/// one. A frame cap keeps the <b>replay</b> navigable, because a recording of ten thousand frames is a
/// recording nobody scrubs through. The derived numbers are reported rather than hidden, so the cost of
/// watching is visible instead of implicit.
/// </para>
/// <para>
/// Fixed-size arrays are allocated once at the derived capacity. Nothing is allocated per frame, because a
/// recorder that allocates on every capture tick would be adding garbage to the path it is observing.
/// </para>
/// </remarks>
internal sealed class ReconstructionHistory
{
    /// <summary>Blocks per frame, which is what decides the scale factor.</summary>
    public const int MaxBlocksPerFrame = 16384;

    /// <summary>Default ceiling on recorded panel bytes.</summary>
    public const long DefaultMaxBytes = 32L * 1024 * 1024;

    /// <summary>Default ceiling on frames, so the replay stays navigable.</summary>
    public const int DefaultMaxFrames = 300;

    /// <summary>Composite RGB (3) + background RGB (3) + classification (1) + alpha (1).</summary>
    private const int BytesPerBlock = 8;

    private byte[] _composite = [];
    private byte[] _background = [];
    private byte[] _modelKind = [];
    private byte[] _modelAlpha = [];

    private long[] _frameIndex = [];
    private RECT[] _roiRect = [];
    private int[] _hotspotX = [];
    private int[] _hotspotY = [];
    private int[] _framesObserved = [];
    private int[] _covered = [];
    private int[] _solved = [];
    private int[] _assumed = [];
    private int[] _unresolved = [];
    private long[] _pairs = [];

    private int _next;

    /// <summary>Whether a capture should record frames into this ring at all.</summary>
    /// <remarks>
    /// Gated so that the cost is only paid while someone is actually looking. An observation feature that
    /// quietly records during every capture would be spending memory to answer a question nobody asked.
    /// </remarks>
    public bool Enabled { get; set; }

    public int Capacity { get; private set; }

    public int Count { get; private set; }

    public int Scale { get; private set; } = 1;

    public int BlockWidth { get; private set; }

    public int BlockHeight { get; private set; }

    public int Blocks => BlockWidth * BlockHeight;

    public long TotalRecorded { get; private set; }

    /// <summary>Bytes the ring currently holds, which is its capacity times its per-frame size.</summary>
    public long Bytes => (long)Capacity * Blocks * BytesPerBlock;

    public int NewestIndex => Count - 1;

    /// <summary>Configures the ring for a captured region of the given size. Reallocates when it changes.</summary>
    /// <returns>True when the geometry changed.</returns>
    public bool Configure(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        int scale = 1;
        while (((width + scale - 1) / scale) * ((height + scale - 1) / scale) > MaxBlocksPerFrame)
        {
            scale *= 2;
        }

        int blockWidth = (width + scale - 1) / scale;
        int blockHeight = (height + scale - 1) / scale;
        if (scale == Scale && blockWidth == BlockWidth && blockHeight == BlockHeight)
        {
            return false;
        }

        Scale = scale;
        BlockWidth = blockWidth;
        BlockHeight = blockHeight;

        long bytesPerFrame = Math.Max(1, (long)Blocks * BytesPerBlock);
        Capacity = (int)Math.Clamp(Math.Min(DefaultMaxFrames, DefaultMaxBytes / bytesPerFrame), 1, DefaultMaxFrames);

        _composite = new byte[(long)Capacity * Blocks * 3];
        _background = new byte[(long)Capacity * Blocks * 3];
        _modelKind = new byte[(long)Capacity * Blocks];
        _modelAlpha = new byte[(long)Capacity * Blocks];

        _frameIndex = new long[Capacity];
        _roiRect = new RECT[Capacity];
        _hotspotX = new int[Capacity];
        _hotspotY = new int[Capacity];
        _framesObserved = new int[Capacity];
        _covered = new int[Capacity];
        _solved = new int[Capacity];
        _assumed = new int[Capacity];
        _unresolved = new int[Capacity];
        _pairs = new long[Capacity];

        _next = 0;
        Count = 0;
        return true;
    }

    public void Clear()
    {
        _next = 0;
        Count = 0;
        TotalRecorded = 0;
    }

    /// <summary>
    /// Records one frame, downsampled to the ring's block grid.
    /// </summary>
    /// <returns>True when the frame was stored; false when the ring is disabled or unconfigured.</returns>
    public bool Record(ReconstructionView view, long frameIndex, int hotspotX, int hotspotY)
    {
        if (!Enabled || view.Composite is null || view.Background is null || view.Model is null)
        {
            return false;
        }

        Configure(view.Width, view.Height);

        int slot = _next;
        long panelBase = (long)slot * Blocks;
        long rgbBase = panelBase * 3;
        int scale = Scale;

        Span<int> histogram = stackalloc int[5];
        for (int by = 0; by < BlockHeight; by++)
        {
            for (int bx = 0; bx < BlockWidth; bx++)
            {
                int block = (int)(panelBase + (by * BlockWidth) + bx);
                int rgb = (int)(rgbBase + (((long)by * BlockWidth + bx) * 3));

                long sumB = 0, sumG = 0, sumR = 0;
                long sumBgB = 0, sumBgG = 0, sumBgR = 0;
                long sumAlpha = 0;
                int votes = 0;
                histogram.Clear();

                int x0 = bx * scale;
                int y0 = by * scale;
                int x1 = Math.Min(x0 + scale, view.Width);
                int y1 = Math.Min(y0 + scale, view.Height);

                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        int cell = (y * view.Width) + x;
                        int pixel = cell * 4;

                        byte kind = view.ModelKind.Length > cell ? view.ModelKind[cell] : (byte)255;
                        int bucket = kind <= 4 ? kind : 4;
                        histogram[bucket]++;

                        sumB += view.Composite.Pixels[pixel];
                        sumG += view.Composite.Pixels[pixel + 1];
                        sumR += view.Composite.Pixels[pixel + 2];
                        sumBgB += view.Background.Pixels[pixel];
                        sumBgG += view.Background.Pixels[pixel + 1];
                        sumBgR += view.Background.Pixels[pixel + 2];
                        sumAlpha += view.ModelAlpha.Length > cell ? view.ModelAlpha[cell] : (byte)0;
                        votes++;
                    }
                }

                if (votes == 0)
                {
                    continue;
                }

                // Majority classification, because at 1/8 scale a single sampled pixel would let one outlier
                // decide what a 64-pixel block is reported as.
                int best = 0;
                for (int kind = 1; kind < histogram.Length; kind++)
                {
                    if (histogram[kind] > histogram[best])
                    {
                        best = kind;
                    }
                }

                _composite[rgb] = (byte)(sumB / votes);
                _composite[rgb + 1] = (byte)(sumG / votes);
                _composite[rgb + 2] = (byte)(sumR / votes);
                _background[rgb] = (byte)(sumBgB / votes);
                _background[rgb + 1] = (byte)(sumBgG / votes);
                _background[rgb + 2] = (byte)(sumBgR / votes);
                _modelKind[block] = (byte)best;
                _modelAlpha[block] = (byte)(sumAlpha / votes);
            }
        }

        _frameIndex[slot] = frameIndex;
        _roiRect[slot] = view.RoiRect;
        _hotspotX[slot] = hotspotX;
        _hotspotY[slot] = hotspotY;
        _framesObserved[slot] = view.FramesObserved;
        _covered[slot] = view.CoveredPixels;
        _solved[slot] = view.SolvedPixels;
        _assumed[slot] = view.AssumedPixels;
        _unresolved[slot] = view.UnresolvedPixels;
        _pairs[slot] = view.PairsRecorded;

        _next = (_next + 1) % Capacity;
        if (Count < Capacity)
        {
            Count++;
        }
        TotalRecorded++;
        return true;
    }

    /// <summary>Reads a frame by position from the oldest, 0-based.</summary>
    public bool TryGetFrame(int indexFromOldest, out HistoryFrame frame)
    {
        frame = default;
        if (indexFromOldest < 0 || indexFromOldest >= Count)
        {
            return false;
        }

        int slot = Slot(indexFromOldest);
        frame = new HistoryFrame(
            _frameIndex[slot], _roiRect[slot], _hotspotX[slot], _hotspotY[slot],
            _framesObserved[slot], _covered[slot], _solved[slot], _assumed[slot], _unresolved[slot],
            _pairs[slot], BlockWidth, BlockHeight, Scale);
        return true;
    }

    /// <summary>Composite panel of a frame, three bytes per block.</summary>
    public ReadOnlySpan<byte> CompositeOf(int indexFromOldest)
        => _composite.AsSpan(Slot(indexFromOldest) * Blocks * 3, Blocks * 3);

    /// <summary>Background panel of a frame, three bytes per block.</summary>
    public ReadOnlySpan<byte> BackgroundOf(int indexFromOldest)
        => _background.AsSpan(Slot(indexFromOldest) * Blocks * 3, Blocks * 3);

    /// <summary>Classification panel of a frame, one byte per block, 255 outside the model.</summary>
    public ReadOnlySpan<byte> ModelKindOf(int indexFromOldest)
        => _modelKind.AsSpan(Slot(indexFromOldest) * Blocks, Blocks);

    /// <summary>Recovered alpha panel of a frame, one byte per block.</summary>
    public ReadOnlySpan<byte> ModelAlphaOf(int indexFromOldest)
        => _modelAlpha.AsSpan(Slot(indexFromOldest) * Blocks, Blocks);

    /// <summary>Maps a position from the oldest to a ring slot. Only valid after a bounds check.</summary>
    private int Slot(int indexFromOldest) => ((_next - Count + indexFromOldest) % Capacity + Capacity) % Capacity;
}
