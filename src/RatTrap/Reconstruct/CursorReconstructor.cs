using RatTrap.Imaging;
using RatTrap.Interop;

namespace RatTrap.Reconstruct;

/// <summary>
/// Recovers a static cursor's pixels - colour and alpha - from a stream of screen captures
/// that already contain the cursor.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is for.</b> When an application draws its own cursor into its own frame (an
/// exclusive-fullscreen game, a custom-crosshair tool, a remote-desktop client rendering a
/// remote pointer), the cursor is just pixels. Nothing in the operating system will hand it
/// over: <c>GetCursorInfo</c> reports the system cursor, and the desktop image from both
/// Desktop Duplication and GDI <c>BitBlt</c> <b>excludes</b> the system cursor on modern
/// Windows, where it is composited on its own plane. See <c>docs/measured-behaviour.md</c>.
/// The only remaining source is the application's own output, and the only way to separate
/// the cursor from the scene behind it is to observe how the two vary independently.
/// </para>
/// <para>
/// <b>The model.</b> For a screen pixel <c>s</c> at time <c>t</c>, with the pointer at
/// <c>h_t</c> and <c>r = s - h_t</c>:
/// </para>
/// <code>
///   covered:    D_t(s) = alpha(r)*C(r) + (1 - alpha(r)) * B_t(s)
///   uncovered:  D_t(s) = B_t(s)
/// </code>
/// <para>
/// <c>alpha(r)</c> and <c>C(r)</c> are fixed properties of the cursor as long as its shape
/// does not change. <c>B</c> is whatever was behind it. Two independent mechanisms supply the
/// variation that makes this solvable, and the solver uses both:
/// </para>
/// <list type="number">
///   <item><b>Spatial.</b> The same offset <c>r</c> visits many different screen pixels as
///     the pointer moves, so a single traversal of a textured background already yields many
///     (B, D) pairs for one <c>r</c>. This is why the tool works on a completely static
///     scene as long as the user moves the mouse.</item>
///   <item><b>Temporal.</b> Within one screen position the background also changes over
///     time - animation, scrolling, a video - which adds pairs even if the pointer is
///     parked.</item>
/// </list>
/// <para>
/// <b>What is not recoverable, and why.</b> If a pixel is covered by the cursor for the whole
/// observation <i>and</i> the background under it never varied, then the observed value is
/// constant and equals <c>alpha*C + (1-alpha)*B</c> with both unknowns free. No amount of
/// further observation of the same configuration resolves it. Those pixels are reported as
/// <see cref="PixelModelKind.Unresolved"/>, and the solver falls back to assuming the pixel
/// is opaque with the observed colour - which is right for the interior of a solid cursor and
/// is stated in the report rather than hidden.
/// </para>
/// </remarks>
public sealed class CursorReconstructor
{
    private readonly ReconstructionOptions _options;
    private readonly OffsetAccumulator _offsets;
    private readonly List<string> _notes = new();

    private BackgroundModel? _background;

    private int _settleFramesRemaining;
    private int _framesObserved;
    private int _framesAccumulated;
    private int _sceneChanges;
    private long _pairsRecorded;

    private bool _hasHotspot;
    private int _minHotspotX, _minHotspotY, _maxHotspotX, _maxHotspotY;
    private int _clippedObservations;
    private BackgroundSource _backgroundSource = BackgroundSource.TemporalMedian;

    /// <summary>
    /// Median of the eight neighbours of a pixel, at two radii, in the same frame.
    /// </summary>
    /// <remarks>
    /// Two radii rather than one so that a stroke of unexpected width still leaves a majority of the
    /// samples off it. Returns false near the border, where the full set of neighbours does not exist;
    /// those pixels simply contribute nothing, which is honest - there is no background to read there.
    /// </remarks>
    private static bool TryNeighbourMedian(ArgbBitmap pixels, int x, int y, int distance, out ArgbPixel median)
    {
        median = ArgbPixel.Transparent;
        if (distance < 1)
        {
            return false;
        }

        Span<byte> channelB = stackalloc byte[24];
        Span<byte> channelG = stackalloc byte[24];
        Span<byte> channelR = stackalloc byte[24];
        int count = 0;

        for (int ring = 0; ring < 2; ring++)
        {
            int radius = distance + (ring * Math.Max(2, distance / 2));
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }
                    int nx = x + (dx * radius);
                    int ny = y + (dy * radius);
                    if (nx < 0 || ny < 0 || nx >= pixels.Width || ny >= pixels.Height)
                    {
                        continue;
                    }
                    ArgbPixel sample = pixels.GetPixel(nx, ny);
                    channelB[count] = sample.B;
                    channelG[count] = sample.G;
                    channelR[count] = sample.R;
                    count++;
                }
            }
        }

        if (count < 5)
        {
            return false;
        }

        median = new ArgbPixel(
            Median(channelB[..count]),
            Median(channelG[..count]),
            Median(channelR[..count]),
            255);
        return true;
    }

    private static byte Median(Span<byte> values)
    {
        // Insertion sort; the span is at most 16 entries.
        for (int i = 1; i < values.Length; i++)
        {
            byte key = values[i];
            int j = i - 1;
            while (j >= 0 && values[j] > key)
            {
                values[j + 1] = values[j];
                j--;
            }
            values[j + 1] = key;
        }
        return values[values.Length / 2];
    }

    public CursorReconstructor(ReconstructionOptions? options = null)
    {
        _options = options ?? ReconstructionOptions.Default;
        _options.Validate();
        _offsets = new OffsetAccumulator(_options.MaxOffsetRadius);
    }

    /// <summary>Screen rectangle the background model currently covers.</summary>
    public RECT AccumulationBounds => _background?.Bounds ?? default;

    /// <summary>Number of screen pixels with a background estimate.</summary>
    public int BackgroundPixelCount => _background?.EstablishedPixelCount ?? 0;

    public int FramesObserved => _framesObserved;

    public IReadOnlyList<string> Notes => _notes;

    /// <summary>
    /// Feeds one captured frame.
    /// </summary>
    /// <param name="roi">Where <paramref name="pixels"/> came from, in desktop coordinates.</param>
    /// <param name="pixels">The captured pixels, which <b>include</b> the cursor.</param>
    /// <param name="hotspotX">Pointer hotspot X in desktop coordinates for this frame.</param>
    /// <param name="hotspotY">Pointer hotspot Y in desktop coordinates for this frame.</param>
    public void Observe(RECT roi, ArgbBitmap pixels, int hotspotX, int hotspotY)
        => Observe(roi, pixels, hotspotX, hotspotY, BackgroundSource.TemporalMedian);

    /// <summary>
    /// Feeds one captured frame, choosing where the background comes from.
    /// </summary>
    /// <param name="roi">Where <paramref name="pixels"/> came from, in desktop coordinates.</param>
    /// <param name="pixels">The captured pixels, which <b>include</b> whatever is being recovered.</param>
    /// <param name="hotspotX">Pointer hotspot X in desktop coordinates.</param>
    /// <param name="hotspotY">Pointer hotspot Y in desktop coordinates.</param>
    /// <param name="source">
    /// <see cref="BackgroundSource.TemporalMedian"/> for a cursor that moves, or
    /// <see cref="BackgroundSource.SpatialNeighbourMedian"/> for a crosshair that does not - see the
    /// remarks on that enum for why the distinction is not a preference.
    /// </param>
    public void Observe(RECT roi, ArgbBitmap pixels, int hotspotX, int hotspotY, BackgroundSource source)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (roi.Width != pixels.Width || roi.Height != pixels.Height)
        {
            throw new ArgumentException(
                $"ROI {roi} does not match the {pixels.Width}x{pixels.Height} image.", nameof(roi));
        }
        if (roi.Width <= 0 || roi.Height <= 0)
        {
            return;
        }

        _framesObserved++;
        _backgroundSource = source;
        TrackHotspot(hotspotX, hotspotY);
        EnsureBackground(roi);

        BackgroundModel background = _background!;
        int gate = _options.ForegroundGate;
        int neighbourDistance = _options.NeighbourDistance;

        int initialised = 0, backgrounds = 0, foregrounds = 0;
        var pixelsSpan = pixels.Pixels;
        int stride = pixels.Stride;

        for (int y = 0; y < roi.Height; y++)
        {
            int screenY = roi.Top + y;
            int rowBase = y * stride;
            for (int x = 0; x < roi.Width; x++)
            {
                int screenX = roi.Left + x;
                if (!background.Contains(screenX, screenY))
                {
                    continue;
                }

                int index = rowBase + (x * 4);
                var observed = new ArgbPixel(pixelsSpan[index], pixelsSpan[index + 1], pixelsSpan[index + 2], 255);

                int rx = screenX - hotspotX;
                int ry = screenY - hotspotY;

                BackgroundObservation outcome;
                ArgbPixel backgroundEstimate;
                bool backgroundTrustworthy;

                if (source == BackgroundSource.SpatialNeighbourMedian)
                {
                    // The background is read from the scene around this pixel rather than from this
                    // pixel's own history, which is what makes a fixed crosshair solvable at all.
                    if (!TryNeighbourMedian(pixels, x, y, neighbourDistance, out backgroundEstimate))
                    {
                        initialised++;
                        continue;
                    }
                    outcome = BackgroundModel.MaxChannelDifference(backgroundEstimate, observed) <= gate
                        ? BackgroundObservation.Background
                        : BackgroundObservation.Foreground;
                    // There is no history to confirm, so there is nothing to be unconfirmed either.
                    backgroundTrustworthy = true;
                }
                else
                {
                    outcome = background.Observe(screenX, screenY, observed, gate,
                        _options.MinBackgroundSamples, _options.MinBackgroundInlierFraction,
                        out backgroundEstimate, out backgroundTrustworthy);
                }

                switch (outcome)
                {
                    case BackgroundObservation.Insufficient:
                        initialised++;
                        break;

                    case BackgroundObservation.Background:
                        backgrounds++;
                        if (_offsets.Contains(rx, ry)) _offsets.Cell(rx, ry).Uncovered++;
                        break;

                    case BackgroundObservation.Foreground:
                        foregrounds++;
                        break;
                }

                // Pairs are only recorded once the model has settled after a scene change, and only
                // from a background estimate the window has confirmed. Both conditions exist to keep
                // unverifiable pairs out of the least-squares fit.
                if (outcome == BackgroundObservation.Foreground && _settleFramesRemaining == 0 && backgroundTrustworthy)
                {
                    if (_offsets.Contains(rx, ry))
                    {
                        _offsets.Cell(rx, ry).Add(backgroundEstimate, observed);
                        _pairsRecorded++;
                    }
                }
            }
        }

        if (_settleFramesRemaining > 0)
        {
            _settleFramesRemaining--;
            return;
        }

        _framesAccumulated++;

        // Scene-change detection exists to invalidate a temporal background model whose history
        // describes content that is no longer on screen. In spatial mode there is no history to
        // invalidate, and a busy scene would trip the detector constantly - suppressing pair
        // recording with settle frames precisely when the scene is giving the solver the most to
        // work with.
        if (_backgroundSource == BackgroundSource.TemporalMedian)
        {
            DetectSceneChange(background, initialised, backgrounds, foregrounds);
        }
    }

    private void TrackHotspot(int hotspotX, int hotspotY)
    {
        if (!_hasHotspot)
        {
            _hasHotspot = true;
            _minHotspotX = _maxHotspotX = hotspotX;
            _minHotspotY = _maxHotspotY = hotspotY;
            return;
        }
        _minHotspotX = Math.Min(_minHotspotX, hotspotX);
        _maxHotspotX = Math.Max(_maxHotspotX, hotspotX);
        _minHotspotY = Math.Min(_minHotspotY, hotspotY);
        _maxHotspotY = Math.Max(_maxHotspotY, hotspotY);
    }

    private void EnsureBackground(RECT roi)
    {
        if (_background is null)
        {
            _background = new BackgroundModel(Pad(roi, 128), _options.BackgroundHistoryLength);
            return;
        }

        RECT current = _background.Bounds;
        if (roi.Left >= current.Left && roi.Top >= current.Top &&
            roi.Right <= current.Right && roi.Bottom <= current.Bottom)
        {
            return;
        }

        _clippedObservations++;
        if (_clippedObservations <= 3)
        {
            RECT grown = Union(current, roi);
            grown = Pad(grown, 128);
            _background = new BackgroundModel(grown, _options.BackgroundHistoryLength, _background);
            _notes.Add($"accumulation area grown to {grown} after the pointer moved outside it.");
        }
    }

    private static RECT Pad(RECT r, int amount)
        => RECT.FromSize(r.Left - amount, r.Top - amount, r.Width + (amount * 2), r.Height + (amount * 2));

    private static RECT Union(RECT a, RECT b) => new()
    {
        Left = Math.Min(a.Left, b.Left),
        Top = Math.Min(a.Top, b.Top),
        Right = Math.Max(a.Right, b.Right),
        Bottom = Math.Max(a.Bottom, b.Bottom),
    };

    /// <summary>
    /// Detects a wholesale change of scene (a cut, a new level, a monitor mode change) and
    /// invalidates the background model, because every pixel's history now describes content
    /// that is no longer on screen.
    /// </summary>
    private void DetectSceneChange(BackgroundModel background, int initialised, int backgrounds, int foregrounds)
    {
        int judged = backgrounds + foregrounds;
        if (judged < 64)
        {
            return;
        }

        double foregroundFraction = (double)foregrounds / judged;
        if (foregroundFraction <= _options.SceneChangeFraction)
        {
            return;
        }

        _sceneChanges++;
        _settleFramesRemaining = _options.SceneChangeSettleFrames;
        background.Reset();
        if (_sceneChanges <= 3)
        {
            _notes.Add($"scene change #{_sceneChanges} detected " +
                       $"({foregroundFraction:P0} of {judged} pixels changed at once); background model reset.");
        }
        _ = initialised;
    }

    public void Reset()
    {
        _background = null;
        _settleFramesRemaining = 0;
        _framesObserved = 0;
        _framesAccumulated = 0;
        _sceneChanges = 0;
        _pairsRecorded = 0;
        _hasHotspot = false;
        _clippedObservations = 0;
        _backgroundSource = BackgroundSource.TemporalMedian;
        _notes.Clear();
        _offsets.Clear();
    }

    /// <summary>
    /// Raw accumulated statistics for one cursor-local offset, for diagnostics.
    /// </summary>
    /// <remarks>
    /// Exposed because a reconstruction that looks wrong is almost impossible to reason about
    /// from the finished bitmap alone: whether a pixel failed because it had no pairs, too few,
    /// or pairs whose background never varied are three different problems with three different
    /// fixes. This is what located the spurious-pair pattern in the synthetic XOR case.
    /// </remarks>
    public string DescribeOffset(int rx, int ry)
    {
        if (!_offsets.Contains(rx, ry))
        {
            return $"offset ({rx},{ry}) is outside the tracked grid (radius {_offsets.Radius}).";
        }

        ref OffsetCell cell = ref _offsets.Cell(rx, ry);
        double varianceB = cell.BackgroundVarianceSum();
        double varianceD = cell.CompositeVarianceSum();
        double agreement = cell.Pairs == 0 ? 0 : (double)cell.XorAgree / cell.Pairs;
        return $"offset ({rx},{ry}): pairs={cell.Pairs} uncovered={cell.Uncovered} " +
               $"varB={varianceB:F1} varD={varianceD:F1} meanD={cell.MeanComposite()} " +
               $"xorSeed=({cell.Xor0:X2},{cell.Xor1:X2},{cell.Xor2:X2}) xorAgree={agreement:P0}";
    }

    /// <summary>Solves the accumulated statistics into a cursor bitmap.</summary>
    public ReconstructionResult Solve()
    {
        var notes = new List<string>(_notes);

        if (_background is null || _pairsRecorded == 0)
        {
            notes.Add("refused to produce a cursor: no (background, composite) pairs were recorded, so there " +
                      "is nothing to solve. Move the pointer over a non-uniform background, or use the " +
                      "compositor source ('--source compositor'), which reads the cursor directly and needs " +
                      "no reconstruction.");
            return new ReconstructionResult(null, BuildReport(notes, default, 0, 0, 0, 0, 0));
        }

        // Output extent: the bounding box of every offset that was ever observed with the
        // cursor actually on it. One pixel of margin keeps anti-aliased fringes that only
        // ever produced weakly-covered samples.
        if (!TryFindCoveredBounds(out int minRx, out int minRy, out int maxRx, out int maxRy))
        {
            bool moved = _minHotspotX != _maxHotspotX || _minHotspotY != _maxHotspotY;
            notes.Add("refused to produce a cursor: no cursor-local offset accumulated enough coverage " +
                      "evidence to be part of a glyph" +
                      (moved
                          ? ", so the cursor was probably never inside the tracked area."
                          : ". The pointer never moved during the observation, which is the most common cause: " +
                            "with a stationary pointer every offset sees only one background, so nothing about the " +
                            "cursor can be separated from the scene behind it."));
            notes.Add("Move the pointer across a textured area and run again.");
            return new ReconstructionResult(null, BuildReport(notes, default, 0, 0, 0, 0, 0));
        }

        int side = _offsets.Side;
        int width = maxRx - minRx + 1;
        int height = maxRy - minRy + 1;

        var image = new ArgbBitmap(width, height);
        byte[] xorMask = new byte[width * height * 4];

        int transparent = 0, alphaSolved = 0, opaqueAssumed = 0, xorMasked = 0, unresolved = 0;
        double alphaSum = 0;
        double residualSum = 0, residualWorst = 0;
        int residualCount = 0;

        var models = new PixelModel[width * height];

        for (int ry = minRy; ry <= maxRy; ry++)
        {
            for (int rx = minRx; rx <= maxRx; rx++)
            {
                ref OffsetCell cell = ref _offsets.Cell(rx, ry);
                PixelModel model = Classify(ref cell);
                models[((ry - minRy) * width) + (rx - minRx)] = model;

                switch (model.Kind)
                {
                    case PixelModelKind.Transparent: transparent++; break;
                    case PixelModelKind.AlphaSolved:
                        alphaSolved++;
                        alphaSum += model.Alpha;
                        if (!double.IsNaN(model.ResidualRms))
                        {
                            residualSum += model.ResidualRms;
                            residualWorst = Math.Max(residualWorst, model.ResidualRms);
                            residualCount++;
                        }
                        break;
                    case PixelModelKind.OpaqueAssumed: opaqueAssumed++; alphaSum += 1; break;
                    case PixelModelKind.XorMasked: xorMasked++; alphaSum += 1; break;
                    default: unresolved++; alphaSum += 1; break;
                }

                int pixelIndex = (((ry - minRy) * width) + (rx - minRx)) * 4;
                if (model.Kind == PixelModelKind.Transparent)
                {
                    // Already zeroed.
                    continue;
                }

                if (model.Kind == PixelModelKind.XorMasked)
                {
                    // The RGBA view shows the mask over black, which is what the cursor looks
                    // like against a black background; the exact mask goes into the CUR.
                    xorMask[pixelIndex] = model.Colour.B;
                    xorMask[pixelIndex + 1] = model.Colour.G;
                    xorMask[pixelIndex + 2] = model.Colour.R;
                    xorMask[pixelIndex + 3] = 255;
                }

                image.Pixels[pixelIndex] = model.Colour.B;
                image.Pixels[pixelIndex + 1] = model.Colour.G;
                image.Pixels[pixelIndex + 2] = model.Colour.R;
                image.Pixels[pixelIndex + 3] = (byte)Math.Clamp(Math.Round(model.Alpha * 255), 0, 255);
            }
        }

        bool xorDominant = xorMasked > 0 && xorMasked >= 0.8 * (xorMasked + alphaSolved + opaqueAssumed + unresolved);

        // --- Refuse to emit something that cannot be a cursor --------------------------------
        // Recovering alpha and colour is not the same as recovering a cursor. A wrong answer that
        // looks plausible in a viewer is worse than a failure, because the user cannot tell it apart
        // from a correct one. Measured on real hardware: a pointer that never moved, over a terminal
        // whose output was scrolling, produced 600 "covered" pixels spread over a 160x162 box, and an
        // earlier version reported that as a cursor.
        //
        // The guard below is deliberately about *information* rather than about shape. Shape tests
        // (compactness, connected components, "the hotspot must be drawn") all reject legitimate
        // cursors: a crosshair is sparse, a move cursor is four disconnected arrows, and a hotspot can
        // sit in a gap at the centre of the glyph. Availability of information has no such blind spots.
        bool pointerMoved = _minHotspotX != _maxHotspotX || _minHotspotY != _maxHotspotY;

        if (_backgroundSource == BackgroundSource.TemporalMedian && !pointerMoved && alphaSolved == 0)
        {
            notes.Add("refused to produce a cursor: the pointer never moved, so no cursor-local offset " +
                      "was ever seen against two different backgrounds, and no pixel's alpha could be " +
                      "identified from the background varying instead. Nothing here carries information " +
                      "about the cursor.");
            notes.Add("Move the pointer across a textured area and run again. This is a refusal rather " +
                      "than a failure: emitting a plausible-looking but wrong cursor would be worse, " +
                      "because it cannot be told apart from a correct one.");
            return new ReconstructionResult(null,
                BuildReport(notes, _background.Bounds, transparent, alphaSolved, opaqueAssumed, xorMasked, unresolved));
        }

        // In crosshair mode the geometry does not move by design, so the test above cannot apply. What
        // replaces it: the neighbourhood background is only a valid stand-in for the real background if
        // some pixel's alpha was actually identified against it. If nothing was solved and nothing was
        // even plausibly opaque, the frame content did not support a reconstruction at all.
        if (_backgroundSource == BackgroundSource.SpatialNeighbourMedian)
        {
            int covered = alphaSolved + opaqueAssumed + unresolved;
            double solvedFraction = covered == 0 ? 0 : (double)alphaSolved / covered;

            if (alphaSolved == 0 || solvedFraction < _options.MinSpatialSolvedFraction)
            {
                notes.Add($"refused to produce a crosshair: only {alphaSolved} of {covered} covered pixels " +
                          $"({solvedFraction:P0}) had their alpha identified, and at least " +
                          $"{_options.MinSpatialSolvedFraction:P0} is required.");
                notes.Add("That fraction is the test that separates a fixed overlay from ordinary scene " +
                          "structure. Both are thin and both differ from their surroundings, and a static " +
                          "region passes every other check - a wall of text was measured doing exactly that, " +
                          "and was reported as a 193x193 crosshair by an earlier version. What scene " +
                          "structure lacks is a varying background underneath it, so its pixels come back " +
                          "assumed-opaque instead of solved.");
                notes.Add("Aim at something that moves so the scene changes beneath the crosshair itself, then " +
                          "run again.");
                return new ReconstructionResult(null,
                    BuildReport(notes, _background.Bounds, transparent, alphaSolved, opaqueAssumed, xorMasked, unresolved));
            }
        }

        // Halo suppression: RGB under fully transparent pixels is meaningless for viewing but
        // is read by bilinear scaling, where it produces dark fringes. Fill from the nearest
        // opaque neighbour.
        FillTransparentColours(image, models, width, height);

        var cursor = new CursorImage(image)
        {
            HotspotX = -minRx,
            HotspotY = -minRy,
            PreferredEncoding = xorDominant ? CursorEncoding.XorMasked32 : CursorEncoding.AlphaBlend32,
            XorMaskBgra = xorDominant ? xorMask : null,
            Provenance = CursorProvenance.Reconstructed,
            Note = $"reconstructed from {_framesAccumulated} frames; " +
                   $"{alphaSolved} pixels solved, {opaqueAssumed + unresolved} assumed opaque, " +
                   $"{transparent} transparent; pairs={_pairsRecorded}",
        };

        notes.Add($"mean alpha of covered pixels = {alphaSum / Math.Max(1, alphaSolved + opaqueAssumed + xorMasked + unresolved):F3}");
        if (residualCount > 0)
        {
            notes.Add($"affine fit residual of solved pixels: mean {residualSum / residualCount:F2}, " +
                      $"worst {residualWorst:F2} levels over {residualCount} pixels");
        }
        if (xorDominant)
        {
            notes.Add("a majority of covered pixels are XOR-composited, so the cursor is written with " +
                      "the exact XOR mask, which reproduces the original rendering.");
        }
        if (unresolved + opaqueAssumed > 0.25 * (width * height))
        {
            notes.Add($"{opaqueAssumed + unresolved} pixels were assumed opaque because the background never " +
                      "varied under them. Move the pointer across a more textured area to solve them.");
        }
        if (_sceneChanges > 0)
        {
            notes.Add($"the scene changed {_sceneChanges} time(s); pairs recorded while the background model " +
                      "was settling were discarded.");
        }

        return new ReconstructionResult(cursor,
            BuildReport(notes, _background.Bounds, transparent, alphaSolved, opaqueAssumed, xorMasked, unresolved))
        {
            KindMap = Array.ConvertAll(models, m => m.Kind),
            MapWidth = width,
            MapHeight = height,
            MapOriginRx = minRx,
            MapOriginRy = minRy,
        };
    }

    /// <summary>
    /// True when an offset looked covered often enough to be part of a glyph.
    /// </summary>
    /// <remarks>
    /// A real cursor covers an offset every time the pointer places that offset on screen, so the
    /// coverage rate is near 1; residue from a stale background model is a few pairs against hundreds of
    /// ordinary observations. See <see cref="ReconstructionOptions.MinCoverageRate"/>.
    /// </remarks>
    private bool IsCoverageRateSufficient(ref OffsetCell cell)
    {
        int total = cell.Pairs + cell.Uncovered;
        if (total <= 0)
        {
            return false;
        }

        // A fixed overlay is present in nearly every frame; scene structure that merely resembles one is
        // not. See MinSpatialCoverageRate.
        double required = _backgroundSource == BackgroundSource.SpatialNeighbourMedian
            ? _options.MinSpatialCoverageRate
            : _options.MinCoverageRate;
        return (double)cell.Pairs / total >= required;
    }

    /// <summary>
    /// Bounding box of the offsets that carry enough coverage evidence to be part of the cursor.
    /// </summary>
    private bool TryFindCoveredBounds(out int minRx, out int minRy, out int maxRx, out int maxRy)
    {
        minRx = minRy = int.MaxValue;
        maxRx = maxRy = int.MinValue;
        int required = _options.MinCoverageEvidence;

        for (int ry = -_offsets.Radius; ry <= _offsets.Radius; ry++)
        {
            for (int rx = -_offsets.Radius; rx <= _offsets.Radius; rx++)
            {
                ref OffsetCell cell = ref _offsets.Cell(rx, ry);
                if (cell.Pairs < required || !IsCoverageRateSufficient(ref cell))
                {
                    continue;
                }
                if (rx < minRx) minRx = rx;
                if (rx > maxRx) maxRx = rx;
                if (ry < minRy) minRy = ry;
                if (ry > maxRy) maxRy = ry;
            }
        }

        if (maxRx < minRx)
        {
            return false;
        }

        // One pixel of margin, clamped to the tracked grid.
        minRx = Math.Max(minRx - 1, -_offsets.Radius);
        minRy = Math.Max(minRy - 1, -_offsets.Radius);
        maxRx = Math.Min(maxRx + 1, _offsets.Radius);
        maxRy = Math.Min(maxRy + 1, _offsets.Radius);
        return true;
    }

    /// <summary>Decides what one cursor-local offset is, from its accumulated statistics.</summary>
    private PixelModel Classify(ref OffsetCell cell)
    {
        int pairs = cell.Pairs;

        if (pairs < _options.MinCoverageEvidence || !IsCoverageRateSufficient(ref cell))
        {
            // Not enough coverage evidence to call this part of the cursor. A single pair can come from
            // a briefly-wrong background estimate, and treating that as coverage would both add a
            // phantom pixel and move the hotspot; a low coverage *rate* means the offset was mostly
            // ordinary background that occasionally disagreed with its own estimate.
            return PixelModel.Transparent;
        }

        if (pairs < _options.MinPairs)
        {
            // Too few pairs to fit anything, but the pixel clearly was covered: the best
            // available answer is the observed colour at full opacity.
            return new PixelModel(PixelModelKind.Unresolved, 1.0, cell.MeanComposite(), 0.0);
        }

        double backgroundVariance = cell.BackgroundVarianceSum();
        double compositeVariance = cell.CompositeVarianceSum();

        // XOR hypothesis. A legacy or masked-colour cursor writes screen = background XOR mask
        // with a constant mask, so the observed difference is not affine in the background at
        // all and the alpha model would fit it badly. Test it first, but only where the
        // background actually varied - otherwise "constant XOR" is indistinguishable from
        // "constant screen".
        if (backgroundVariance >= _options.MinBackgroundVariance)
        {
            double agreement = (double)cell.XorAgree / pairs;
            if (agreement >= _options.XorAgreementThreshold)
            {
                double confidence = Math.Clamp((agreement - _options.XorAgreementThreshold) /
                                               (1 - _options.XorAgreementThreshold), 0, 1);
                return new PixelModel(PixelModelKind.XorMasked, 1.0,
                    new ArgbPixel(cell.Xor0, cell.Xor1, cell.Xor2, 255), confidence);
            }
        }

        if (backgroundVariance >= _options.MinBackgroundVariance &&
            cell.TryFitAffine(out double slope, out double k0, out double k1, out double k2, out double residualRms))
        {
            double alpha = 1.0 - slope;
            if (alpha < _options.TransparentAlphaThreshold)
            {
                // The composite tracks the background exactly: nothing is drawn here.
                return PixelModel.Transparent;
            }

            double clampedAlpha = Math.Clamp(alpha, 0, 1);
            byte c0, c1, c2;
            if (clampedAlpha >= _options.OpaqueAlphaThreshold)
            {
                // Dividing by a near-zero alpha would amplify noise, and for an opaque pixel
                // the composite is the colour regardless of the background.
                ArgbPixel mean = cell.MeanComposite();
                c0 = mean.B; c1 = mean.G; c2 = mean.R;
            }
            else
            {
                c0 = (byte)Math.Clamp(Math.Round(k0 / clampedAlpha), 0, 255);
                c1 = (byte)Math.Clamp(Math.Round(k1 / clampedAlpha), 0, 255);
                c2 = (byte)Math.Clamp(Math.Round(k2 / clampedAlpha), 0, 255);
            }

            // Confidence from the residual of the affine fit: a well-modelled pixel has
            // essentially zero residual, a misaligned or animated one does not.
            double confidence = Math.Clamp(1.0 - (residualRms / 12.0), 0, 1);
            return new PixelModel(PixelModelKind.AlphaSolved, clampedAlpha, new ArgbPixel(c0, c1, c2, 255), confidence)
            {
                ResidualRms = residualRms,
            };
        }

        if (compositeVariance <= _options.MaxOpaqueVariance)
        {
            // The composite never varied. Either the pixel is fully opaque (D = C for every
            // background) or it is not covered at all; the caller distinguishes those by
            // whether pairs exist, and pairs exist here.
            return new PixelModel(PixelModelKind.OpaqueAssumed, 1.0, cell.MeanComposite(),
                Math.Clamp(backgroundVariance / _options.MinBackgroundVariance, 0, 1));
        }

        // The composite varies but the background did not vary enough to attribute the
        // variation to it. That is a genuine failure to identify alpha.
        return new PixelModel(PixelModelKind.Unresolved, 1.0, cell.MeanComposite(), 0.0);
    }

    /// <summary>
    /// Gives fully transparent pixels the colour of their nearest opaque neighbour.
    /// </summary>
    /// <remarks>
    /// Alpha is what makes a cursor correct, but RGB under alpha=0 is still read by bilinear
    /// filtering and by naive compositing, where black under a transparent edge shows up as a
    /// dark halo. A short multi-pass dilation is enough and costs nothing at cursor sizes.
    /// </remarks>
    private static void FillTransparentColours(ArgbBitmap image, PixelModel[] models, int width, int height)
    {
        const int MaxPasses = 8;
        // A separate flag array rather than testing for alpha==0: a pixel filled with black
        // would otherwise never propagate, and black is a perfectly ordinary cursor colour.
        var filled = new bool[width * height];

        for (int pass = 0; pass < MaxPasses; pass++)
        {
            bool changed = false;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width) + x;
                    if (models[index].Kind != PixelModelKind.Transparent || filled[index])
                    {
                        continue;
                    }

                    long b = 0, g = 0, r = 0;
                    int contributors = 0;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                            {
                                continue;
                            }
                            int neighbour = (ny * width) + nx;
                            bool neighbourHasColour = models[neighbour].Kind != PixelModelKind.Transparent || filled[neighbour];
                            if (!neighbourHasColour)
                            {
                                continue;
                            }
                            b += image.Pixels[neighbour * 4];
                            g += image.Pixels[(neighbour * 4) + 1];
                            r += image.Pixels[(neighbour * 4) + 2];
                            contributors++;
                        }
                    }

                    if (contributors > 0)
                    {
                        int pixel = index * 4;
                        image.Pixels[pixel] = (byte)(b / contributors);
                        image.Pixels[pixel + 1] = (byte)(g / contributors);
                        image.Pixels[pixel + 2] = (byte)(r / contributors);
                        // Alpha deliberately stays 0; only the meaningless colour is filled in.
                        filled[index] = true;
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                break;
            }
        }
    }

    private ReconstructionReport BuildReport(List<string> notes, RECT bounds,
        int transparent, int alphaSolved, int opaqueAssumed, int xorMasked, int unresolved)
        => new()
        {
            FramesObserved = _framesObserved,
            FramesAccumulated = _framesAccumulated,
            SceneChanges = _sceneChanges,
            OffsetCells = _offsets.Side * _offsets.Side,
            TransparentPixels = transparent,
            AlphaSolvedPixels = alphaSolved,
            OpaqueAssumedPixels = opaqueAssumed,
            XorMaskedPixels = xorMasked,
            UnresolvedPixels = unresolved,
            PairsRecorded = (int)Math.Min(int.MaxValue, _pairsRecorded),
            AccumulationBounds = bounds,
            HotspotMin = new POINT { X = _minHotspotX, Y = _minHotspotY },
            HotspotMax = new POINT { X = _maxHotspotX, Y = _maxHotspotY },
            Notes = notes,
        };
}







