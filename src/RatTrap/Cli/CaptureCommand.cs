using System.Diagnostics;
using RatTrap.Capture;
using RatTrap.Imaging;
using RatTrap.Interop;
using RatTrap.Output;
using RatTrap.Pointer;
using RatTrap.Reconstruct;

namespace RatTrap.Cli;

/// <summary>
/// Acquires a cursor and writes it out as PNG, .cur and .ani.
/// </summary>
/// <remarks>
/// <para>
/// There are two fundamentally different ways to obtain the cursor, and which one applies is a
/// property of the application under the pointer, not a user preference. <c>--source</c> selects,
/// and <c>auto</c> tries the cheap one first:
/// </para>
/// <list type="table">
///   <item>
///     <term><c>compositor</c></term>
///     <description>The operating system is drawing the cursor. The compositor hands over its own
///     bitmap through Desktop Duplication's <c>GetFramePointerShape</c>, already scaled for the DPI
///     and the accessibility pointer-size setting. This is exact and takes about a second, and
///     nothing needs to be reconstructed.</description>
///   </item>
///   <item>
///     <term><c>reconstruct</c></term>
///     <description>The application draws its own cursor into its own frame, so the cursor exists
///     only as pixels in the captured image. The solver recovers alpha and colour from how the
///     cursor and the scene vary independently. See <see cref="CursorReconstructor"/>.</description>
///   </item>
/// </list>
/// <para>
/// These are not interchangeable fallbacks for one situation: <c>compositor</c> cannot see an
/// application-drawn cursor, because the operating system does not know about it, and
/// <c>reconstruct</c> has nothing to solve when the OS <i>is</i> drawing the cursor, because the
/// captured desktop excludes it. <c>auto</c> therefore inspects what is actually available rather
/// than guessing.
/// </para>
/// <para>
/// Both paths share a <b>single</b> capture source. A duplication session is exclusive per output,
/// so opening a second one on the same monitor fails and can break the first.
/// </para>
/// </remarks>
internal static class CaptureCommand
{
    public static int Run(CommandLine cl)
    {
        double seconds = cl.GetDouble("seconds", 10);
        int roiSize = cl.GetInt("roi", 160);
        string sourceMode = cl.Get("source", "auto").ToLowerInvariant();
        bool trim = cl.GetBool("trim");
        bool verbose = cl.GetBool("verbose");
        bool allowNudge = !cl.GetBool("no-nudge");

        var monitors = Monitors.Enumerate();
        if (monitors.Count == 0)
        {
            Console.Error.WriteLine("No monitors reported by the system.");
            return 2;
        }

        int monitorIndex = cl.GetInt("monitor", -1);
        MonitorDescriptor monitor;
        if (monitorIndex >= 0 && monitorIndex < monitors.Count)
        {
            monitor = monitors[monitorIndex];
        }
        else
        {
            User32.GetCursorPos(out POINT cursorPoint);
            monitor = Monitors.FromPoint(cursorPoint.X, cursorPoint.Y);
        }

        Console.WriteLine("rat-trap capture");
        Console.WriteLine($"  monitor : [{monitor.Index}] {monitor.DeviceName} {monitor.Rect} " +
                          $"{monitor.Width}x{monitor.Height} dpi={monitor.DpiX} scale={monitor.Scale:F2}");
        Console.WriteLine($"  source  : {sourceMode}{(sourceMode == "auto" ? " (compositor first, then reconstruction)" : string.Empty)}");
        Console.WriteLine($"  budget  : {seconds:F1}s, roi {roiSize}px");

        var notes = new List<string>();
        ICaptureSource source;
        try
        {
            source = CaptureFactory.Create(monitor, ParseBackend(cl.Get("backend", "auto")), out IReadOnlyList<string> diagnostics);
            foreach (string line in diagnostics)
            {
                notes.Add(line);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"No capture backend could be created for {monitor.DeviceName}: {ex.Message}");
            return 2;
        }

        using (source)
        {
            Console.WriteLine($"  backend : {source.Name}");
            Console.WriteLine();

            CursorImage? cursor = null;

            if (sourceMode is "auto" or "compositor")
            {
                cursor = TryCompositorCursor(source, monitor, roiSize, seconds, notes, verbose, allowNudge);

                if (cursor is null && sourceMode == "auto")
                {
                    Console.WriteLine();
                    Console.WriteLine("  No compositor cursor was available: the operating system is not drawing one,");
                    Console.WriteLine("  which is what happens when an application (typically a fullscreen game) draws");
                    Console.WriteLine("  its own cursor. Switching to reconstruction - keep moving the pointer across a");
                    Console.WriteLine("  textured area so the cursor varies against different backgrounds.");
                    Console.WriteLine();
                }
            }

            if (cursor is null && sourceMode is "auto" or "reconstruct")
            {
                cursor = TryReconstruction(source, monitor, roiSize, seconds, notes, verbose);
            }

            if (cursor is null)
            {
                Console.Error.WriteLine("Could not obtain a cursor.");
                foreach (string note in notes)
                {
                    Console.Error.WriteLine($"  note: {note}");
                }
                return 3;
            }

            Console.WriteLine();
            Console.WriteLine($"Cursor obtained via {cursor.Provenance}: {cursor.Width}x{cursor.Height}, " +
                              $"hotspot ({cursor.HotspotX},{cursor.HotspotY}), encoding {cursor.PreferredEncoding}");

            var counts = CountAlpha(cursor.Image);
            Console.WriteLine($"  alpha: {counts.Opaque} opaque, {counts.Partial} partial, {counts.Transparent} transparent");

            if (trim)
            {
                int beforeWidth = cursor.Width, beforeHeight = cursor.Height;
                var encoding = cursor.PreferredEncoding;
                cursor = cursor.Trim();
                Console.WriteLine($"  trimmed {beforeWidth}x{beforeHeight} -> {cursor.Width}x{cursor.Height}, " +
                                  $"hotspot now ({cursor.HotspotX},{cursor.HotspotY})");
                if (encoding != CursorEncoding.AlphaBlend32)
                {
                    notes.Add($"trimming dropped the {encoding} encoding, because the exact XOR or 1bpp masks " +
                              "describe the untrimmed geometry; the output is alpha-blended instead.");
                }
            }

            return WriteArtefacts(cursor, cl, notes);
        }
    }

    private static CaptureBackend ParseBackend(string name) => name.ToLowerInvariant() switch
    {
        "dxgi" or "duplication" => CaptureBackend.DxgiDuplication,
        "gdi" or "bitblt" => CaptureBackend.GdiBitBlt,
        _ => CaptureBackend.Auto,
    };

    // -----------------------------------------------------------------------------
    // Compositor path
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Asks the compositor for the cursor it is drawing, using the session's capture source.
    /// </summary>
    private static CursorImage? TryCompositorCursor(ICaptureSource source, MonitorDescriptor monitor,
        int roiSize, double seconds, List<string> notes, bool verbose, bool allowNudge)
    {
        if (source is not IPointerShapeProvider provider)
        {
            // GDI cannot report the compositor's shape, so the only remaining route is the GDI
            // cursor bitmap, at native size and with a less dependable alpha channel.
            var gdiOnly = SystemCursorReader.Read();
            if (gdiOnly.Cursor is null)
            {
                notes.Add($"{source.Name} cannot report the compositor's pointer shape, and reading the GDI " +
                          $"cursor bitmap failed too: {gdiOnly.Error}");
                Console.WriteLine($"  compositor: not available ({gdiOnly.Error})");
                return null;
            }

            notes.Add($"GDI cursor bitmap fallback: {gdiOnly.Notes}");
            Console.WriteLine($"  compositor: reading the GDI cursor bitmap ({gdiOnly.Cursor.Width}x{gdiOnly.Cursor.Height}); " +
                              "Desktop Duplication is unavailable, so the compositor's own scaled shape cannot be had");
            return gdiOnly.Cursor;
        }

        Console.WriteLine("  compositor: reading the operating system's own pointer shape...");
        if (verbose && source is DxgiDuplicationSource dxgiSource)
        {
            Console.WriteLine($"    duplication image {dxgiSource.DuplicationDesc.Width}x{dxgiSource.DuplicationDesc.Height}, " +
                              $"system-memory image: {dxgiSource.DuplicationDesc.DesktopImageInSystemMemory}");
        }

        int width = Math.Min(roiSize, monitor.Width);
        int height = Math.Min(roiSize, monitor.Height);
        var scratch = new ArgbBitmap(width, height);

        PointerShapeSnapshot shape = default;
        bool haveShape = false;
        bool reopened = false;
        bool nudged = false;
        ulong lastHash = 0;
        int shapeUpdates = 0;
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed.TotalSeconds < Math.Min(seconds, 6))
        {
            User32.GetCursorPos(out POINT cursor);
            RECT roi = CenteredRoi(monitor, cursor, width, height);

            var result = source.Capture(roi, scratch, 100);
            if (result.Status == CaptureStatus.NeedsReinitialize)
            {
                if (verbose) Console.WriteLine($"    reinitialising capture source: {result.Error}");
                try
                {
                    source.Reinitialize();
                }
                catch (Exception ex)
                {
                    notes.Add($"The capture source could not be re-established: {ex.Message}");
                    break;
                }
                continue;
            }

            if (provider.TryGetPointerShape(out PointerShapeSnapshot candidate))
            {
                ulong hash = Hashing.Fnv1a64(candidate.Buffer);
                if (!haveShape || hash != lastHash)
                {
                    shape = candidate;
                    lastHash = hash;
                    haveShape = true;
                    shapeUpdates++;
                    if (verbose)
                    {
                        Console.WriteLine($"    pointer shape update #{shapeUpdates}: type {candidate.ShapeType}, " +
                                          $"{candidate.Width}x{candidate.Height}, hotspot " +
                                          $"({candidate.HotspotX},{candidate.HotspotY}), {candidate.Buffer.Length} bytes");
                    }
                }
            }

            // Two escalation steps, because either can be the one that works, and the first is the
            // reliable one: a fresh duplication session always delivers a full initial frame,
            // including the pointer shape, whereas an established session only reports the shape when
            // it changes. Measured: an idle desktop produced 83 frames with no shape report until the
            // session was reopened.
            if (!haveShape && !reopened && stopwatch.Elapsed.TotalMilliseconds > 600)
            {
                reopened = true;
                Console.WriteLine("    no shape yet; reopening the duplication session to force a fresh initial frame");
                try
                {
                    source.Reinitialize();
                }
                catch (Exception ex)
                {
                    notes.Add($"The capture source could not be reopened: {ex.Message}");
                }
                continue;
            }

            // The session is already reporting frame after frame, so now supply the position change
            // the compositor is waiting for.
            if (!haveShape && !nudged && allowNudge && stopwatch.Elapsed.TotalMilliseconds > 1200)
            {
                nudged = true;
                if (PointerNudge.TryNudge(out _))
                {
                    Console.WriteLine("    no shape yet with an idle pointer; nudged it one pixel so the compositor");
                    Console.WriteLine("    re-announces the cursor (pass --no-nudge to skip this)");
                    continue;
                }
            }

            if (haveShape && stopwatch.Elapsed.TotalMilliseconds > 250)
            {
                break;
            }

            Thread.Sleep(8);
        }

        if (!haveShape)
        {
            notes.Add("The capture source reported no pointer shape. The compositor only sends one when it " +
                      "changes, so moving the pointer usually makes it appear; --no-nudge disables the " +
                      "one-pixel nudge this tool performs in that case.");
            Console.WriteLine("  compositor: no pointer shape was reported");

            var fallback = SystemCursorReader.Read();
            if (fallback.Cursor is not null)
            {
                notes.Add($"GDI cursor bitmap fallback: {fallback.Notes}");
                Console.WriteLine($"  compositor: falling back to the GDI cursor bitmap ({fallback.Cursor.Width}x{fallback.Cursor.Height})");
                return fallback.Cursor;
            }

            notes.Add($"GDI cursor bitmap fallback also failed: {fallback.Error}");
            return null;
        }

        var decoded = PointerShapeDecoder.Decode(shape);
        if (!decoded.Success)
        {
            notes.Add($"The compositor's pointer shape could not be decoded: {decoded.Error}");
            Console.WriteLine($"  compositor: decode failed ({decoded.Error})");
            return null;
        }

        POINT reported = default;
        bool havePosition = false;
        if (source is DxgiDuplicationSource dxgi)
        {
            havePosition = dxgi.TryGetPointerPositionInOutput(out reported, out _);
        }
        var anchor = PointerAnchorResolver.Resolve(shape, monitor, havePosition, reported, GetCursorPosition());
        notes.Add($"anchor: {anchor.Source} -> hotspot ({anchor.HotspotX},{anchor.HotspotY}). {anchor.Explanation}");
        notes.Add($"compositor shape: {decoded.Notes}");

        var image = decoded.Cursor!;
        var adjusted = new CursorImage(image.Image)
        {
            HotspotX = anchor.HotspotX,
            HotspotY = anchor.HotspotY,
            PreferredEncoding = image.PreferredEncoding,
            XorMaskBgra = image.XorMaskBgra,
            AndMask1Bpp = image.AndMask1Bpp,
            XorMask1Bpp = image.XorMask1Bpp,
            Provenance = image.Provenance,
            Note = image.Note,
        };

        Console.WriteLine($"  compositor: obtained {adjusted.Width}x{adjusted.Height} " +
                          $"(type {shape.ShapeType}, {shapeUpdates} shape update(s))");
        return adjusted;
    }

    // -----------------------------------------------------------------------------
    // Reconstruction path
    // -----------------------------------------------------------------------------

    private static CursorImage? TryReconstruction(ICaptureSource source, MonitorDescriptor monitor,
        int roiSize, double seconds, List<string> notes, bool verbose)
    {
        Console.WriteLine($"  reconstruct: accumulating from {source.Name}.");
        Console.WriteLine("               Move the pointer across a textured area so the cursor is seen against");
        Console.WriteLine("               different backgrounds; that variation is what identifies alpha.");

        int width = Math.Min(roiSize, monitor.Width);
        int height = Math.Min(roiSize, monitor.Height);
        var scratch = new ArgbBitmap(width, height);

        var reconstructor = new CursorReconstructor();
        var stopwatch = Stopwatch.StartNew();
        var progress = Stopwatch.StartNew();
        int frames = 0, observed = 0;

        while (stopwatch.Elapsed.TotalSeconds < seconds)
        {
            User32.GetCursorPos(out POINT cursor);
            RECT roi = CenteredRoi(monitor, cursor, width, height);

            var result = source.Capture(roi, scratch, 50);
            if (result.Status == CaptureStatus.NeedsReinitialize)
            {
                source.Reinitialize();
                continue;
            }
            if (result.HasPixels)
            {
                observed++;
                reconstructor.Observe(roi, scratch, cursor.X, cursor.Y);
            }
            frames++;

            if (progress.Elapsed.TotalSeconds >= 1.5)
            {
                progress.Restart();
                Console.WriteLine($"    {stopwatch.Elapsed.TotalSeconds,4:F1}s  frames={frames} observations={observed} " +
                                  $"backgroundPixels={reconstructor.BackgroundPixelCount}");
            }

            Thread.Sleep(4);
        }

        var solved = reconstructor.Solve();
        Console.WriteLine();
        Console.WriteLine("  reconstruction report:");
        foreach (string line in solved.Report.Describe().Split(Environment.NewLine))
        {
            Console.WriteLine("    " + line);
        }

        if (!solved.Success)
        {
            notes.Add("reconstruction did not produce a cursor; see the report above");
            return null;
        }

        if (verbose)
        {
            Console.WriteLine("  model map (?=unresolved a=solved O=assumed X=xor .=transparent):");
            foreach (string line in solved.DescribeKindMap().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                Console.WriteLine("    " + line);
            }
        }

        return solved.Cursor;
    }

    // -----------------------------------------------------------------------------
    // Output
    // -----------------------------------------------------------------------------

    private static int WriteArtefacts(CursorImage cursor, CommandLine cl, List<string> notes)
    {
        string outDir = cl.Get("out", "rattrap-out");
        Directory.CreateDirectory(outDir);

        string curPath = cl.Get("cur") ?? Path.Combine(outDir, "cursor.cur");
        string pngPath = cl.Get("png") ?? Path.Combine(outDir, "cursor.png");
        string aniPath = cl.Get("ani") ?? Path.Combine(outDir, "cursor.ani");
        string? previewPath = cl.Get("preview");

        Console.WriteLine();
        Console.WriteLine("Writing:");

        CursorFileWriter.WriteCur(curPath, cursor);
        Console.WriteLine($"  {Path.GetFullPath(curPath)}  (.cur, {cursor.PreferredEncoding}, hotspot " +
                          $"({cursor.HotspotX},{cursor.HotspotY}))");

        PngWriter.WriteFile(pngPath, cursor.Image);
        Console.WriteLine($"  {Path.GetFullPath(pngPath)}  (.png, {cursor.Width}x{cursor.Height}, straight alpha RGBA)");

        AniWriter.WriteFile(aniPath, [cursor]);
        Console.WriteLine($"  {Path.GetFullPath(aniPath)}  (.ani, single frame)");

        if (!string.IsNullOrWhiteSpace(previewPath))
        {
            var checker = ArgbBitmap.CreateCheckerboard(cursor.Width, cursor.Height, 8,
                new ArgbPixel(0x60, 0x60, 0x60, 255), new ArgbPixel(0xA0, 0xA0, 0xA0, 255));
            for (int y = 0; y < cursor.Height; y++)
            {
                for (int x = 0; x < cursor.Width; x++)
                {
                    ArgbPixel src = cursor.Image.GetPixel(x, y);
                    ArgbPixel under = checker.GetPixel(x, y);
                    int a = src.A, inv = 255 - a;
                    checker.SetPixel(x, y, new ArgbPixel(
                        (byte)((src.B * a + under.B * inv + 127) / 255),
                        (byte)((src.G * a + under.G * inv + 127) / 255),
                        (byte)((src.R * a + under.R * inv + 127) / 255),
                        255));
                }
            }
            PngWriter.WriteFile(previewPath, checker);
            Console.WriteLine($"  {Path.GetFullPath(previewPath)}  (.png preview over a checkerboard)");
        }

        if (notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Notes:");
            foreach (string note in notes)
            {
                Console.WriteLine($"  - {note}");
            }
        }

        return 0;
    }

    private static POINT GetCursorPosition()
    {
        User32.GetCursorPos(out POINT p);
        return p;
    }

    private static RECT CenteredRoi(MonitorDescriptor monitor, POINT center, int width, int height)
    {
        int left = center.X - (width / 2);
        int top = center.Y - (height / 2);
        return RECT.FromSize(left, top, width, height).ClampInside(monitor.Rect);
    }

    private readonly record struct PixelCount(int Opaque, int Partial, int Transparent);

    private static PixelCount CountAlpha(ArgbBitmap image)
    {
        int opaque = 0, partial = 0, transparent = 0;
        for (int i = 3; i < image.Pixels.Length; i += 4)
        {
            byte a = image.Pixels[i];
            if (a == 255) opaque++;
            else if (a == 0) transparent++;
            else partial++;
        }
        return new PixelCount(opaque, partial, transparent);
    }
}



