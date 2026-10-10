using System.Diagnostics;
using RatTrap.Capture;
using RatTrap.Imaging;
using RatTrap.Interop;
using RatTrap.Output;
using RatTrap.Pointer;
using RatTrap.Reconstruct;
using RatTrap.Settings;

namespace RatTrap.Ui;

/// <summary>The state a capture session is in.</summary>
public enum SessionPhase
{
    Idle = 0,
    /// <summary>Asking the compositor, which is exact; gives up after a couple of seconds.</summary>
    WaitingForCompositor,
    /// <summary>The compositor is drawing the cursor and hands over its bitmap.</summary>
    Compositor,
    /// <summary>An application is drawing its own cursor, so the solver is used.</summary>
    Reconstructing,
    /// <summary>Watching a fixed square for a crosshair.</summary>
    Crosshair,
    /// <summary>A cursor was recovered and saved.</summary>
    Done,
    /// <summary>Something went wrong; the message is in the log.</summary>
    Failed,
}

/// <summary>What a session step produced.</summary>
public readonly record struct SessionStep(bool Acquired, bool Saved, string? SavedBasePath);

/// <summary>
/// One capture attempt: acquire a cursor or crosshair, then save it.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from the window so that the same logic drives the GUI, and could drive a headless run,
/// without either owning the other. The session owns the capture source, the solver and the settings;
/// the window owns the pixels on screen and the message loop.
/// </para>
/// <para>
/// Everything here runs on the UI thread, one step per timer tick. That is a deliberate constraint
/// rather than a simplification: the capture source holds a single-threaded COM duplication session and
/// a GDI device context, and the solver touches a large per-pixel accumulator with no locking.
/// </para>
/// </remarks>
internal sealed class CaptureSession : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Action<string> _log;
    private readonly Stopwatch _elapsed = new();

    private ICaptureSource? _source;
    private IPointerShapeProvider? _shapeProvider;
    private CursorReconstructor? _reconstructor;
    private ArgbBitmap? _roi;
    private RECT _roiRect;
    private CaptureTarget _activeTarget;
    private MonitorDescriptor? _monitor;

    private bool _nudged;
    private bool _reopened;
    private int _ticks;
    private ulong _lastShapeHash;
    private bool _reportedNoShape;
    private bool _reportedFlatFrame;
    private bool _reportedProtectedContent;
    private SessionPhase _phase = SessionPhase.Idle;

    public CaptureSession(AppSettings settings, Action<string> log)
    {
        _settings = settings;
        _log = log;
    }

    public SessionPhase Phase => _phase;

    /// <summary>The recovered cursor or crosshair, once there is one.</summary>
    public CursorImage? Cursor { get; private set; }

    /// <summary>Short line describing what is happening, for the window's status strip.</summary>
    public string Status { get; private set; } = "Press Start to capture.";

    /// <summary>Second line with the detail that did not fit in the status.</summary>
    public string Detail { get; private set; } = string.Empty;

    public bool IsRunning => _phase is SessionPhase.WaitingForCompositor or SessionPhase.Compositor
        or SessionPhase.Reconstructing or SessionPhase.Crosshair;

    /// <summary>Frame counter, exposed so the window can throttle re-solving.</summary>
    public int FramesObserved => _reconstructor?.FramesObserved ?? _ticks;

    public void Start()
    {
        Stop();

        Cursor = null;
        _ticks = 0;
        _nudged = false;
        _reopened = false;
        _lastShapeHash = 0;
        _reportedNoShape = false;
        _elapsed.Restart();

        User32.GetCursorPos(out POINT cursor);
        var monitors = Monitors.Enumerate();
        if (monitors.Count == 0)
        {
            Fail("no monitors reported by the system");
            return;
        }

        _monitor = Monitors.FromPoint(cursor.X, cursor.Y);
        _activeTarget = _settings.Target;

        _log($"monitor: [{_monitor.Index}] {_monitor.DeviceName} {_monitor.Rect} " +
             $"dpi={_monitor.DpiX} scale={_monitor.Scale:F2}");
        _log($"target: {(_activeTarget == CaptureTarget.Crosshair ? "crosshair" : "cursor")}; " +
             $"budget {_settings.ReconstructSeconds}s");

        try
        {
            // One capture source for the whole session. A Desktop Duplication session is exclusive per
            // output, so opening a second one on the same monitor fails and can disturb the first.
            _source = CaptureFactory.Create(_monitor, CaptureBackend.Auto, out IReadOnlyList<string> diagnostics);
            foreach (string line in diagnostics)
            {
                _log(line);
            }
            _shapeProvider = CaptureFactory.AsPointerShapeProvider(_source);
        }
        catch (Exception ex)
        {
            Fail($"no capture backend is available for {_monitor.DeviceName}: {ex.Message}");
            return;
        }

        _reconstructor = _activeTarget == CaptureTarget.Crosshair
            ? new CursorReconstructor(new ReconstructionOptions { NeighbourDistance = _settings.CrosshairNeighbourDistance })
            : new CursorReconstructor();

        int side = _activeTarget == CaptureTarget.Crosshair
            ? _settings.CrosshairSize
            : Math.Min(160, Math.Min(_monitor.Width, _monitor.Height));
        side = Math.Clamp(side, 32, Math.Min(_monitor.Width, _monitor.Height));
        _roi = new ArgbBitmap(side, side);
        _roiRect = default;

        if (_activeTarget == CaptureTarget.Crosshair)
        {
            _phase = SessionPhase.Crosshair;
            Status = "Waiting for a foreground window to watch...";
            Detail = "Switch to the game. A square at the centre of its window is watched for a crosshair.";
            _log("crosshair mode: waiting for a foreground window that is not this one");
        }
        else if (_shapeProvider is not null)
        {
            _phase = SessionPhase.WaitingForCompositor;
            Status = "Asking the compositor for the cursor...";
            Detail = string.Empty;
        }
        else
        {
            _phase = SessionPhase.Reconstructing;
            Status = "Reconstructing - move the pointer across a textured area.";
            Detail = "This backend cannot report the compositor's pointer shape.";
        }
    }

    public void Stop()
    {
        _source?.Dispose();
        _source = null;
        _shapeProvider = null;
        _reconstructor = null;
        _roi = null;
        _elapsed.Reset();

        if (_phase is not SessionPhase.Done and not SessionPhase.Failed)
        {
            _phase = SessionPhase.Idle;
        }
    }

    public void Dispose() => Stop();

    private void Fail(string message)
    {
        _phase = SessionPhase.Failed;
        Status = "Capture failed. See the log.";
        Detail = message;
        _log($"error: {message}");
        Stop();
    }

    /// <summary>
    /// Advances the session by one observation.
    /// </summary>
    /// <remarks>
    /// Returns as soon as a cursor has been recovered <i>and saved</i>, which is the point of the whole
    /// exercise. Saving immediately rather than waiting for the user is deliberate: the previous version
    /// made them press an export button for each format, which is three chances to lose the result.
    /// </remarks>
    public SessionStep Tick()
    {
        if (!IsRunning || _source is null || _monitor is null || _reconstructor is null || _roi is null)
        {
            return default;
        }

        _ticks++;
        double budget = _settings.ReconstructSeconds;

        try
        {
            switch (_phase)
            {
                case SessionPhase.WaitingForCompositor:
                    if (TryCompositorTick())
                    {
                        return Complete();
                    }
                    if (_ticks == 10 && !_reopened)
                    {
                        // A fresh duplication always delivers a full initial frame, pointer shape included,
                        // whereas an established one reports the shape only when it changes.
                        _reopened = true;
                        try
                        {
                            _source.Reinitialize();
                            _log("no compositor shape yet; reopened the session to force a fresh initial frame");
                        }
                        catch (Exception ex)
                        {
                            _log($"could not reopen the compositor session: {ex.Message}");
                        }
                        if (_settings.NudgePointer)
                        {
                            if (PointerNudge.TryNudge(out _))
                            {
                                _log("also nudged the pointer one pixel so the compositor re-announces the cursor");
                            }
                        }
                    }
                    if (_ticks > 25)
                    {
                        _phase = SessionPhase.Reconstructing;
                        Status = "Reconstructing - move the pointer across a textured area.";
                        _log("the operating system is not drawing a cursor here, so an application is probably");
                        _log("drawing its own. Switching to reconstruction.");
                    }
                    return default;

                case SessionPhase.Compositor:
                    if (TryCompositorTick())
                    {
                        return Complete();
                    }
                    return default;

                case SessionPhase.Crosshair:
                    return CrosshairTick();

                case SessionPhase.Reconstructing:
                    return ReconstructTick(budget);

                default:
                    return default;
            }
        }
        catch (Exception ex)
        {
            Fail($"{ex.GetType().Name}: {ex.Message}");
            return default;
        }
    }

    private SessionStep Complete()
    {
        if (Cursor is null)
        {
            return default;
        }

        CursorImage cursor = Cursor;
        if (_settings.Trim)
        {
            int beforeWidth = cursor.Width, beforeHeight = cursor.Height;
            var encoding = cursor.PreferredEncoding;
            cursor = cursor.Trim();
            Cursor = cursor;
            _log($"trimmed {beforeWidth}x{beforeHeight} -> {cursor.Width}x{cursor.Height}; " +
                 $"hotspot now ({cursor.HotspotX},{cursor.HotspotY})");
            if (encoding != CursorEncoding.AlphaBlend32)
            {
                _log($"note: trimming dropped the {encoding} encoding, because the exact XOR or 1bpp masks " +
                     "describe the untrimmed geometry");
            }
        }

        Status = _activeTarget == CaptureTarget.Crosshair
            ? $"Crosshair saved: {cursor.Width}x{cursor.Height}, hotspot ({cursor.HotspotX},{cursor.HotspotY})"
            : $"Cursor saved: {cursor.Width}x{cursor.Height}, hotspot ({cursor.HotspotX},{cursor.HotspotY}), " +
              $"{cursor.Provenance}";

        string? savedBase = null;
        bool saved = false;
        if (_settings.AutoSave)
        {
            savedBase = Save(cursor);
            saved = savedBase is not null;
        }

        _phase = SessionPhase.Done;
        Stop();
        _phase = SessionPhase.Done;

        if (saved && _settings.PlaySound)
        {
            PlayCompletionSound();
        }

        return new SessionStep(Acquired: true, Saved: saved, SavedBasePath: savedBase);
    }

    /// <summary>Writes every supported format under one base name and returns that base path.</summary>
    private string? Save(CursorImage cursor)
    {
        try
        {
            string directory = _settings.ResolveSaveDirectory();
            Directory.CreateDirectory(directory);

            string baseName = NextBaseName(directory, cursor.HotspotX, cursor.HotspotY);
            string stem = Path.Combine(directory, baseName);

            CursorFileWriter.WriteCur(stem + ".cur", cursor);
            PngWriter.WriteFile(stem + ".png", cursor.Image);
            AniWriter.WriteFile(stem + ".ani", [cursor]);

            // A preview over a checkerboard, so that transparency is visible without opening the .cur.
            var checker = ArgbBitmap.CreateCheckerboard(cursor.Width, cursor.Height, 8,
                new ArgbPixel(0x60, 0x60, 0x60, 255), new ArgbPixel(0xA0, 0xA0, 0xA0, 255));
            for (int y = 0; y < cursor.Height; y++)
            {
                for (int x = 0; x < cursor.Width; x++)
                {
                    ArgbPixel source = cursor.Image.GetPixel(x, y);
                    ArgbPixel under = checker.GetPixel(x, y);
                    int alpha = source.A, inverse = 255 - alpha;
                    checker.SetPixel(x, y, new ArgbPixel(
                        (byte)((source.B * alpha + under.B * inverse + 127) / 255),
                        (byte)((source.G * alpha + under.G * inverse + 127) / 255),
                        (byte)((source.R * alpha + under.R * inverse + 127) / 255),
                        255));
                }
            }
            PngWriter.WriteFile(stem + ".preview.png", checker);

            _log($"saved {baseName}.cur / .png / .ani / .preview.png in {directory}");
            return stem;
        }
        catch (Exception ex)
        {
            _log($"save failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Builds the next <c>[hotspotX,hotspotY]_NNNN</c> name.
    /// </summary>
    /// <remarks>
    /// The hotspot is in the name because it is the one piece of metadata a cursor file does not make
    /// obvious at a glance, and because it makes two captures of the same cursor distinguishable. The
    /// four-digit sequence is chosen as one past the highest already present, so a re-run never overwrites
    /// an earlier capture - the failure mode that matters when the previous version wrote a fixed name.
    /// </remarks>
    private static string NextBaseName(string directory, int hotspotX, int hotspotY)
    {
        int highest = 0;
        try
        {
            foreach (string existing in Directory.EnumerateFiles(directory, "*_*.*"))
            {
                string name = Path.GetFileNameWithoutExtension(existing);
                int underscore = name.LastIndexOf('_');
                if (underscore < 0 || underscore == name.Length - 1)
                {
                    continue;
                }
                if (int.TryParse(name[(underscore + 1)..], out int sequence) && sequence > highest)
                {
                    highest = sequence;
                }
            }
        }
        catch (Exception)
        {
            // An unreadable directory simply means the numbering restarts; the file itself may still write.
        }

        return $"[{hotspotX},{hotspotY}]_{highest + 1:D4}";
    }

    /// <summary>
    /// Plays the completion cue through the system sound alias.
    /// </summary>
    /// <remarks>
    /// <c>SND_ASYNC</c> so that a slow or missing sound device can never stall the message loop, and
    /// <c>SND_NODEFAULT</c> so that a machine without the alias is silent rather than beeping.
    /// </remarks>
    private void PlayCompletionSound()
    {
        // SND_ASYNC so a slow or absent sound device can never stall the message loop, and
        // SND_NODEFAULT so a machine without the alias is silent rather than beeping the default.
        uint flags = User32.SND_ALIAS | User32.SND_ASYNC | User32.SND_NODEFAULT;
        if (User32.PlaySoundW(_settings.SoundAlias, IntPtr.Zero, flags))
        {
            return;
        }

        // The alias can be missing while the sound event itself is still configured, which is the case on
        // a machine whose sound scheme was customised. Reading the scheme's file and playing it directly
        // keeps the user's chosen sound instead of silently doing nothing.
        string? path = ResolveSoundSchemePath(_settings.SoundAlias);
        if (path is not null)
        {
            User32.PlaySoundW(path, IntPtr.Zero,
                User32.SND_FILENAME | User32.SND_ASYNC | User32.SND_NODEFAULT);
        }
    }

    /// <summary>Looks up the .wav a system sound event is mapped to, in the user's scheme first.</summary>
    private static string? ResolveSoundSchemePath(string alias)
    {
        string[] subKeys =
        [
            $@"HKEY_CURRENT_USER\AppEvents\Schemes\Apps\.Default\{alias}\.Current",
            $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\AppEvents\Schemes\Apps\.Default\{alias}\.Current",
        ];

        foreach (string subKey in subKeys)
        {
            try
            {
                object? value = Microsoft.Win32.Registry.GetValue(subKey, string.Empty, null);
                if (value is string text && text.Length > 0 && File.Exists(text))
                {
                    return text;
                }
            }
            catch (Exception)
            {
                // A missing or unreadable key just means trying the next one.
            }
        }
        return null;
    }

    // -----------------------------------------------------------------------------
    // Compositor
    // -----------------------------------------------------------------------------

    private bool TryCompositorTick()
    {
        if (_source is null || _monitor is null || _roi is null)
        {
            return false;
        }

        if (_shapeProvider is null)
        {
            // No duplication, so the compositor path can only read the GDI cursor bitmap.
            var fallback = SystemCursorReader.Read();
            if (fallback.Cursor is null)
            {
                return false;
            }
            Cursor = fallback.Cursor;
            _log($"GDI cursor bitmap: {Cursor.Width}x{Cursor.Height}, hotspot ({Cursor.HotspotX},{Cursor.HotspotY})");
            _log(fallback.Notes);
            return true;
        }

        User32.GetCursorPos(out POINT cursor);
        RECT roi = RECT.FromSize(cursor.X - (_roi.Width / 2), cursor.Y - (_roi.Height / 2),
            _roi.Width, _roi.Height).ClampInside(_monitor.Rect);

        var result = _source.Capture(roi, _roi, 30);
        if (result.Status == CaptureStatus.NeedsReinitialize)
        {
            _log($"reinitialising the compositor session: {result.Error}");
            _source.Reinitialize();
            return false;
        }

        if (!_shapeProvider.TryGetPointerShape(out PointerShapeSnapshot shape) || !shape.Visible)
        {
            return false;
        }

        ulong hash = Hashing.Fnv1a64(shape.Buffer);
        if (hash == _lastShapeHash && Cursor is not null)
        {
            return true;
        }
        _lastShapeHash = hash;

        var decoded = PointerShapeDecoder.Decode(shape);
        if (!decoded.Success)
        {
            if (!_reportedNoShape)
            {
                _reportedNoShape = true;
                _log($"the compositor's pointer shape could not be decoded: {decoded.Error}");
            }
            return false;
        }

        POINT reported = default;
        bool havePosition = false;
        if (_source is DxgiDuplicationSource dxgi)
        {
            havePosition = dxgi.TryGetPointerPositionInOutput(out reported, out _);
        }
        var anchor = PointerAnchorResolver.Resolve(shape, _monitor, havePosition, reported, cursor);

        var image = decoded.Cursor!;
        Cursor = new CursorImage(image.Image)
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

        _log($"compositor cursor: {Cursor.Width}x{Cursor.Height}, type {shape.ShapeType}, " +
             $"hotspot ({Cursor.HotspotX},{Cursor.HotspotY}), {Cursor.PreferredEncoding}");
        _log(decoded.Notes);
        Detail = anchor.Explanation;
        return true;
    }

    // -----------------------------------------------------------------------------
    // Reconstruction
    // -----------------------------------------------------------------------------

    private SessionStep ReconstructTick(double budget)
    {
        if (_source is null || _monitor is null || _reconstructor is null || _roi is null)
        {
            return default;
        }

        User32.GetCursorPos(out POINT cursor);
        RECT roi = RECT.FromSize(cursor.X - (_roi.Width / 2), cursor.Y - (_roi.Height / 2),
            _roi.Width, _roi.Height).ClampInside(_monitor.Rect);

        var result = _source.Capture(roi, _roi, 20);
        if (result.Status == CaptureStatus.NeedsReinitialize)
        {
            _source.Reinitialize();
            return default;
        }
        if (!result.HasPixels)
        {
            return default;
        }

        DiagnoseCapture(_roi, roi);

        _reconstructor.Observe(roi, _roi, cursor.X, cursor.Y);

        Status = $"Reconstructing: {_reconstructor.FramesObserved} frames, " +
                 $"background {_reconstructor.BackgroundPixelCount}px, {_elapsed.Elapsed.TotalSeconds:F0}s";
        Detail = "Move the pointer across a textured area so the cursor is seen against different backgrounds.";

        if (_reconstructor.FramesObserved % 10 != 0)
        {
            return default;
        }

        var solved = _reconstructor.Solve();
        if (solved.Success && solved.Cursor is { } candidate)
        {
            Cursor = candidate;
            _log(solved.Report.Describe());
            return Complete();
        }

        if (_elapsed.Elapsed.TotalSeconds >= budget)
        {
            foreach (string note in solved.Report.Notes)
            {
                _log(note);
            }
            Fail($"nothing could be solved within {budget:F0}s. " +
                 "Move the pointer across a textured area and try again.");
            return default;
        }

        return default;
    }

    // -----------------------------------------------------------------------------
    // Crosshair
    // -----------------------------------------------------------------------------

    private SessionStep CrosshairTick()
    {
        if (_source is null || _reconstructor is null || _roi is null)
        {
            return default;
        }

        if (!TryGetWatchRect(_roi.Width, _roi.Height, out RECT rect, out string source))
        {
            Status = "Waiting for a foreground window to watch...";
            Detail = "Switch to the game, or use the hot key while the game is focused.";
            return default;
        }

        if (_roiRect.Width == 0)
        {
            _roiRect = rect;
            _log($"crosshair: watching {rect} ({rect.Width}x{rect.Height}) at the centre of {source}");
            Status = "Watching for a crosshair...";
            Detail = "Aim at something that moves, so the scene behind the crosshair changes.";
        }
        else if (_roiRect.Left != rect.Left || _roiRect.Top != rect.Top)
        {
            // The geometry a crosshair sits on has to be stable for the accumulation to mean anything:
            // an offset must always refer to the same screen pixel.
            _log($"the watched window moved ({_roiRect} -> {rect}); restarting the accumulation");
            _reconstructor = new CursorReconstructor(
                new ReconstructionOptions { NeighbourDistance = _settings.CrosshairNeighbourDistance });
            _roiRect = rect;
        }

        var result = _source.Capture(_roiRect, _roi, 20);
        if (result.Status == CaptureStatus.NeedsReinitialize)
        {
            _source.Reinitialize();
            return default;
        }
        if (!result.HasPixels)
        {
            return default;
        }

        int hotspotX = _roiRect.Left + (_roiRect.Width / 2);
        int hotspotY = _roiRect.Top + (_roiRect.Height / 2);
        _reconstructor.Observe(_roiRect, _roi, hotspotX, hotspotY, BackgroundSource.SpatialNeighbourMedian);

        Status = $"Watching for a crosshair: {_reconstructor.FramesObserved} frames, " +
                 $"{_elapsed.Elapsed.TotalSeconds:F0}s";
        Detail = "Aim at something that moves. A static wall behind the crosshair leaves alpha unidentified.";

        if (_reconstructor.FramesObserved % 10 != 0)
        {
            return default;
        }

        var solved = _reconstructor.Solve();
        if (solved.Success && solved.Cursor is { } candidate)
        {
            Cursor = candidate;
            _log(solved.Report.Describe());
            if (_settings.VerboseModelMap)
            {
                foreach (string line in solved.DescribeKindMap().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
                {
                    _log("  " + line);
                }
            }
            return Complete();
        }

        if (_elapsed.Elapsed.TotalSeconds >= _settings.ReconstructSeconds)
        {
            foreach (string note in solved.Report.Notes)
            {
                _log(note);
            }
            Fail($"no crosshair could be solved within {_settings.ReconstructSeconds:F0}s");
            return default;
        }

        return default;
    }

    /// <summary>
    /// Turns a capture that cannot possibly contain a cursor into an actionable message.
    /// </summary>
    /// <remarks>
    /// There are three distinct reasons a fullscreen game yields nothing, and they need different
    /// answers, so the raw symptom - a region that is a single flat colour, or a frame the compositor
    /// flagged as protected - is reported with its likely cause instead of being left as a failure to
    /// find a cursor.
    /// <para>
    /// Note what is <i>not</i> on this list: insufficient privilege. Screen capture reads the composited
    /// desktop, which is a session object rather than a per-process one, so an unelevated process sees
    /// elevated windows perfectly well. Elevated is neither necessary nor sufficient for any of these.
    /// </para>
    /// </remarks>
    private void DiagnoseCapture(ArgbBitmap frame, RECT roi)
    {
        if (_source is DxgiDuplicationSource { LastFrameProtectedContentMasked: true } && !_reportedProtectedContent)
        {
            _reportedProtectedContent = true;
            _log("the compositor reports that this frame contains PROTECTED CONTENT and has masked it out. " +
                 "This is usually DRM playback, and no capture API can read it.");
        }

        if (_reportedFlatFrame || frame.Width < 16 || frame.Height < 16)
        {
            return;
        }

        // A single flat colour across the whole region is what a capture-protected or absent window looks
        // like. Sample sparsely; this only has to be cheap and decisive.
        ArgbPixel first = frame.GetPixel(2, 2);
        for (int y = 2; y < frame.Height; y += 7)
        {
            for (int x = 2; x < frame.Width; x += 7)
            {
                ArgbPixel sample = frame.GetPixel(x, y);
                if (Math.Abs(sample.B - first.B) > 3 || Math.Abs(sample.G - first.G) > 3 ||
                    Math.Abs(sample.R - first.R) > 3)
                {
                    return;
                }
            }
        }

        _reportedFlatFrame = true;
        _log($"the captured area {roi} is a single flat colour " +
             $"({first.R:X2}{first.G:X2}{first.B:X2}), so it contains no scene to separate a cursor from.");
        _log("  likely causes, in order of how often they are the answer:");
        _log("  1. the window called SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE), which hides it from");
        _log("     every capture API by design; that is deliberate and cannot be worked around");
        _log("  2. the game is in exclusive fullscreen and has bypassed desktop composition");
        _log("  3. HDR or a hardware overlay plane that duplication does not include");
        _log("  Running as administrator does not change any of these.");
    }

    /// <summary>
    /// Finds the square to watch: the centre of the foreground window, or the monitor centre as a
    /// fallback.
    /// </summary>
    /// <remarks>
    /// This program's own window is excluded, because clicking Start would otherwise make the tool watch
    /// itself - and the whole point of the global hot key is that the user can start a capture while the
    /// game keeps focus. The DWM extended frame bounds are preferred over <c>GetWindowRect</c> because
    /// the latter includes the invisible resize border and the drop shadow, which would put the centre
    /// slightly off for a windowed game.
    /// </remarks>
    private static bool TryGetWatchRect(int size, int height, out RECT rect, out string source)
    {
        rect = default;
        source = string.Empty;

        IntPtr foreground = User32.GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != MainWindow.WindowHandle &&
            User32.IsWindowVisible(foreground) && !User32.IsIconic(foreground))
        {
            RECT bounds;
            int hr = User32.DwmGetWindowAttribute(foreground, User32.DWMWA_EXTENDED_FRAME_BOUNDS,
                out bounds, (uint)System.Runtime.InteropServices.Marshal.SizeOf<RECT>());
            if (hr != 0)
            {
                User32.GetWindowRect(foreground, out bounds);
            }

            if (bounds.Width >= size && bounds.Height >= height)
            {
                rect = CentreWithin(bounds, size, height);
                source = $"window 0x{foreground:X}";
                return true;
            }
        }

        var monitors = Monitors.Enumerate();
        if (monitors.Count == 0)
        {
            return false;
        }

        User32.GetCursorPos(out POINT cursor);
        var monitor = Monitors.FromPoint(cursor.X, cursor.Y);
        if (monitor.Width < size || monitor.Height < height)
        {
            return false;
        }

        rect = CentreWithin(monitor.Rect, size, height);
        source = $"monitor {monitor.DeviceName} (no other window is in the foreground)";
        return true;
    }

    private static RECT CentreWithin(RECT bounds, int width, int height)
        => RECT.FromSize(
            bounds.Left + ((bounds.Width - width) / 2),
            bounds.Top + ((bounds.Height - height) / 2),
            width,
            height);
}



