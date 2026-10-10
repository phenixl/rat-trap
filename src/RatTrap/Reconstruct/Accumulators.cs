using RatTrap.Imaging;
using RatTrap.Interop;

namespace RatTrap.Reconstruct;

/// <summary>Outcome of feeding one observation into the background model.</summary>
internal enum BackgroundObservation
{
    /// <summary>
    /// Too few samples to estimate a background yet. The observation must not be used as a
    /// composite, because there is nothing trustworthy to pair it with.
    /// </summary>
    Insufficient = 0,
    /// <summary>The sample is consistent with the estimated background.</summary>
    Background = 1,
    /// <summary>The sample differs from the estimated background: cursor coverage.</summary>
    Foreground = 2,
}

/// <summary>
/// Per-screen-pixel background estimate, taken as the median of a bounded history.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a median and not a low-pass filter.</b> The first implementation used a gated IIR
/// filter: fold a sample in when it is close to the current estimate, refuse it otherwise.
/// That fails in a way that is invisible until you look at the recovered geometry. Consider a
/// pixel whose <i>first</i> observation happens to be cursor coverage. An opaque cursor gives
/// the same value regardless of what is behind it, so the very next observation also matches,
/// the estimate is declared confirmed, and the filter has now confidently adopted <b>the
/// cursor</b> as the background. Every later sample from that pixel is then paired as
/// (cursor, background) - exactly inverted - and least squares quietly fits a slope to it. The
/// symptom was a recovered hotspot at offset 33 instead of 3: pairs were being recorded across
/// the entire region instead of on the glyph.
/// </para>
/// <para>
/// A median over a sliding window has no such failure mode. The cursor occupies a small part
/// of the area it traverses, so for any given screen pixel the majority of recent samples are
/// genuine background, and the median selects them no matter which sample came first. It also
/// adapts to a permanent local change by itself, because stale samples simply age out, and it
/// needs no tuning of a learning rate.
/// </para>
/// <para>
/// The estimate is only declared trustworthy when enough of the window agrees with it, which is
/// the check that stops a pixel the cursor <i>parks</i> on from being mistaken for background.
/// </para>
/// </remarks>
internal sealed class BackgroundModel
{
    private readonly byte[] _historyB;
    private readonly byte[] _historyG;
    private readonly byte[] _historyR;
    private readonly byte[] _count;
    private readonly byte[] _head;

    private readonly int _historyLength;

    public BackgroundModel(RECT bounds, int historyLength = 15)
        : this(bounds, historyLength, null)
    {
    }

    /// <summary>
    /// Creates a model over <paramref name="bounds"/>, carrying over whatever the
    /// <paramref name="existing"/> model already knew about the overlapping area.
    /// </summary>
    /// <remarks>
    /// The tracked screen area grows as the pointer wanders, so the model has to grow with it.
    /// Re-seeding from scratch on every growth would throw away a converged background exactly
    /// when the user starts moving - which is when the data is most valuable - so the overlap
    /// is copied instead.
    /// </remarks>
    public BackgroundModel(RECT bounds, int historyLength, BackgroundModel? existing)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), $"{bounds} has non-positive size.");
        }

        _historyLength = Math.Clamp(historyLength, 5, 63);
        Bounds = bounds;

        int cells = checked(bounds.Width * bounds.Height);
        _historyB = new byte[checked(cells * _historyLength)];
        _historyG = new byte[_historyB.Length];
        _historyR = new byte[_historyB.Length];
        _count = new byte[cells];
        _head = new byte[cells];

        if (existing is null || existing._count.Length == 0 || existing._historyLength != _historyLength)
        {
            return;
        }

        RECT overlap = Intersect(bounds, existing.Bounds);
        if (overlap.Width <= 0 || overlap.Height <= 0)
        {
            return;
        }

        int rowBytes = overlap.Width * _historyLength;
        for (int y = 0; y < overlap.Height; y++)
        {
            int destinationCell = ((overlap.Top + y - bounds.Top) * bounds.Width) + (overlap.Left - bounds.Left);
            int sourceCell = ((overlap.Top + y - existing.Bounds.Top) * existing.Bounds.Width) + (overlap.Left - existing.Bounds.Left);

            Array.Copy(existing._historyB, sourceCell * _historyLength, _historyB, destinationCell * _historyLength, rowBytes);
            Array.Copy(existing._historyG, sourceCell * _historyLength, _historyG, destinationCell * _historyLength, rowBytes);
            Array.Copy(existing._historyR, sourceCell * _historyLength, _historyR, destinationCell * _historyLength, rowBytes);
            Array.Copy(existing._count, sourceCell, _count, destinationCell, overlap.Width);
            Array.Copy(existing._head, sourceCell, _head, destinationCell, overlap.Width);
        }
    }

    private static RECT Intersect(RECT a, RECT b) => new()
    {
        Left = Math.Max(a.Left, b.Left),
        Top = Math.Max(a.Top, b.Top),
        Right = Math.Min(a.Right, b.Right),
        Bottom = Math.Min(a.Bottom, b.Bottom),
    };

    public RECT Bounds { get; }

    /// <summary>Number of screen pixels that have enough samples to estimate a background.</summary>
    public int EstablishedPixelCount { get; private set; }

    public bool Contains(int x, int y)
        => x >= Bounds.Left && x < Bounds.Right && y >= Bounds.Top && y < Bounds.Bottom;

    private int Index(int x, int y) => ((y - Bounds.Top) * Bounds.Width) + (x - Bounds.Left);

    /// <summary>
    /// Pushes one observation into the window and returns how it relates to the estimate.
    /// </summary>
    /// <param name="background">
    /// The estimate <b>after</b> folding this sample in. For
    /// <see cref="BackgroundObservation.Foreground"/> this is the background to pair with the
    /// observed composite; it deliberately includes the current sample, because with a median
    /// the inclusion of one more genuine background sample cannot drag the estimate towards a
    /// single outlier.
    /// </param>
    /// <param name="trustworthy">True when enough of the window agrees with the estimate.</param>
    public BackgroundObservation Observe(int x, int y, ArgbPixel observed, int gate,
        int minimumSamples, double minimumInlierFraction, out ArgbPixel background, out bool trustworthy)
    {
        background = observed;
        trustworthy = false;

        if (!Contains(x, y))
        {
            return BackgroundObservation.Insufficient;
        }

        int index = Index(x, y);
        int head = _head[index];
        int count = _count[index];

        int slot = (index * _historyLength) + head;
        _historyB[slot] = observed.B;
        _historyG[slot] = observed.G;
        _historyR[slot] = observed.R;

        _head[index] = (byte)((head + 1) % _historyLength);
        if (count < _historyLength)
        {
            count++;
            if (count == minimumSamples)
            {
                EstablishedPixelCount++;
            }
        }
        _count[index] = (byte)count;

        if (count < minimumSamples)
        {
            // Fall back to the running mean of what little there is, purely so the caller can
            // report something; it is explicitly not trustworthy.
            background = MeanOfWindow(index, count);
            return BackgroundObservation.Insufficient;
        }

        ArgbPixel median = MedianOfWindow(index, count);
        int inliers = CountInliers(index, count, median, Math.Max(1, gate));
        trustworthy = inliers >= Math.Max(3, (int)(minimumInlierFraction * count));

        background = median;

        // The comparison uses the median, not the sample being added, so a single deviating
        // sample is judged against a background it could not have influenced.
        return MaxChannelDifference(median, observed) <= gate
            ? BackgroundObservation.Background
            : BackgroundObservation.Foreground;
    }

    private ArgbPixel MeanOfWindow(int index, int count)
    {
        if (count <= 0)
        {
            return ArgbPixel.Transparent;
        }

        long b = 0, g = 0, r = 0;
        for (int j = 0; j < count; j++)
        {
            int slot = (index * _historyLength) + j;
            b += _historyB[slot];
            g += _historyG[slot];
            r += _historyR[slot];
        }

        return new ArgbPixel(
            (byte)Math.Clamp(b / count, 0, 255),
            (byte)Math.Clamp(g / count, 0, 255),
            (byte)Math.Clamp(r / count, 0, 255),
            255);
    }

    private ArgbPixel MedianOfWindow(int index, int count)
    {
        Span<byte> buffer = stackalloc byte[64];
        return new ArgbPixel(
            MedianOfChannel(_historyB, index, count, buffer),
            MedianOfChannel(_historyG, index, count, buffer),
            MedianOfChannel(_historyR, index, count, buffer),
            255);
    }

    /// <summary>
    /// Median of one channel's window. <paramref name="buffer"/> is supplied by the caller so
    /// that a single stack allocation serves all three channels.
    /// </summary>
    private byte MedianOfChannel(byte[] history, int index, int count, Span<byte> buffer)
    {
        Span<byte> values = buffer[..count];
        int baseOffset = index * _historyLength;
        for (int j = 0; j < count; j++)
        {
            values[j] = history[baseOffset + j];
        }

        // Insertion sort: count is at most 63 and usually 15, so this beats anything that
        // involves a call into the framework or an allocation.
        for (int j = 1; j < count; j++)
        {
            byte key = values[j];
            int k = j - 1;
            while (k >= 0 && values[k] > key)
            {
                values[k + 1] = values[k];
                k--;
            }
            values[k + 1] = key;
        }

        return values[count / 2];
    }

    private int CountInliers(int index, int count, ArgbPixel median, int gate)
    {
        int inliers = 0;
        for (int j = 0; j < count; j++)
        {
            int slot = (index * _historyLength) + j;
            var sample = new ArgbPixel(_historyB[slot], _historyG[slot], _historyR[slot], 255);
            if (MaxChannelDifference(sample, median) <= gate)
            {
                inliers++;
            }
        }
        return inliers;
    }

    public void Reset()
    {
        Array.Clear(_historyB);
        Array.Clear(_historyG);
        Array.Clear(_historyR);
        Array.Clear(_count);
        Array.Clear(_head);
        EstablishedPixelCount = 0;
    }

    public static int MaxChannelDifference(ArgbPixel a, ArgbPixel b)
    {
        int db = Math.Abs(a.B - b.B);
        int dg = Math.Abs(a.G - b.G);
        int dr = Math.Abs(a.R - b.R);
        return Math.Max(db, Math.Max(dg, dr));
    }
}

/// <summary>
/// Sufficient statistics for one cursor-local offset: everything needed to fit
/// <c>D = alpha*C + (1-alpha)*B</c> per channel with a single shared slope.
/// </summary>
/// <remarks>
/// <para>
/// The model is <c>D_c = m*B_c + k_c</c> with <c>m = 1 - alpha</c> shared by all three
/// channels (alpha is a property of the pixel, not of the channel) and <c>k_c = alpha*C_c</c>
/// per channel. For a fixed <c>m</c> the least-squares <c>k_c</c> is
/// <c>mean(D_c) - m*mean(B_c)</c>, so the objective reduces to a quadratic in the single
/// unknown <c>m</c>:
/// </para>
/// <code>
///   m* = sum_c Cov(B_c, D_c) / sum_c Var(B_c)
/// </code>
/// <para>
/// which is why the accumulator stores only sums rather than a sample list. This is a
/// one-parameter closed-form solve, not an iterative fit, and it is exactly identified whenever
/// the background varied under the cursor.
/// </para>
/// </remarks>
internal struct OffsetCell
{
    public int Pairs;
    /// <summary>Times this offset was seen with no cursor coverage at all.</summary>
    public int Uncovered;
    public double SumB0, SumB1, SumB2;
    public double SumD0, SumD1, SumD2;
    /// <summary>Sum over channels and pairs of <c>B*B</c>.</summary>
    public double SumBB;
    /// <summary>Sum over channels and pairs of <c>D*D</c>.</summary>
    public double SumDD;
    /// <summary>Sum over channels and pairs of <c>B*D</c>.</summary>
    public double SumBD;

    // XOR hypothesis: screen = background XOR mask, with the mask constant per pixel.
    public bool XorSeeded;
    public byte Xor0, Xor1, Xor2;
    public int XorAgree;

    public void Add(ArgbPixel background, ArgbPixel composite)
    {
        Pairs++;
        SumB0 += background.B;
        SumB1 += background.G;
        SumB2 += background.R;
        SumD0 += composite.B;
        SumD1 += composite.G;
        SumD2 += composite.R;
        SumBB += (double)background.B * background.B
               + (double)background.G * background.G
               + (double)background.R * background.R;
        SumDD += (double)composite.B * composite.B
               + (double)composite.G * composite.G
               + (double)composite.R * composite.R;
        SumBD += (double)background.B * composite.B
               + (double)background.G * composite.G
               + (double)background.R * composite.R;

        byte x0 = (byte)(background.B ^ composite.B);
        byte x1 = (byte)(background.G ^ composite.G);
        byte x2 = (byte)(background.R ^ composite.R);
        if (!XorSeeded)
        {
            XorSeeded = true;
            Xor0 = x0;
            Xor1 = x1;
            Xor2 = x2;
            XorAgree = 1;
        }
        else if (x0 == Xor0 && x1 == Xor1 && x2 == Xor2)
        {
            XorAgree++;
        }
    }

    /// <summary>Variance of the background, summed over the three channels.</summary>
    public readonly double BackgroundVarianceSum()
    {
        if (Pairs == 0) return 0;
        double n = Pairs;
        return SumBB - ((SumB0 * SumB0) + (SumB1 * SumB1) + (SumB2 * SumB2)) / n;
    }

    /// <summary>Variance of the composite, summed over the three channels.</summary>
    public readonly double CompositeVarianceSum()
    {
        if (Pairs == 0) return 0;
        double n = Pairs;
        return SumDD - ((SumD0 * SumD0) + (SumD1 * SumD1) + (SumD2 * SumD2)) / n;
    }

    /// <summary>Covariance of background and composite, summed over the three channels.</summary>
    public readonly double CovarianceSum()
    {
        if (Pairs == 0) return 0;
        double n = Pairs;
        return SumBD - ((SumB0 * SumD0) + (SumB1 * SumD1) + (SumB2 * SumD2)) / n;
    }

    /// <summary>Mean of the composite observations, per channel.</summary>
    public readonly ArgbPixel MeanComposite()
    {
        if (Pairs == 0) return ArgbPixel.Transparent;
        return new ArgbPixel(
            (byte)Math.Clamp(Math.Round(SumD0 / Pairs), 0, 255),
            (byte)Math.Clamp(Math.Round(SumD1 / Pairs), 0, 255),
            (byte)Math.Clamp(Math.Round(SumD2 / Pairs), 0, 255),
            255);
    }

    /// <summary>
    /// Fits <c>D_c = m*B_c + k_c</c> with one shared slope across channels and returns
    /// <c>m</c>, per-channel <c>k</c>, and the residual RMS in 8-bit levels.
    /// </summary>
    /// <remarks>
    /// The slope is shared because alpha is a property of the pixel, not of the channel;
    /// sharing it turns a three-parameter-per-channel fit into a one-parameter solve and removes
    /// the channel-asymmetry bias that an independent per-channel fit would allow.
    /// </remarks>
    public readonly bool TryFitAffine(out double slope, out double k0, out double k1, out double k2, out double residualRms)
    {
        slope = 0;
        k0 = k1 = k2 = 0;
        residualRms = double.NaN;

        double varianceB = BackgroundVarianceSum();
        if (Pairs < 2 || varianceB <= 0)
        {
            return false;
        }

        double n = Pairs;
        slope = CovarianceSum() / varianceB;

        double meanB0 = SumB0 / n, meanB1 = SumB1 / n, meanB2 = SumB2 / n;
        double meanD0 = SumD0 / n, meanD1 = SumD1 / n, meanD2 = SumD2 / n;
        k0 = meanD0 - (slope * meanB0);
        k1 = meanD1 - (slope * meanB1);
        k2 = meanD2 - (slope * meanB2);

        double sumSquares = SumDD - (2 * slope * SumBD) + (slope * slope * SumBB)
                            - (n * ((k0 * k0) + (k1 * k1) + (k2 * k2)));
        double degreesOfFreedom = (3 * n) - 3;
        if (degreesOfFreedom <= 0)
        {
            residualRms = 0;
            return true;
        }
        residualRms = Math.Sqrt(Math.Max(0, sumSquares) / degreesOfFreedom);
        return true;
    }
}

/// <summary>A square grid of <see cref="OffsetCell"/> keyed by cursor-local offset.</summary>
internal sealed class OffsetAccumulator
{
    private readonly OffsetCell[] _cells;

    public OffsetAccumulator(int radius)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(radius, 1);
        Radius = radius;
        Side = (radius * 2) + 1;
        _cells = new OffsetCell[checked(Side * Side)];
    }

    public int Radius { get; }
    public int Side { get; }
    public long TotalPairs { get; private set; }

    public bool Contains(int rx, int ry) => rx >= -Radius && rx <= Radius && ry >= -Radius && ry <= Radius;

    public ref OffsetCell Cell(int rx, int ry)
        => ref _cells[(((ry + Radius) * Side) + rx + Radius)];

    /// <summary>Records one (background, composite) pair at the given cursor-local offset.</summary>
    public void Add(int rx, int ry, ArgbPixel background, ArgbPixel composite)
    {
        if (!Contains(rx, ry))
        {
            return;
        }
        Cell(rx, ry).Add(background, composite);
        TotalPairs++;
    }

    public void Clear()
    {
        Array.Clear(_cells);
        TotalPairs = 0;
    }
}
