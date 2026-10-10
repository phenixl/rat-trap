using System.Runtime.InteropServices;
using Microsoft.Win32;
using RatTrap.Capture;
using RatTrap.Imaging;
using RatTrap.Interop;
using RatTrap.Pointer;

namespace RatTrap.Cli;

/// <summary>
/// Environment and assumption probe. This is the instrument that establishes, on the
/// machine actually in front of us, which of the design's assumptions hold.
/// </summary>
/// <remarks>
/// The load-bearing question it answers is the one the whole reconstruction pipeline
/// depends on: <b>does GDI BitBlt return the cursor while DXGI Desktop Duplication returns
/// the same scene without it?</b> If both are true then a pair of near-simultaneous captures
/// gives the solver a composite <c>D</c> and its background <c>B</c> for the same instant,
/// and alpha recovery stops being a search problem. The probe moves the cursor to a known
/// point, captures both, diffs them, and reports the difference's bounding box next to the
/// compositor's own pointer-shape bounding box so the two can be compared directly.
/// </remarks>
internal static class ProbeCommand
{
    public static int Run(CommandLine cl)
    {
        string? outDir = cl.Get("out");
        if (!string.IsNullOrWhiteSpace(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        Console.WriteLine("=== rat-trap probe ===");
        Console.WriteLine($"  options: {cl.DescribeOptions()}");
        Console.WriteLine($"  artefacts: {(string.IsNullOrWhiteSpace(outDir) ? "(none requested; pass --out <dir>)" : Path.GetFullPath(outDir))}");
        Section("1. process and system state");
        ProbeSystemState();

        Section("2. monitors");
        var monitors = Monitors.Enumerate();
        foreach (var m in monitors)
        {
            Console.WriteLine($"  {m}");
        }
        if (monitors.Count == 0)
        {
            Console.WriteLine("  FAIL: no monitors reported.");
            return 1;
        }

        Section("3. live system cursor");
        ProbeSystemCursor();

        var cursorPos = GetCursorPosition();
        var target = Monitors.FromPoint(cursorPos.X, cursorPos.Y);
        Console.WriteLine($"  cursor is on {target.DeviceName} {target.Rect}");

        Section($"4. DXGI Desktop Duplication on {target.DeviceName}");
        DxgiDuplicationSource? dxgi = null;
        try
        {
            dxgi = new DxgiDuplicationSource(target);
            Console.WriteLine($"  OK: image {dxgi.DuplicationDesc.Width}x{dxgi.DuplicationDesc.Height}, " +
                              $"rotation {dxgi.DuplicationDesc.Rotation}, " +
                              $"desktop image in system memory: {dxgi.DuplicationDesc.DesktopImageInSystemMemory}");
            Console.WriteLine($"  readback path: {(dxgi.UsesSystemMemoryMapping ? "MapDesktopSurface" : "staging texture (CopySubresourceRegion)")}");

            ProbeDxgiFrames(dxgi, outDir);

            Console.WriteLine();
            Console.WriteLine("  step-by-step readback diagnosis:");
            foreach (string line in dxgi.DiagnoseReadback(CenterRoi(target, 64), 200))
            {
                Console.WriteLine(line);
            }
        }
        catch (CaptureBackendUnavailableException ex)
        {
            Console.WriteLine($"  UNAVAILABLE: {ex.Message}");
        }

        if (dxgi is not null)
        {
            Section("5. pointer anchor calibration");
            CalibratePointerAnchor(dxgi, target);

            Section("6. inverse-capture experiment (GDI composite vs DXGI background)");
            RunInverseExperiment(dxgi, target, outDir);

            Section("7. decoded pointer shape");
            DumpPointerShape(dxgi, outDir);
        }

        dxgi?.Dispose();

        Section("done");
        Console.WriteLine("Notes: '.bgra' files are raw top-down BGRA; convert with probe/bgra2png.py.");
        return 0;
    }

    private static void Section(string title) => Console.WriteLine($"\n--- {title} ---");

    private static POINT GetCursorPosition()
    {
        var info = CURSORINFO.Create();
        if (User32.GetCursorInfo(ref info) && info.ptScreenPos.X != 0)
        {
            return info.ptScreenPos;
        }
        User32.GetCursorPos(out POINT p);
        return p;
    }

    private static void ProbeSystemState()
    {
        Console.WriteLine($"  GetDpiForSystem            = {User32.GetDpiForSystem()}");
        Console.WriteLine($"  SM_CXSCREEN x SM_CYSCREEN  = {User32.GetSystemMetrics(User32.SM_CXSCREEN)} x {User32.GetSystemMetrics(User32.SM_CYSCREEN)}");
        Console.WriteLine($"  SM_CXCURSOR x SM_CYCURSOR  = {User32.GetSystemMetrics(User32.SM_CXCURSOR)} x {User32.GetSystemMetrics(User32.SM_CYCURSOR)}");
        Console.WriteLine($"  SM_CXICON   x SM_CYICON    = {User32.GetSystemMetrics(User32.SM_CXICON)} x {User32.GetSystemMetrics(User32.SM_CYICON)}");
        Console.WriteLine($"  Environment.Is64BitProcess = {Environment.Is64BitProcess}");
        Console.WriteLine($"  Framework                 = {RuntimeInformation.FrameworkDescription}");

        // Applying the DPI awareness context after the fact is expected to fail when the
        // application manifest already set it, so report rather than treat as an error.
        bool dpiOk = User32.SetProcessDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Console.WriteLine($"  SetProcessDpiAwarenessContext(PMv2) = {dpiOk}" +
                          (dpiOk ? string.Empty : $" (already set, Win32 error {Marshal.GetLastPInvokeError()})"));

        Console.WriteLine("  registry:");
        Console.WriteLine($"    Control Panel\\Cursors\\CursorBaseSize      = {ReadDword(@"Control Panel\Cursors", "CursorBaseSize")}");
        Console.WriteLine($"    Software\\Microsoft\\Accessibility\\CursorSize = {ReadDword(@"Software\Microsoft\Accessibility", "CursorSize")}");
        Console.WriteLine($"    Control Panel\\Desktop\\LogPixels          = {ReadDword(@"Control Panel\Desktop", "LogPixels")}");
    }

    private static string ReadDword(string subKey, string name)
    {
        try
        {
            object? value = Registry.CurrentUser.OpenSubKey(subKey)?.GetValue(name);
            return value?.ToString() ?? "<unset>";
        }
        catch (Exception ex)
        {
            return $"<error: {ex.GetType().Name}>";
        }
    }

    private static void ProbeSystemCursor()
    {
        var info = CURSORINFO.Create();
        if (!User32.GetCursorInfo(ref info))
        {
            Console.WriteLine($"  GetCursorInfo FAILED (Win32 error {Marshal.GetLastPInvokeError()})");
            return;
        }

        Console.WriteLine($"  flags = 0x{info.flags:X} (SHOWING={((info.flags & CURSORINFO.CURSOR_SHOWING) != 0 ? 1 : 0)}, " +
                          $"SUPPRESSED={((info.flags & CURSORINFO.CURSOR_SUPPRESSED) != 0 ? 1 : 0)})");
        Console.WriteLine($"  ptScreenPos = {info.ptScreenPos}  hCursor = 0x{info.hCursor:X}");

        if (info.hCursor == IntPtr.Zero)
        {
            return;
        }

        if (!User32.GetIconInfo(info.hCursor, out ICONINFO icon))
        {
            Console.WriteLine($"  GetIconInfo FAILED (Win32 error {Marshal.GetLastPInvokeError()})");
            return;
        }

        try
        {
            Console.WriteLine($"  fIcon={icon.fIcon} hotspot=({icon.xHotspot},{icon.yHotspot})");
            DescribeBitmap("hbmColor", icon.hbmColor);
            DescribeBitmap("hbmMask", icon.hbmMask);
        }
        finally
        {
            if (icon.hbmColor != IntPtr.Zero) Gdi32.DeleteObject(icon.hbmColor);
            if (icon.hbmMask != IntPtr.Zero) Gdi32.DeleteObject(icon.hbmMask);
        }
    }

    private static void DescribeBitmap(string label, IntPtr bitmap)
    {
        if (bitmap == IntPtr.Zero)
        {
            // Documented: a monochrome cursor has no colour bitmap.
            Console.WriteLine($"  {label}: <null>");
            return;
        }

        var bm = default(BITMAP);
        if (Gdi32.GetObjectW(bitmap, Marshal.SizeOf<BITMAP>(), ref bm) == 0)
        {
            Console.WriteLine($"  {label}: GetObjectW failed");
            return;
        }

        Console.WriteLine($"  {label}: {bm.bmWidth}x{bm.bmHeight} bpp={bm.bmBitsPixel} planes={bm.bmPlanes} " +
                          $"isDib={bm.bmBits != IntPtr.Zero} widthBytes={bm.bmWidthBytes}");
    }

    private static void ProbeDxgiFrames(DxgiDuplicationSource dxgi, string? outDir)
    {
        var monitor = dxgi.Monitor;
        var roi = CenterRoi(monitor, 64);
        var scratch = new ArgbBitmap(roi.Width, roi.Height);

        int frames = 0, shapes = 0, timeouts = 0;
        ulong lastHash = 0;
        for (int i = 0; i < 40; i++)
        {
            var result = dxgi.Capture(roi, scratch, 120);
            if (result.Status == CaptureStatus.NoNewFrame)
            {
                timeouts++;
                continue;
            }
            if (result.Status != CaptureStatus.Captured)
            {
                Console.WriteLine($"  capture status {result.Status}: {result.Error}");
                break;
            }

            frames++;
            if (dxgi.TryGetPointerShape(out var shape))
            {
                shapes++;
                ulong hash = Fnv1a64(shape.Buffer);
                if (hash != lastHash)
                {
                    Console.WriteLine($"  shape change: type={shape.ShapeType} {shape.Width}x{shape.Height} " +
                                      $"pitch={shape.Pitch} hotspot=({shape.HotspotX},{shape.HotspotY}) " +
                                      $"bytes={shape.Buffer.Length} hash=0x{hash:X16}");
                    lastHash = hash;
                }
            }

            if (dxgi.TryGetPointerPositionInOutput(out POINT pos, out bool visible))
            {
                Console.WriteLine($"  frame {i}: pointerInOutput={pos} visible={visible}");
            }
        }

        Console.WriteLine($"  frames captured={frames} withShapeUpdate={shapes} timeouts={timeouts}");
        Console.WriteLine($"  ROI {roi} sample pixel = {scratch.GetPixel(roi.Width / 2, roi.Height / 2)}");

        if (outDir is not null)
        {
            SaveBgra(Path.Combine(outDir, "dxgi_roi.bgra"), scratch);
            Console.WriteLine($"  saved dxgi_roi.bgra ({scratch.Width}x{scratch.Height})");
        }
    }

    /// <summary>
    /// Works out what <c>DXGI_OUTDUPL_POINTER_POSITION</c> actually refers to on this
    /// machine, by measuring it against <c>GetCursorPos</c> instead of trusting the docs.
    /// </summary>
    private static void CalibratePointerAnchor(DxgiDuplicationSource dxgi, MonitorDescriptor monitor)
    {
        var original = GetCursorPosition();
        POINT probe = new() { X = monitor.Rect.Left + (monitor.Width / 2), Y = monitor.Rect.Top + (monitor.Height / 2) };

        User32.SetCursorPos(probe.X, probe.Y);
        Thread.Sleep(200);

        var roi = CenterRoi(monitor, 8);
        var scratch = new ArgbBitmap(roi.Width, roi.Height);

        POINT reported = default;
        bool visible = false, havePosition = false;
        for (int i = 0; i < 60; i++)
        {
            var result = dxgi.Capture(roi, scratch, 60);
            if (result.Status == CaptureStatus.Captured &&
                dxgi.TryGetPointerPositionInOutput(out reported, out visible))
            {
                havePosition = true;
                break;
            }
        }

        User32.GetCursorPos(out POINT settledCursor);

        if (!havePosition)
        {
            Console.WriteLine("  no pointer position was reported by the duplication session.");
        }
        else
        {
            POINT reportedDesktop = new() { X = monitor.Rect.Left + reported.X, Y = monitor.Rect.Top + reported.Y };
            int dx = settledCursor.X - reportedDesktop.X;
            int dy = settledCursor.Y - reportedDesktop.Y;

            Console.WriteLine($"  GetCursorPos (settled)      = {settledCursor}   <- the hotspot, by definition");
            Console.WriteLine($"  DXGI pointer (output space) = {reported}  visible={visible}");
            Console.WriteLine($"  DXGI pointer (desktop space)= {reportedDesktop}");
            Console.WriteLine($"  measured offset P - Q       = ({dx},{dy})");

            if (dxgi.TryGetPointerShape(out var shape))
            {
                Console.WriteLine($"  DXGI shape hotspot field    = ({shape.HotspotX},{shape.HotspotY})");
                bool matchesHotspotField = dx == shape.HotspotX && dy == shape.HotspotY;
                bool matchesZero = dx == 0 && dy == 0;
                Console.WriteLine($"  => offset equals shape hotspot field? {matchesHotspotField}");
                Console.WriteLine($"  => offset is zero (position IS the hotspot)? {matchesZero}");
                Console.WriteLine("     Interpretation \"offset == hotspot field\" means the reported position is the " +
                                  "shape's TOP-LEFT; \"zero\" means it is already the hotspot.");
            }
        }

        User32.SetCursorPos(original.X, original.Y);
        Thread.Sleep(120);
    }

    private static void RunInverseExperiment(DxgiDuplicationSource dxgi, MonitorDescriptor monitor, string? outDir)
    {
        POINT original = GetCursorPosition();

        // Park the cursor at a predictable point so the recovered geometry can be checked
        // against a known answer rather than against itself.
        POINT park = new()
        {
            X = monitor.Rect.Left + (monitor.Width / 2),
            Y = monitor.Rect.Top + (monitor.Height / 2),
        };
        User32.SetCursorPos(park.X, park.Y);
        Thread.Sleep(250);
        User32.GetCursorPos(out POINT settled);

        const int size = 128;
        var roi = new RECT
        {
            Left = Math.Clamp(settled.X - (size / 2), monitor.Rect.Left, monitor.Rect.Right - size),
            Top = Math.Clamp(settled.Y - (size / 2), monitor.Rect.Top, monitor.Rect.Bottom - size),
        };
        roi.Right = roi.Left + size;
        roi.Bottom = roi.Top + size;

        var background = new ArgbBitmap(size, size);
        var composite = new ArgbBitmap(size, size);

        // Prime the duplicator: the first AcquireNextFrame after a session starts (or after
        // the screen has been still for a while) legitimately reports nothing.
        CaptureResult backgroundResult = CaptureResult.NoNewFrame;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            backgroundResult = dxgi.Capture(roi, background, 250);
            if (backgroundResult.HasPixels)
            {
                break;
            }
        }

        CaptureResult compositeResult;
        using (var gdi = new GdiScreenSource(monitor))
        {
            compositeResult = gdi.Capture(roi, composite, 0);
        }

        Console.WriteLine($"  ROI = {roi} (128x128 centred on the parked cursor)");
        Console.WriteLine($"  background (DXGI) status = {backgroundResult.Status}" +
                          $"{(backgroundResult.IsCached ? " (replayed from cache: the desktop was unchanged)" : string.Empty)}" +
                          $" {backgroundResult.Error}");
        Console.WriteLine($"  composite  (GDI)  status = {compositeResult.Status} {compositeResult.Error}");

        if (!backgroundResult.HasPixels || !compositeResult.HasPixels)
        {
            Console.WriteLine("  FAIL: could not obtain both observations.");
            User32.SetCursorPos(original.X, original.Y);
            return;
        }

        // Difference at a threshold well above capture noise but below a cursor's contrast.
        const int threshold = 8;
        var diff = new ArgbBitmap(size, size);
        int differing = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        long totalDelta = 0;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                ArgbPixel b = background.GetPixel(x, y);
                ArgbPixel d = composite.GetPixel(x, y);
                int db = Math.Abs(b.B - d.B);
                int dg = Math.Abs(b.G - d.G);
                int dr = Math.Abs(b.R - d.R);
                int max = Math.Max(db, Math.Max(dg, dr));
                totalDelta += max;

                // Visualise the difference amplified, so a faint cursor is still visible.
                byte amplified = (byte)Math.Min(255, max * 8);
                diff.SetPixel(x, y, new ArgbPixel(amplified, amplified, amplified, 255));

                if (max > threshold)
                {
                    differing++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        Console.WriteLine($"  differing pixels (>{threshold}) = {differing} of {size * size} " +
                          $"({100.0 * differing / (size * size):F2}%), mean |delta| = {(double)totalDelta / (size * size):F2}");

        if (maxX >= 0)
        {
            var diffBox = new RECT { Left = minX, Top = minY, Right = maxX + 1, Bottom = maxY + 1 };
            Console.WriteLine($"  difference bounding box in ROI = {diffBox} ({diffBox.Width}x{diffBox.Height})");
            Console.WriteLine($"  difference bounding box on screen = " +
                              $"({roi.Left + diffBox.Left},{roi.Top + diffBox.Top})-({roi.Left + diffBox.Right},{roi.Top + diffBox.Bottom})");
            Console.WriteLine($"  parked cursor (hotspot) = ({settled.X},{settled.Y}); " +
                              $"hotspot offset inside the diff box = " +
                              $"({settled.X - (roi.Left + diffBox.Left)},{settled.Y - (roi.Top + diffBox.Top)})");

            if (dxgi.TryGetPointerShape(out var shape))
            {
                var decoded = PointerShapeDecoder.Decode(shape);
                Console.WriteLine($"  pointer shape from compositor = type {shape.ShapeType}, " +
                                  $"{shape.Width}x{shape.Height}, hotspot field ({shape.HotspotX},{shape.HotspotY})");
                if (decoded.Cursor is { } cursor && cursor.Image.TryGetAlphaBounds(0, out int ax, out int ay, out int aw, out int ah))
                {
                    Console.WriteLine($"  pointer shape alpha bbox = ({ax},{ay}) {aw}x{ah}");
                    Console.WriteLine($"  difference bbox         = ({diffBox.Left},{diffBox.Top}) {diffBox.Width}x{diffBox.Height}");
                    int sizeDeltaW = Math.Abs(aw - diffBox.Width);
                    int sizeDeltaH = Math.Abs(ah - diffBox.Height);
                    Console.WriteLine($"  => size agreement: dW={sizeDeltaW} dH={sizeDeltaH} " +
                                      $"({(sizeDeltaW <= 2 && sizeDeltaH <= 2 ? "MATCH" : "MISMATCH")})");
                    Console.WriteLine($"  => implied shape origin on screen = " +
                                      $"({roi.Left + diffBox.Left - ax},{roi.Top + diffBox.Top - ay}) under the " +
                                      "hypothesis that the diff box IS the shape");
                }
            }
        }
        else
        {
            Console.WriteLine("  no difference detected: GDI and DXGI agree, so GDI does NOT include the cursor " +
                              "(or the cursor was not visible).");
        }

        if (outDir is not null)
        {
            SaveBgra(Path.Combine(outDir, "roi_background_dxgi.bgra"), background);
            SaveBgra(Path.Combine(outDir, "roi_composite_gdi.bgra"), composite);
            SaveBgra(Path.Combine(outDir, "roi_diff.bgra"), diff);
            Console.WriteLine($"  saved roi_background_dxgi.bgra, roi_composite_gdi.bgra, roi_diff.bgra ({size}x{size})");
        }

        User32.SetCursorPos(original.X, original.Y);
    }

    private static void DumpPointerShape(DxgiDuplicationSource dxgi, string? outDir)
    {
        if (!dxgi.TryGetPointerShape(out var shape))
        {
            Console.WriteLine("  no pointer shape observed (the compositor only sends one when it changes).");
            return;
        }

        var decoded = PointerShapeDecoder.Decode(shape);
        Console.WriteLine($"  type={shape.ShapeType} {shape.Width}x{shape.Height} pitch={shape.Pitch} " +
                          $"hotspot=({shape.HotspotX},{shape.HotspotY}) bytes={shape.Buffer.Length}");

        if (!decoded.Success)
        {
            Console.WriteLine($"  decode FAILED: {decoded.Error}");
            return;
        }

        var cursor = decoded.Cursor!;
        Console.WriteLine($"  preferred encoding = {cursor.PreferredEncoding}");
        Console.WriteLine($"  notes = {decoded.Notes}");

        int opaque = 0, transparent = 0, partial = 0;
        foreach (var p in EnumeratePixels(cursor.Image))
        {
            if (p.A == 255) opaque++;
            else if (p.A == 0) transparent++;
            else partial++;
        }
        Console.WriteLine($"  alpha histogram: opaque={opaque} transparent={transparent} partial={partial}");

        if (cursor.Image.TryGetAlphaBounds(0, out int x, out int y, out int w, out int h))
        {
            Console.WriteLine($"  alpha bbox = ({x},{y}) {w}x{h}");
        }

        if (outDir is not null)
        {
            SaveBgra(Path.Combine(outDir, "pointer_shape.bgra"), cursor.Image);
            Console.WriteLine($"  saved pointer_shape.bgra ({cursor.Image.Width}x{cursor.Image.Height})");
        }
    }

    private static IEnumerable<ArgbPixel> EnumeratePixels(ArgbBitmap bmp)
    {
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                yield return bmp.GetPixel(x, y);
            }
        }
    }

    private static RECT CenterRoi(MonitorDescriptor monitor, int size)
    {
        int width = Math.Min(size, monitor.Width);
        int height = Math.Min(size, monitor.Height);
        int left = monitor.Rect.Left + ((monitor.Width - width) / 2);
        int top = monitor.Rect.Top + ((monitor.Height - height) / 2);
        return new RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }

    private static void SaveBgra(string path, ArgbBitmap bitmap)
    {
        File.WriteAllBytes(path, bitmap.Pixels);
    }

    internal static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }
}

