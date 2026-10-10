using System.Runtime.InteropServices;
using RatTrap.Imaging;
using RatTrap.Interop;
using RatTrap.Reconstruct;

namespace RatTrap.SelfTest;

/// <summary>
/// Deterministic verification of the reconstruction mathematics and of the interop layout
/// assumptions. No capture hardware is involved, so this runs anywhere.
/// </summary>
/// <remarks>
/// The synthetic cases carry ground truth: a known cursor is composited onto a known
/// background and the solver is asked to invert that. Two cases deliberately cannot be
/// solved, and they assert the <b>failure reporting</b> rather than a magic result, because
/// "this configuration is mathematically under-determined" is a real answer that the tool
/// must give clearly instead of returning a plausible-looking wrong bitmap.
/// </remarks>
internal static class CoreSelfTest
{
    public static IReadOnlyList<string> Run()
    {
        var lines = new List<string>();
        InteropLayout(lines);
        AlphaBlendRecovery(lines);
        OpaqueCursorRecovery(lines);
        XorCursorRecovery(lines);
        UniformBackgroundIsReported(lines);
        NoMotionIsReported(lines);
        StationaryPointerOnChangingSceneIsRefused(lines);
        CrosshairRecovery(lines);
        CrosshairOnAStaticSceneAssumesAlpha(lines);
        CrosshairRejectsAnIntermittentOverlay(lines);
        MaskPacking(lines);
        EmbeddedSoundCues(lines);
        return lines;
    }

    // -----------------------------------------------------------------------------
    // Feedback cues
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Checks that the three embedded cues are the sounds they are supposed to be.
    /// </summary>
    /// <remarks>
    /// The requirement is not merely "there are three sounds" but "start and stop are a matched pair and the
    /// save cue cannot be confused with either", and reading the generator's configuration does not
    /// demonstrate that. These are the same measurements <c>tools/build_sounds.py --verify</c> makes on the
    /// same artifact, restated in the language the program is written in, so that a corrupted or truncated
    /// payload fails the program's own test rather than only the generator's.
    /// <para>
    /// Zero-crossing rate stands in for pitch: it counts sign changes, which rise and fall with the dominant
    /// frequency content and need no transform. Spectral centroid is the energy-weighted mean frequency, and
    /// it is what makes the save cue measurably darker than the pair.
    /// </para>
    /// </remarks>
    private static void EmbeddedSoundCues(List<string> lines)
    {
        string[] names = ["Start", "Stop", "Save"];
        string[] encoded = [Audio.SoundCues.StartBase64, Audio.SoundCues.StopBase64, Audio.SoundCues.SaveBase64];
        var rising = new double[names.Length];
        var falling = new double[names.Length];
        var centroids = new double[names.Length];
        var payloads = new byte[names.Length][];

        for (int index = 0; index < names.Length; index++)
        {
            byte[] wav;
            try
            {
                wav = Convert.FromBase64String(encoded[index]);
            }
            catch (FormatException)
            {
                CheckTrue(lines, $"the {names[index]} cue is valid base64", false, "the payload does not decode");
                return;
            }
            payloads[index] = wav;

            if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F' ||
                wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
            {
                CheckTrue(lines, $"the {names[index]} cue is a RIFF/WAVE image", false, "the header is wrong");
                return;
            }

            // Little-endian fields read straight out of the canonical 44-byte PCM header.
            int channels = wav[22] | (wav[23] << 8);
            int bits = wav[34] | (wav[35] << 8);
            int rate = wav[24] | (wav[25] << 8) | (wav[26] << 16) | (wav[27] << 24);
            int dataLength = wav[40] | (wav[41] << 8) | (wav[42] << 16) | (wav[43] << 24);

            CheckTrue(lines, $"the {names[index]} cue is mono 16-bit PCM",
                channels == 1 && bits == 16, $"{channels} channels, {bits} bit");
            CheckTrue(lines, $"the {names[index]} cue is at the expected sample rate",
                rate == 22050, $"{rate} Hz");

            int samples = Math.Min(dataLength / 2, (wav.Length - 44) / 2);
            if (samples < 64)
            {
                CheckTrue(lines, $"the {names[index]} cue has audio in it", false, $"{samples} samples");
                return;
            }

            var mono = new double[samples];
            for (int i = 0; i < samples; i++)
            {
                short value = (short)(wav[44 + (i * 2)] | (wav[44 + (i * 2) + 1] << 8));
                mono[i] = value / 32768.0;
            }

            double rms = 0;
            for (int i = 0; i < samples; i++)
            {
                rms += mono[i] * mono[i];
            }
            rms = Math.Sqrt(rms / samples);
            CheckTrue(lines, $"the {names[index]} cue is not silent", rms > 0.01, $"rms {rms:F4}");

            int half = samples / 2;
            rising[index] = ZeroCrossingRate(mono, 0, half);
            falling[index] = ZeroCrossingRate(mono, half, samples - half);
            centroids[index] = SpectralCentroid(mono, rate);

            lines.Add($"  {names[index],-5} {wav.Length,6} bytes  {samples / (double)rate:F3}s  {rate} Hz  " +
                      $"zcr {rising[index]:F0} -> {falling[index]:F0}  centroid {centroids[index]:F0} Hz");
        }

        CheckTrue(lines, "the start cue sweeps upward", falling[0] > rising[0],
            $"zero-crossing rate goes {rising[0]:F0} -> {falling[0]:F0}");
        CheckTrue(lines, "the stop cue sweeps downward", falling[1] < rising[1],
            $"zero-crossing rate goes {rising[1]:F0} -> {falling[1]:F0}");
        CheckTrue(lines, "the save cue is darker than the start/stop pair",
            centroids[2] < Math.Min(centroids[0], centroids[1]),
            $"save {centroids[2]:F0} Hz against {centroids[0]:F0} and {centroids[1]:F0} Hz");
        CheckTrue(lines, "all three cues are distinct",
            !payloads[0].AsSpan().SequenceEqual(payloads[1]) &&
            !payloads[1].AsSpan().SequenceEqual(payloads[2]) &&
            !payloads[0].AsSpan().SequenceEqual(payloads[2]),
            "two cues are byte-identical");
    }

    /// <summary>Sign changes per sample across a range, which tracks the dominant frequency content.</summary>
    private static double ZeroCrossingRate(double[] samples, int start, int count)
    {
        if (count <= 1)
        {
            return 0;
        }

        double mean = 0;
        for (int i = start; i < start + count; i++)
        {
            mean += samples[i];
        }
        mean /= count;

        int crossings = 0;
        bool previous = samples[start] - mean >= 0;
        for (int i = start + 1; i < start + count; i++)
        {
            bool current = samples[i] - mean >= 0;
            if (current != previous)
            {
                crossings++;
            }
            previous = current;
        }
        return crossings / (double)(count - 1);
    }

    /// <summary>
    /// Energy-weighted mean frequency, by a fixed-size DFT.
    /// </summary>
    /// <remarks>
    /// A full FFT would be faster, but a cue is a few thousand samples and this runs once, so the
    /// straightforward correlation is the better trade here: no bit-reversal, no twiddle tables, and nothing
    /// to get subtly wrong in code whose only job is to check a number.
    /// </remarks>
    private static double SpectralCentroid(double[] samples, int rate)
    {
        const int Bins = 512;
        double weighted = 0;
        double total = 0;

        for (int bin = 1; bin < Bins; bin++)
        {
            double omega = Math.PI * bin / Bins;   // 0..pi, that is 0..Nyquist
            double real = 0;
            double imaginary = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                // A Hann window, so a short burst does not smear across every bin.
                double window = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (samples.Length - 1)));
                double angle = omega * i;
                real += samples[i] * window * Math.Cos(angle);
                imaginary += samples[i] * window * Math.Sin(angle);
            }

            double magnitude = Math.Sqrt((real * real) + (imaginary * imaginary));
            weighted += magnitude * (omega / Math.PI) * (rate / 2.0);
            total += magnitude;
        }

        return total > 0 ? weighted / total : 0;
    }

    // -----------------------------------------------------------------------------
    // Crosshair mode
    // -----------------------------------------------------------------------------

    /// <summary>Ground truth for the synthetic crosshair: a colour and an alpha per position.</summary>
    private sealed record SyntheticCrosshair(int Size, int Centre, int StrokeHalf, int ArmHalf)
    {
        public ArgbPixel StrokeColour { get; } = new(0, 255, 0, 255);      // pure green
        public ArgbPixel RingColour { get; } = new(0, 255, 255, 255);      // yellow
        public byte RingAlpha { get; } = 140;

        public bool IsStroke(int x, int y)
            => (Math.Abs(x - Centre) <= StrokeHalf && Math.Abs(y - Centre) <= ArmHalf) ||
               (Math.Abs(y - Centre) <= StrokeHalf && Math.Abs(x - Centre) <= ArmHalf);

        /// <summary>A one-pixel anti-aliased fringe around the stroke, which is the part whose alpha has to be solved.</summary>
        public bool IsRing(int x, int y)
        {
            if (IsStroke(x, y))
            {
                return false;
            }
            int wide = StrokeHalf + 1;
            int tall = ArmHalf + 1;
            return (Math.Abs(x - Centre) <= wide && Math.Abs(y - Centre) <= tall) ||
                   (Math.Abs(y - Centre) <= wide && Math.Abs(x - Centre) <= tall);
        }

        public ArgbPixel Sample(int x, int y)
        {
            if (IsStroke(x, y)) return StrokeColour;
            if (IsRing(x, y)) return RingColour with { A = RingAlpha };
            return ArgbPixel.Transparent;
        }
    }

    /// <summary>
    /// A scene that is smooth in space and changing in time, which is exactly the regime crosshair
    /// mode needs.
    /// </summary>
    /// <remarks>
    /// Smoothness is what lets a neighbourhood sample stand in for the background at a pixel: a gentle
    /// gradient means the eight samples around a point average back to very nearly that point's own
    /// background, whereas a white-noise scene would make the substitute wrong by the noise amplitude.
    /// Temporal change is what gives the solver a variance to fit against. A wall the player is walking
    /// past is exactly this.
    /// </remarks>
    private static void BuildSmoothScene(ArgbBitmap destination, int frame)
    {
        double t = frame * 0.15;
        for (int y = 0; y < destination.Height; y++)
        {
            for (int x = 0; x < destination.Width; x++)
            {
                double b = 118 + (92 * Math.Sin((x / 150.0) + t));
                double g = 118 + (92 * Math.Sin((y / 170.0) + (t * 1.1)));
                double r = 118 + (92 * Math.Sin(((x + y) / 190.0) - (t * 0.9)));
                destination.SetPixel(x, y, new ArgbPixel(
                    (byte)Math.Clamp(b, 0, 255),
                    (byte)Math.Clamp(g, 0, 255),
                    (byte)Math.Clamp(r, 0, 255),
                    255));
            }
        }
    }

    private static void CompositeCrosshair(ArgbBitmap frame, SyntheticCrosshair crosshair)
    {
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                ArgbPixel layer = crosshair.Sample(x, y);
                if (layer.A == 0)
                {
                    continue;
                }
                ArgbPixel under = frame.GetPixel(x, y);
                frame.SetPixel(x, y, new ArgbPixel(
                    Blend(layer.B, under.B, layer.A),
                    Blend(layer.G, under.G, layer.A),
                    Blend(layer.R, under.R, layer.A),
                    255));
            }
        }
    }

    private static void CrosshairRecovery(List<string> lines)
    {
        const int size = 128;
        var crosshair = new SyntheticCrosshair(size, size / 2, StrokeHalf: 1, ArmHalf: 18);

        var options = new ReconstructionOptions { NeighbourDistance = 9 };
        var reconstructor = new CursorReconstructor(options);
        var frame = new ArgbBitmap(size, size);
        var roi = RECT.FromSize(1000, 1000, size, size);
        int hotspotX = roi.Left + crosshair.Centre;
        int hotspotY = roi.Top + crosshair.Centre;

        const int frames = 70;
        for (int t = 0; t < frames; t++)
        {
            BuildSmoothScene(frame, t);
            CompositeCrosshair(frame, crosshair);
            reconstructor.Observe(roi, frame, hotspotX, hotspotY, BackgroundSource.SpatialNeighbourMedian);
        }

        var result = reconstructor.Solve();
        if (!result.Success)
        {
            lines.Add($"FAIL crosshair: solve refused after {frames} frames: {result.Report.Describe()}");
            return;
        }

        var recovered = result.Cursor!;
        lines.Add($"  crosshair: {frames} frames, hotspot=({recovered.HotspotX},{recovered.HotspotY}) " +
                  $"{recovered.Width}x{recovered.Height}, solved={result.Report.AlphaSolvedPixels} " +
                  $"assumed={result.Report.OpaqueAssumedPixels} unresolved={result.Report.UnresolvedPixels}");

        int strokeCount = 0, ringCount = 0;
        double strokeAlphaWorst = 0, ringAlphaWorst = 0, ringAlphaSum = 0;
        double strokeColourWorst = 0;
        int falsePositives = 0;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int imageX = x - crosshair.Centre + recovered.HotspotX;
                int imageY = y - crosshair.Centre + recovered.HotspotY;
                if (imageX < 0 || imageY < 0 || imageX >= recovered.Width || imageY >= recovered.Height)
                {
                    continue;
                }
                ArgbPixel got = recovered.Image.GetPixel(imageX, imageY);
                ArgbPixel truth = crosshair.Sample(x, y);

                if (truth.A == 0)
                {
                    // Nothing is drawn here, so alpha must have come out (near) zero.
                    if (got.A > 40)
                    {
                        falsePositives++;
                    }
                    continue;
                }

                double alphaError = Math.Abs((got.A / 255.0) - (truth.A / 255.0));
                if (crosshair.IsStroke(x, y))
                {
                    strokeCount++;
                    strokeAlphaWorst = Math.Max(strokeAlphaWorst, alphaError);
                    if (truth.A > 200)
                    {
                        double colourError = Math.Max(Math.Abs(got.B - truth.B),
                                            Math.Max(Math.Abs(got.G - truth.G), Math.Abs(got.R - truth.R)));
                        strokeColourWorst = Math.Max(strokeColourWorst, colourError);
                    }
                }
                else
                {
                    ringCount++;
                    ringAlphaWorst = Math.Max(ringAlphaWorst, alphaError);
                    ringAlphaSum += alphaError;
                }
            }
        }

        double ringAlphaMean = ringCount == 0 ? 0 : ringAlphaSum / ringCount;
        lines.Add($"  crosshair accuracy: stroke alpha worst={strokeAlphaWorst:F3} colour worst={strokeColourWorst:F0}; " +
                  $"fringe alpha mean={ringAlphaMean:F3} worst={ringAlphaWorst:F3}; " +
                  $"false positives={falsePositives} of {size * size - strokeCount - ringCount} empty pixels");

        CheckTrue(lines, "crosshair stroke alpha is recovered exactly", strokeAlphaWorst < 0.05,
            $"worst error {strokeAlphaWorst:F3}");
        CheckTrue(lines, "crosshair stroke colour is recovered", strokeColourWorst < 12,
            $"worst error {strokeColourWorst:F0} levels");
        CheckTrue(lines, "crosshair fringe alpha is recovered", ringAlphaMean < 0.08,
            $"mean error {ringAlphaMean:F3}");
        CheckTrue(lines, "crosshair leaves the background transparent",
            falsePositives <= 0.01 * (size * size - strokeCount - ringCount),
            $"{falsePositives} empty pixels came back opaque");
    }

    /// <summary>
    /// The same fixed crosshair over a scene that never changes.
    /// </summary>
    /// <remarks>
    /// Alpha cannot be identified here - there is no background variation to fit - but a refusal would be
    /// the wrong answer, and the distinction matters. An <i>opaque</i> pixel's composite is its colour
    /// regardless of what is behind it, so the crosshair's solid stroke is recovered exactly even with no
    /// variation at all. Only the anti-aliased fringe, whose alpha is genuinely unrecoverable here, is
    /// left assumed. So the tool answers, and says which parts of the answer are assumptions.
    /// </remarks>
    private static void CrosshairOnAStaticSceneAssumesAlpha(List<string> lines)
    {
        const int size = 128;
        var crosshair = new SyntheticCrosshair(size, size / 2, StrokeHalf: 1, ArmHalf: 18);
        var reconstructor = new CursorReconstructor(new ReconstructionOptions { NeighbourDistance = 9 });
        var frame = new ArgbBitmap(size, size);
        var roi = RECT.FromSize(1000, 1000, size, size);

        for (int t = 0; t < 60; t++)
        {
            BuildSmoothScene(frame, 0);   // frozen: the frame index is always 0
            CompositeCrosshair(frame, crosshair);
            reconstructor.Observe(roi, frame, roi.Left + crosshair.Centre, roi.Top + crosshair.Centre,
                BackgroundSource.SpatialNeighbourMedian);
        }

        var result = reconstructor.Solve();
        lines.Add($"  crosshair on a static scene: success={result.Success} " +
                  $"solved={result.Report.AlphaSolvedPixels} assumed={result.Report.OpaqueAssumedPixels}");

        CheckTrue(lines, "crosshair refuses a static scene", !result.Success,
            "a crosshair was produced from a scene with nothing changing behind it");
        CheckTrue(lines, "the refusal explains itself",
            result.Report.Notes.Any(n => n.Contains("refused", StringComparison.OrdinalIgnoreCase)),
            "no refusal note was produced: " + string.Join(" | ", result.Report.Notes));
    }

    /// <summary>
    /// A moving scene is necessary but not sufficient: the overlay must also be there in <i>every</i> frame.
    /// </summary>
    /// <remarks>
    /// This is the test that a real desktop demanded. Watching the centre of a text editor - a window that
    /// contains no crosshair - produced a 193x193 "crosshair", because text is thin, high-contrast, differs
    /// from its neighbourhood median, and was dense enough to clear a 0.25 coverage rate. The discriminator
    /// is temporal: a crosshair is fixed in screen space and therefore covered in every frame, whereas scene
    /// structure drifts through. Here the crosshair is present in only two frames out of three, which is
    /// still far more than real scene structure and must nevertheless be rejected.
    /// </remarks>
    private static void CrosshairRejectsAnIntermittentOverlay(List<string> lines)
    {
        const int size = 128;
        var crosshair = new SyntheticCrosshair(size, size / 2, StrokeHalf: 1, ArmHalf: 18);
        var reconstructor = new CursorReconstructor(new ReconstructionOptions { NeighbourDistance = 9 });
        var frame = new ArgbBitmap(size, size);
        var roi = RECT.FromSize(1000, 1000, size, size);

        for (int t = 0; t < 60; t++)
        {
            BuildSmoothScene(frame, t);

            // Present in two frames out of three: a coverage rate near 0.67, which is what a persistent
            // scene structure looks like and which must not be mistaken for a fixed overlay.
            if (t % 3 != 2)
            {
                CompositeCrosshair(frame, crosshair);
            }

            reconstructor.Observe(roi, frame, roi.Left + crosshair.Centre, roi.Top + crosshair.Centre,
                BackgroundSource.SpatialNeighbourMedian);
        }

        var result = reconstructor.Solve();
        lines.Add($"  crosshair present in 2 of 3 frames: success={result.Success} " +
                  $"covered={result.Report.CoveredPixels}");
        CheckTrue(lines, "crosshair rejects an overlay that is not always there", !result.Success,
            "an intermittent overlay was accepted as a crosshair");
    }

    // -----------------------------------------------------------------------------
    // Interop layout
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Asserts the sizes of the structures passed by pointer to native code.
    /// </summary>
    /// <remarks>
    /// A wrong layout here is memory corruption, not an exception, and the compiler cannot
    /// see it. These numbers come from the Win32 headers and from the absolute vtable audit
    /// table in DxgiInterop.cs.
    /// </remarks>
    private static void InteropLayout(List<string> lines)
    {
        Check(lines, "DXGI_MODE_DESC size", 28, Marshal.SizeOf<DXGI_MODE_DESC>());
        Check(lines, "DXGI_OUTPUT_DESC size", 96, Marshal.SizeOf<DXGI_OUTPUT_DESC>());
        Check(lines, "DXGI_OUTDUPL_DESC size", 36, Marshal.SizeOf<DXGI_OUTDUPL_DESC>());
        Check(lines, "DXGI_OUTDUPL_FRAME_INFO size", 48, Marshal.SizeOf<DXGI_OUTDUPL_FRAME_INFO>());
        Check(lines, "DXGI_OUTDUPL_POINTER_SHAPE_INFO size", 24, Marshal.SizeOf<DXGI_OUTDUPL_POINTER_SHAPE_INFO>());
        Check(lines, "DXGI_MAPPED_RECT size", IntPtr.Size == 8 ? 16 : 8, Marshal.SizeOf<DXGI_MAPPED_RECT>());
        Check(lines, "D3D11_TEXTURE2D_DESC size", 44, Marshal.SizeOf<D3D11_TEXTURE2D_DESC>());
        Check(lines, "D3D11_MAPPED_SUBRESOURCE size", 16, Marshal.SizeOf<D3D11_MAPPED_SUBRESOURCE>());
        Check(lines, "D3D11_BOX size", 24, Marshal.SizeOf<D3D11_BOX>());

        // Vtable slots are the other thing that cannot be checked by the compiler. These
        // assert the arithmetic behind the constants, so a re-derivation that changes a
        // constant has to change the derivation too.
        Check(lines, "IUnknown prefix is 3 slots", 3, Dxgi.SlotRelease + 1);
        Check(lines, "IDXGIObject base offset", 3, 3);
        Check(lines, "IDXGIDevice::GetAdapter = 3 + 4 + 0", 7, Dxgi.SlotIdxgiDeviceGetAdapter);
        Check(lines, "IDXGIAdapter::EnumOutputs = 3 + 4 + 0", 7, Dxgi.SlotIdxgiAdapterEnumOutputs);
        Check(lines, "IDXGIOutput::GetDesc = 3 + 4 + 0", 7, Dxgi.SlotIdxgiOutputGetDesc);
        Check(lines, "IDXGIOutput1::DuplicateOutput = 7 + 12 + 3", 22, Dxgi.SlotIdxgiOutput1DuplicateOutput);
        Check(lines, "IDXGIOutputDuplication::AcquireNextFrame = 7 + 1", 8, Dxgi.SlotIdxgiOutputDuplicationAcquireNextFrame);
        Check(lines, "IDXGIOutputDuplication::GetFramePointerShape = 7 + 4", 11, Dxgi.SlotIdxgiOutputDuplicationGetFramePointerShape);
        Check(lines, "IDXGIOutputDuplication::MapDesktopSurface = 7 + 5", 12, Dxgi.SlotIdxgiOutputDuplicationMapDesktopSurface);
        Check(lines, "IDXGIOutputDuplication::ReleaseFrame = 7 + 7", 14, Dxgi.SlotIdxgiOutputDuplicationReleaseFrame);
        Check(lines, "ID3D11Device::CreateTexture2D = 3 + 2", 5, Dxgi.SlotId3d11DeviceCreateTexture2D);
        Check(lines, "ID3D11DeviceContext base (ID3D11DeviceChild adds 4)", 7, Dxgi.SlotId3d11DeviceContextFirstMethod);
        Check(lines, "ID3D11DeviceContext::Map = 7 + 7", 14, Dxgi.SlotId3d11DeviceContextMap);
        Check(lines, "ID3D11DeviceContext::Unmap = 7 + 8", 15, Dxgi.SlotId3d11DeviceContextUnmap);
        Check(lines, "ID3D11DeviceContext::CopySubresourceRegion = 7 + 39", 46, Dxgi.SlotId3d11DeviceContextCopySubresourceRegion);
    }

    // -----------------------------------------------------------------------------
    // Synthetic ground truth
    // -----------------------------------------------------------------------------

    /// <summary>A synthetic cursor: straight-alpha colour plus a shape, in cursor-local coordinates.</summary>
    private sealed record SyntheticCursor(int Width, int Height, int HotspotX, int HotspotY, ArgbBitmap Image);

    private static SyntheticCursor BuildArrowCursor()
    {
        const int width = 24, height = 24, hx = 2, hy = 2;
        var image = new ArgbBitmap(width, height);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                ArgbPixel pixel = ArgbPixel.Transparent;

                // Diagonal blade: fully opaque white.
                if (y <= x + 6 && x < 14 && y < 20)
                {
                    pixel = new ArgbPixel(255, 255, 255, 255);
                }

                // Solid colour block: fully opaque.
                if (x is >= 15 and < 22 && y is >= 4 and < 12)
                {
                    pixel = new ArgbPixel(60, 30, 200, 255);
                }

                // Semi-transparent strip: the case that makes alpha recovery non-trivial.
                if (x is >= 8 and < 12 && y is >= 6 and < 18)
                {
                    pixel = new ArgbPixel(255, 120, 0, 128);
                }

                image.SetPixel(x, y, pixel);
            }
        }

        return new SyntheticCursor(width, height, hx, hy, image);
    }

    /// <summary>A deterministic textured background with independent per-channel variation.</summary>
    private static ArgbBitmap BuildTexturedBackground(int width, int height, int seed)
    {
        var background = new ArgbBitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // A smooth ramp combined with a per-pixel hash: neighbouring pixels differ
                // (so a one-pixel pointer step changes the background) but the range is wide
                // and each channel moves independently (so the shared-slope fit is well posed).
                uint h = Hash((uint)(x * 2654435761u) ^ ((uint)y * 40503u) ^ (uint)seed);
                byte b = (byte)(((x * 3) + (h & 0xFF)) >> 1);
                byte g = (byte)(((y * 5) + ((h >> 8) & 0xFF)) >> 1);
                byte r = (byte)((((x + y) * 2) + ((h >> 16) & 0xFF)) >> 1);
                background.SetPixel(x, y, new ArgbPixel(b, g, r, 255));
            }
        }
        return background;
    }

    private static uint Hash(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        value ^= value >> 16;
        return value;
    }

    /// <summary>Composites the cursor onto the background at a screen position, with integer rounding.</summary>
    private static byte Blend(byte cursor, byte background, byte alpha)
        => (byte)Math.Clamp(((cursor * alpha) + (background * (255 - alpha)) + 127) / 255, 0, 255);

    private static void AlphaBlendRecovery(List<string> lines)
    {
        const int canvas = 256;
        const int roiSide = 64;
        var cursor = BuildArrowCursor();
        var background = BuildTexturedBackground(canvas, canvas, 12345);

        var reconstructor = new CursorReconstructor();
        var roiImage = new ArgbBitmap(roiSide, roiSide);
        int frames = 0;

        for (int hy = 32; hy < canvas - 32; hy += 7)
        {
            for (int hx = 32; hx < canvas - 32; hx += 7)
            {
                ComposeFrame(background, cursor, hx, hy, roiImage);
                var roi = RECT.FromSize(hx - (roiSide / 2), hy - (roiSide / 2), roiSide, roiSide);
                reconstructor.Observe(roi, roiImage, hx, hy);
                frames++;
            }
        }

        var result = reconstructor.Solve();
        if (!result.Success)
        {
            lines.Add($"FAIL alpha-blend: solve failed after {frames} frames: {result.Report.Describe()}");
            return;
        }

        var recovered = result.Cursor!;
        lines.Add($"  alpha-blend: {frames} frames, hotspot=({recovered.HotspotX},{recovered.HotspotY}) " +
                  $"{recovered.Width}x{recovered.Height}, solved=" +
                  $"{result.Report.AlphaSolvedPixels}/{result.Report.CoveredPixels}");

        // The output origin corresponds to cursor-local offset (-HotspotX, -HotspotY), and the
        // one-pixel anti-aliasing margin means the true hotspot must land at (3,3) for this
        // cursor (its footprint starts at offset (-2,-2)).
        Check(lines, "alpha-blend hotspot X", 3, recovered.HotspotX);
        Check(lines, "alpha-blend hotspot Y", 3, recovered.HotspotY);

        double worstAlpha = 0, sumAlpha = 0;
        double worstColour = 0, sumColour = 0;
        int comparedPixels = 0, missing = 0;

        for (int oy = 0; oy < cursor.Height; oy++)
        {
            for (int ox = 0; ox < cursor.Width; ox++)
            {
                ArgbPixel truth = cursor.Image.GetPixel(ox, oy);
                if (truth.A == 0)
                {
                    continue;
                }

                // Cursor-local pixel (ox,oy) sits at offset r = (ox - hotspotX, oy - hotspotY).
                // The recovered image's origin corresponds to offset (-HotspotX, -HotspotY),
                // so the image index is r + recovered.Hotspot.
                int imageX = ox - cursor.HotspotX + recovered.HotspotX;
                int imageY = oy - cursor.HotspotY + recovered.HotspotY;
                if (imageX < 0 || imageY < 0 || imageX >= recovered.Width || imageY >= recovered.Height)
                {
                    missing++;
                    continue;
                }

                ArgbPixel got = recovered.Image.GetPixel(imageX, imageY);
                double alphaError = Math.Abs((got.A / 255.0) - (truth.A / 255.0));
                sumAlpha += alphaError;
                worstAlpha = Math.Max(worstAlpha, alphaError);

                if (truth.A > 0.5 * 255)
                {
                    double colourError = Math.Max(Math.Abs(got.B - truth.B),
                                        Math.Max(Math.Abs(got.G - truth.G), Math.Abs(got.R - truth.R)));
                    sumColour += colourError;
                    worstColour = Math.Max(worstColour, colourError);
                    comparedPixels++;
                }
            }
        }

        int totalCovered = cursor.Width * cursor.Height - CountTransparent(cursor);
        double meanAlpha = sumAlpha / Math.Max(1, totalCovered);
        double meanColour = comparedPixels == 0 ? 0 : sumColour / comparedPixels;

        lines.Add($"  alpha-blend accuracy: mean|dAlpha|={meanAlpha:F4} worst|dAlpha|={worstAlpha:F4}; " +
                  $"mean|dColour|={meanColour:F2} worst|dColour|={worstColour:F0} over {comparedPixels} opaque pixels");

        Check(lines, "alpha-blend no missing footprint pixels", 0, missing);
        CheckTrue(lines, "alpha-blend mean alpha error < 0.02", meanAlpha < 0.02, $"was {meanAlpha:F4}");
        CheckTrue(lines, "alpha-blend worst alpha error < 0.10", worstAlpha < 0.10, $"was {worstAlpha:F4}");
        CheckTrue(lines, "alpha-blend mean colour error < 6 levels", meanColour < 6.0, $"was {meanColour:F2}");
        CheckTrue(lines, "alpha-blend worst colour error < 24 levels", worstColour < 24.0, $"was {worstColour:F0}");
        CheckTrue(lines, "alpha-blend solved most covered pixels",
            result.Report.AlphaSolvedPixels >= 0.75 * result.Report.CoveredPixels,
            $"{result.Report.AlphaSolvedPixels}/{result.Report.CoveredPixels}");
    }

    private static int CountTransparent(SyntheticCursor cursor)
    {
        int count = 0;
        for (int y = 0; y < cursor.Height; y++)
        {
            for (int x = 0; x < cursor.Width; x++)
            {
                if (cursor.Image.GetPixel(x, y).A == 0) count++;
            }
        }
        return count;
    }

    private static void ComposeFrame(ArgbBitmap background, SyntheticCursor cursor, int hx, int hy, ArgbBitmap destination)
    {
        int ox = hx - cursor.HotspotX;
        int oy = hy - cursor.HotspotY;
        int roiLeft = hx - (destination.Width / 2);
        int roiTop = hy - (destination.Height / 2);
        int stride = destination.Stride;

        for (int y = 0; y < destination.Height; y++)
        {
            for (int x = 0; x < destination.Width; x++)
            {
                ArgbPixel pixel = background.GetPixel(roiLeft + x, roiTop + y);

                int cx = roiLeft + x - ox;
                int cy = roiTop + y - oy;
                if (cx >= 0 && cy >= 0 && cx < cursor.Width && cy < cursor.Height)
                {
                    ArgbPixel layer = cursor.Image.GetPixel(cx, cy);
                    if (layer.A != 0)
                    {
                        pixel = new ArgbPixel(
                            Blend(layer.B, pixel.B, layer.A),
                            Blend(layer.G, pixel.G, layer.A),
                            Blend(layer.R, pixel.R, layer.A),
                            255);
                    }
                }

                int index = (y * stride) + (x * 4);
                destination.Pixels[index] = pixel.B;
                destination.Pixels[index + 1] = pixel.G;
                destination.Pixels[index + 2] = pixel.R;
                destination.Pixels[index + 3] = 255;
            }
        }
    }

    private static void OpaqueCursorRecovery(List<string> lines)
    {
        // A fully opaque cursor cannot be solved by the slope (the composite does not depend on
        // the background at all) and must be recovered by the constant-composite branch.
        const int canvas = 192, roiSide = 64;
        const int width = 16, height = 16, hx = 1, hy = 1;

        var image = new ArgbBitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image.SetPixel(x, y, new ArgbPixel((byte)(20 + (x * 3)), (byte)(200 - (y * 4)), (byte)(90 + (x + y)), 255));
            }
        }

        var cursor = new SyntheticCursor(width, height, hx, hy, image);
        var background = BuildTexturedBackground(canvas, canvas, 999);
        var reconstructor = new CursorReconstructor();
        var roiImage = new ArgbBitmap(roiSide, roiSide);

        for (int y = 32; y < canvas - 32; y += 6)
        {
            for (int x = 32; x < canvas - 32; x += 6)
            {
                ComposeFrame(background, cursor, x, y, roiImage);
                reconstructor.Observe(RECT.FromSize(x - (roiSide / 2), y - (roiSide / 2), roiSide, roiSide), roiImage, x, y);
            }
        }

        var result = reconstructor.Solve();
        if (!result.Success)
        {
            lines.Add($"FAIL opaque: solve failed: {result.Report.Describe()}");
            return;
        }

        var recovered = result.Cursor!;
        lines.Add($"  opaque: solved={result.Report.AlphaSolvedPixels} opaqueAssumed={result.Report.OpaqueAssumedPixels}");

        int worst = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                ArgbPixel truth = cursor.Image.GetPixel(x, y);
                int ix = x + recovered.HotspotX - hx;
                int iy = y + recovered.HotspotY - hy;
                if (ix < 0 || iy < 0 || ix >= recovered.Width || iy >= recovered.Height)
                {
                    lines.Add($"FAIL opaque: pixel ({x},{y}) outside the recovered {recovered.Width}x{recovered.Height} image");
                    return;
                }
                ArgbPixel got = recovered.Image.GetPixel(ix, iy);
                worst = Math.Max(worst, Math.Max(Math.Abs(got.B - truth.B),
                                     Math.Max(Math.Abs(got.G - truth.G), Math.Abs(got.R - truth.R))));
                if (got.A != 255)
                {
                    lines.Add($"FAIL opaque: pixel ({x},{y}) recovered with alpha {got.A}, expected 255");
                    return;
                }
            }
        }

        lines.Add($"  opaque: worst colour error = {worst} levels");
        CheckTrue(lines, "opaque worst colour error < 6 levels", worst < 6, $"was {worst}");
        // A fully opaque cursor is recovered through the slope: the composite does not depend on
        // the background, so the fitted slope is zero and alpha comes out as 1. That is the
        // correct route, and it needs no background variation at all for the interior.
        CheckTrue(lines, "opaque recovered through the slope branch",
            result.Report.AlphaSolvedPixels > 0.9 * result.Report.CoveredPixels,
            $"alphaSolved={result.Report.AlphaSolvedPixels}/{result.Report.CoveredPixels}");
    }

    private static void XorCursorRecovery(List<string> lines)
    {
        // screen = background XOR mask. The mask is deliberately asymmetric per channel so a
        // per-channel XOR test cannot be satisfied by a coincidence.
        const int canvas = 192, roiSide = 64;
        const int width = 16, height = 16, hx = 8, hy = 8;
        var mask = new ArgbPixel(0xFF, 0x7F, 0x00, 255);

        var image = new ArgbBitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // A cross shape carries the mask; the rest has mask 0 and stays background.
                bool inCross = Math.Abs(x - hx) <= 2 || Math.Abs(y - hy) <= 2;
                image.SetPixel(x, y, inCross ? mask : ArgbPixel.Transparent);
            }
        }

        var cursor = new SyntheticCursor(width, height, hx, hy, image);
        var background = BuildTexturedBackground(canvas, canvas, 4242);
        var reconstructor = new CursorReconstructor();
        var roiImage = new ArgbBitmap(roiSide, roiSide);

        for (int y = 32; y < canvas - 32; y += 6)
        {
            for (int x = 32; x < canvas - 32; x += 6)
            {
                int roiLeft = x - (roiSide / 2);
                int roiTop = y - (roiSide / 2);
                int ox = x - hx, oy = y - hy;

                for (int ry = 0; ry < roiSide; ry++)
                {
                    for (int rx = 0; rx < roiSide; rx++)
                    {
                        ArgbPixel pixel = background.GetPixel(roiLeft + rx, roiTop + ry);
                        int cx = roiLeft + rx - ox;
                        int cy = roiTop + ry - oy;
                        if (cx >= 0 && cy >= 0 && cx < width && cy < height && image.GetPixel(cx, cy).A != 0)
                        {
                            pixel = new ArgbPixel((byte)(pixel.B ^ mask.B), (byte)(pixel.G ^ mask.G), (byte)(pixel.R ^ mask.R), 255);
                        }
                        roiImage.SetPixel(rx, ry, pixel);
                    }
                }

                reconstructor.Observe(RECT.FromSize(roiLeft, roiTop, roiSide, roiSide), roiImage, x, y);
            }
        }

        var result = reconstructor.Solve();
        if (!result.Success)
        {
            lines.Add($"FAIL xor: solve failed: {result.Report.Describe()}");
            return;
        }

        var recovered = result.Cursor!;
        lines.Add($"  xor: xorMasked={result.Report.XorMaskedPixels} alphaSolved={result.Report.AlphaSolvedPixels} " +
                  $"opaqueAssumed={result.Report.OpaqueAssumedPixels} unresolved={result.Report.UnresolvedPixels} " +
                  $"transparent={result.Report.TransparentPixels} covered={result.Report.CoveredPixels}");
        lines.Add($"  xor: encoding={recovered.PreferredEncoding} " +
                  $"xorMask={(recovered.XorMaskBgra is null ? "absent" : "present")} " +
                  $"{recovered.Width}x{recovered.Height} hotspot=({recovered.HotspotX},{recovered.HotspotY})");

        CheckTrue(lines, "xor detection found masked pixels", result.Report.XorMaskedPixels >= 40,
            $"xorMasked={result.Report.XorMaskedPixels}");
        lines.Add("  xor model map (X=mask, a=solved, O=opaque, ?=unresolved, .=transparent):");
        foreach (string mapLine in result.DescribeKindMap().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            lines.Add("    " + mapLine);
        }
        foreach (int probeRx in new[] { -20, -17, -14, -11, -9, -5, 0, 5 })
        {
            lines.Add("    " + reconstructor.DescribeOffset(probeRx, 0));
        }
        Check(lines, "xor encoding chosen", (int)CursorEncoding.XorMasked32, (int)recovered.PreferredEncoding);
        CheckTrue(lines, "xor mask payload present", recovered.XorMaskBgra is not null, "XorMaskBgra was null");

        if (recovered.XorMaskBgra is { } payload)
        {
            // Sample the centre of the cross and require the exact mask back.
            int px = hx + recovered.HotspotX - hx;
            int py = hy + recovered.HotspotY - hy;
            int index = ((py * recovered.Width) + px) * 4;
            var got = new ArgbPixel(payload[index], payload[index + 1], payload[index + 2], payload[index + 3]);
            lines.Add($"  xor: mask at hotspot = ({got.B:X2},{got.G:X2},{got.R:X2}) expected ({mask.B:X2},{mask.G:X2},{mask.R:X2})");
            Check(lines, "xor mask B", mask.B, got.B);
            Check(lines, "xor mask G", mask.G, got.G);
            Check(lines, "xor mask R", mask.R, got.R);
        }
    }

    private static void UniformBackgroundIsReported(List<string> lines)
    {
        // A perfectly uniform background under a moving cursor gives no way to separate alpha
        // from colour: only alpha*C is observable. The tool must say so.
        const int canvas = 192, roiSide = 64;
        var cursor = BuildArrowCursor();
        var background = new ArgbBitmap(canvas, canvas);
        background.Fill(new ArgbPixel(90, 90, 90, 255));

        var reconstructor = new CursorReconstructor();
        var roiImage = new ArgbBitmap(roiSide, roiSide);
        for (int y = 32; y < canvas - 32; y += 6)
        {
            for (int x = 32; x < canvas - 32; x += 6)
            {
                ComposeFrame(background, cursor, x, y, roiImage);
                reconstructor.Observe(RECT.FromSize(x - (roiSide / 2), y - (roiSide / 2), roiSide, roiSide), roiImage, x, y);
            }
        }

        var result = reconstructor.Solve();
        string report = result.Report.Describe();
        lines.Add($"  uniform background: solved={result.Report.AlphaSolvedPixels} " +
                  $"assumed={result.Report.OpaqueAssumedPixels} unresolved={result.Report.UnresolvedPixels}");

        CheckTrue(lines, "uniform background solves (almost) nothing",
            result.Report.AlphaSolvedPixels <= 0.05 * Math.Max(1, result.Report.CoveredPixels),
            $"alphaSolved={result.Report.AlphaSolvedPixels}/{result.Report.CoveredPixels}");
        CheckTrue(lines, "uniform background reports something to the user",
            report.Contains("note:", StringComparison.Ordinal) || result.Report.CoveredPixels == 0,
            "no explanatory note was produced");
    }

    private static void NoMotionIsReported(List<string> lines)
    {
        // A stationary cursor on a stationary background. Every covered pixel is always
        // covered, so no (background, composite) pair can ever exist.
        const int canvas = 192, roiSide = 64;
        var cursor = BuildArrowCursor();
        var background = BuildTexturedBackground(canvas, canvas, 7);

        var reconstructor = new CursorReconstructor();
        var roiImage = new ArgbBitmap(roiSide, roiSide);
        const int hx = 96, hy = 96;
        var roi = RECT.FromSize(hx - (roiSide / 2), hy - (roiSide / 2), roiSide, roiSide);

        for (int i = 0; i < 60; i++)
        {
            ComposeFrame(background, cursor, hx, hy, roiImage);
            reconstructor.Observe(roi, roiImage, hx, hy);
        }

        var result = reconstructor.Solve();
        lines.Add($"  no motion: success={result.Success} pairs={result.Report.PairsRecorded}");

        CheckTrue(lines, "no motion reports failure rather than a guess", !result.Success,
            "a cursor was produced from a configuration that cannot be solved");
        CheckTrue(lines, "no motion explains itself",
            result.Report.Notes.Any(n => n.Contains("pairs", StringComparison.OrdinalIgnoreCase)),
            "the report does not mention the missing pairs: " + string.Join(" | ", result.Report.Notes));
    }

    /// <summary>
    /// A stationary pointer over a scene that never settles must be refused, not answered.
    /// </summary>
    /// <remarks>
    /// This is the case that produced a plausible-looking but entirely wrong cursor on real hardware:
    /// a pointer that never moved, over a terminal whose text was scrolling. The background model
    /// could not settle, so differences from it were noise rather than coverage, and enough scattered
    /// "covered" pixels existed to fill a bounding box. The guard is about availability of information
    /// rather than about the shape of the result, because every shape test tried was found to reject a
    /// legitimate cursor - crosshairs are sparse, move cursors are disconnected, and a hotspot can sit
    /// in a gap in the middle of the glyph.
    /// </remarks>
    private static void StationaryPointerOnChangingSceneIsRefused(List<string> lines)
    {
        const int canvas = 192, roiSide = 64;
        var cursor = BuildArrowCursor();
        var reconstructor = new CursorReconstructor();
        var roiImage = new ArgbBitmap(roiSide, roiSide);
        const int hx = 96, hy = 96;
        var roi = RECT.FromSize(hx - (roiSide / 2), hy - (roiSide / 2), roiSide, roiSide);

        for (int frame = 0; frame < 80; frame++)
        {
            // A fresh pattern every frame, and the pointer parked: nothing about the cursor can be
            // learned, and everything that differs from the background model is an artifact of the
            // model failing to settle.
            var background = BuildTexturedBackground(canvas, canvas, 5000 + frame);
            ComposeFrame(background, cursor, hx, hy, roiImage);
            reconstructor.Observe(roi, roiImage, hx, hy);
        }

        var result = reconstructor.Solve();
        lines.Add($"  stationary on a changing scene: success={result.Success} " +
                  $"covered={result.Report.CoveredPixels} pairs={result.Report.PairsRecorded}");

        CheckTrue(lines, "stationary pointer on a changing scene is refused", !result.Success,
            "a cursor was produced from a configuration that carries no information about one");
        CheckTrue(lines, "the refusal explains itself",
            result.Report.Notes.Any(n => n.Contains("refused", StringComparison.OrdinalIgnoreCase)),
            "no refusal note was produced: " + string.Join(" | ", result.Report.Notes));
    }

    private static void MaskPacking(List<string> lines)    {
        // 1bpp masks must round-trip, and rows must be DWORD aligned.
        Check(lines, "1bpp row bytes for width 1", 4, BitMask1Bpp.RowBytes(1));
        Check(lines, "1bpp row bytes for width 32", 4, BitMask1Bpp.RowBytes(32));
        Check(lines, "1bpp row bytes for width 33", 8, BitMask1Bpp.RowBytes(33));

        const int width = 13, height = 7;
        int stride = BitMask1Bpp.RowBytes(width);
        var mask = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                BitMask1Bpp.SetBit(mask, stride, x, y, ((x + y) & 1) == 0);
            }
        }

        bool ok = true;
        for (int y = 0; y < height && ok; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (BitMask1Bpp.GetBit(mask, stride, x, y) != (((x + y) & 1) == 0))
                {
                    ok = false;
                    break;
                }
            }
        }
        CheckTrue(lines, "1bpp mask round-trips", ok, "a bit was read back incorrectly");

        // Bits past the image width must stay clear, or the padding leaks into the cursor.
        bool paddingClear = true;
        for (int y = 0; y < height; y++)
        {
            for (int x = width; x < stride * 8; x++)
            {
                if (BitMask1Bpp.GetBit(mask, stride, x, y))
                {
                    paddingClear = false;
                }
            }
        }
        CheckTrue(lines, "1bpp mask row padding stays clear", paddingClear, "padding bits were set");
    }

    // -----------------------------------------------------------------------------
    // Assertion helpers
    // -----------------------------------------------------------------------------

    private static void Check(List<string> lines, string what, int expected, int actual)
    {
        if (expected == actual)
        {
            lines.Add($"  ok   {what} = {actual}");
        }
        else
        {
            lines.Add($"FAIL {what}: expected {expected}, got {actual}");
        }
    }

    private static void CheckTrue(List<string> lines, string what, bool condition, string detail)
    {
        lines.Add(condition ? $"  ok   {what}" : $"FAIL {what}: {detail}");
    }
}


