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
    private int _hotspotX;
    private int _hotspotY;
    private long _observedFrames;
    private bool _reportedManualMode;
    private bool _reportedHoles;
    private CaptureTarget _activeTarget;
    private MonitorDescriptor? _monitor;

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

    /// <summary>
    /// The observation view's panels, refreshed only while <see cref="ObservationEnabled"/> is set.
    /// </summary>
    public ReconstructionView View { get; } = new();

    /// <summary>The bounded recording that makes the progress replayable.</summary>
    public ReconstructionHistory History { get; } = new();

    /// <summary>
    /// Whether the observation view is on screen, and therefore worth keeping up to date.
    /// </summary>
    /// <remarks>
    /// Gating on this is the whole reason watching costs nothing when nobody is watching. Refreshing the view
    /// means a <c>Solve()</c> and three panel fills per captured frame; recording means a downsampling pass and
    /// a slice of a fixed ring. Neither happens unless the view is actually selected, and the recording is kept
    /// when the capture stops so that a finished run can still be replayed.
    /// </remarks>
    public bool ObservationEnabled { get; set; }

    /// <summary>
    /// True while the compositor is handing the cursor over directly, which means there is nothing to solve.
    /// </summary>
    /// <remarks>
    /// Worth surfacing because it is otherwise indistinguishable from a view that is simply empty: when the
    /// operating system draws the cursor, GetFramePointerShape returns the finished bitmap and the
    /// reconstruction never runs at all, so a live view has no model to show and no amount of waiting will
    /// produce one.
    /// </remarks>
    public bool CompositorOwned { get; private set; }

    /// <summary>Pointer hotspot of the most recent observed frame, in desktop coordinates.</summary>
    public int HotspotX => _hotspotX;

    public int HotspotY => _hotspotY;

    /// <summary>Screen rectangle of the most recent capture.</summary>
    public RECT RoiRect => _roiRect;

    /// <summary>Frames captured since the session started.</summary>
    public long ObservedFrames => _observedFrames;

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
        _reopened = false;
        _lastShapeHash = 0;
        _reportedNoShape = false;
        _reportedManualMode = false;
        _reportedHoles = false;
        _elapsed.Restart();

        // Immediate feedback that the press was received. With the window hidden behind a fullscreen game
        // there is nothing else to look at, and the compositor shape can take a second to arrive.
        PlayFeedbackSound(_settings.StartSound, Audio.SoundCue.Start, "start");

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
            ? new CursorReconstructor(new ReconstructionOptions
            {
                NeighbourDistance = _settings.CrosshairNeighbourDistance,
                MaxOffsetRadius = OffsetRadiusForCrosshair(_settings.CrosshairSize),
            })
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
        CompositorOwned = false;
            Status = "Waiting for a foreground window to watch...";
            Detail = "Switch to the game. A square at the centre of its window is watched for a crosshair.";
            _log("crosshair mode: waiting for a foreground window that is not this one");
        }
        else if (_shapeProvider is not null)
        {
            // WaitingForCompositor is where the operating system is expected to hand over the cursor bitmap.
            // SessionPhase.Compositor is never assigned - the enum keeps it, but every compositor frame is
            // served from this state, so this is the flag that says the cursor is not ours to solve.
            _phase = SessionPhase.WaitingForCompositor;
            CompositorOwned = true;
            Status = "Asking the compositor for the cursor...";
            Detail = string.Empty;
        }
        else
        {
            _phase = SessionPhase.Reconstructing;
        CompositorOwned = false;
            Status = "Reconstructing - move the pointer across a textured area.";
            Detail = "This backend cannot report the compositor's pointer shape.";
        }
    }

    /// <summary>
    /// Tears the session down.
    /// </summary>
    /// <param name="announce">
    /// Play the stop cue. Only a stop the user asked for should be audible: <see cref="Start"/> resets by
    /// calling this, <see cref="Dispose"/> runs on window close, and a completed capture has already given
    /// its own cue. An unconditioned sound here would either play twice or announce a stop nobody requested.
    /// </param>
    public void Stop(bool announce = false)
    {
        if (announce)
        {
            PlayFeedbackSound(_settings.StopSound, Audio.SoundCue.Stop, "stop");
        }

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
        CompositorOwned = false;
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

        // The save cue is the one that means "the result exists". Stop is deliberately not announced here:
        // this is completion, not a stop the user asked for, and two cues at once would be noise.
        if (saved)
        {
            PlayFeedbackSound(_settings.SaveSound, Audio.SoundCue.Save, "save");
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
    /// Plays one of the three feedback cues.
    /// </summary>
    /// <param name="configured">The setting value: <c>builtin</c>, or a path to a WAV file.</param>
    /// <param name="cue">Which built-in cue to fall back to.</param>
    /// <param name="what">The role, for the log line only.</param>
    /// <remarks>
    /// This used to play a system sound alias. It does not any more: the embedded cues are guaranteed to
    /// exist, guaranteed to be three distinct sounds, and are redistributable, none of which was true of an
    /// entry in the machine's sound scheme. See <see cref="Audio.SoundPlayer"/>.
    /// </remarks>
    private void PlayFeedbackSound(string configured, Audio.SoundCue cue, string what)
    {
        if (!_settings.PlaySound)
        {
            return;
        }

        if (!AppSettings.IsBuiltinSound(configured))
        {
            string path = configured.Trim();
            if (File.Exists(path))
            {
                if (Audio.SoundPlayer.PlayFile(path))
                {
                    return;
                }
                _log($"could not play the {what} sound from {path}; using the built-in cue");
            }
            else
            {
                // Load-time normalisation already reported a value that is not a file, once, at start-up.
                // Repeating it on every capture is how a message stops being read.
                _log($"the {what} sound setting '{path}' is not a file; using the built-in cue");
            }
        }

        if (!Audio.SoundPlayer.Play(cue))
        {
            _log($"the built-in {what} cue could not be played (no audio device, or playback refused)");
        }
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

    /// <summary>
    /// Offset-grid radius needed to model a crosshair square of the given side.
    /// </summary>
    /// <remarks>
    /// The offset grid is cursor-local, and anything further from the centre than the radius does not merely
    /// score badly - it never enters the model. A 500-pixel-wide viewfinder against the default radius of 96
    /// therefore came out as a fragment: the outer pixels were dropped by an <c>if (Contains)</c> guard, the
    /// result looked like a partly captured shape, and nothing said the grid was the cause.
    /// <para>
    /// Half the side is exactly enough, and this path only ever models that square, so it is arithmetic rather
    /// than a guess. The options bound the radius at 512 and the crosshair side at 1024, so half the side is
    /// always in range. The cost is quadratic and worth stating: the grid is 88 bytes per offset, so a
    /// 256-pixel square is about 5.8 MB, the 500-pixel one about 22 MB, and the 1024 maximum about 92 MB.
    /// </para>
    /// </remarks>
    private static int OffsetRadiusForCrosshair(int crosshairSize) => (crosshairSize + 1) / 2;
    /// <summary>The hole fraction above which an automatically finishing capture keeps observing.</summary>
    private const double HoleLimit = 0.02;

    private double _optionsHoleLimit = HoleLimit;

    /// <summary>
    /// Whether a solved shape is complete enough for an automatic finish to accept it.
    /// </summary>
    /// <remarks>
    /// Holes are unresolved pixels surrounded by covered ones, so they sit inside the figure. Opaque interiors are
    /// covered and never count, which is why this does not reject a solid cursor: it fires on under-observation,
    /// not on opacity. Only the automatic path consults it. Finishing by hand overrides it, because the
    /// observation window exists precisely so that a person can look at the model and decide.
    /// </remarks>
    private static bool HolesAcceptable(ReconstructionReport report) =>
        report.CoveredPixels == 0 || report.InteriorHoleFraction <= HoleLimit;
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

        // Kept so that the observation view can place the model in screen coordinates. In cursor mode this
        // rect previously existed only as a local, which meant nothing outside this method could map a
        // cursor-local offset back to the pixel it belongs to.
        _roiRect = roi;
        _hotspotX = cursor.X;
        _hotspotY = cursor.Y;
        _observedFrames++;

        _reconstructor.Observe(roi, _roi, cursor.X, cursor.Y);
        RefreshObservation();

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
            if (_settings.AutoFinish && !HolesAcceptable(solved.Report))
            {
                // The shape came out with holes: pixels it needs that the evidence did not reach. Accepting it
                // would produce the gaps that were reported as "the threshold is too low", so the capture keeps
                // observing instead. It is said once, not every ten frames.
                if (!_reportedHoles)
                {
                    _reportedHoles = true;
                    _log($"a shape was solved but {solved.Report.InteriorHolePixels} of its " +
                         $"{solved.Report.CoveredPixels} covered pixels are holes " +
                         $"({solved.Report.InteriorHoleFraction:P1}, over the " +
                         $"{_optionsHoleLimit:P1} limit), so the capture keeps observing rather than saving a " +
                         "shape with gaps in it. Turn off 'Finish automatically' to accept it as it stands.");
                }
                return default;
            }

            Cursor = candidate;
            _log(solved.Report.Describe());
            return Complete();
        }

        if (_elapsed.Elapsed.TotalSeconds >= budget)
        {
            if (!_settings.AutoFinish)
            {
                // Manual mode: the budget is a progress report rather than a deadline, and the capture ends when
                // somebody says so. Saying it every ten frames would fill the log, so it is said once.
                if (!_reportedManualMode)
                {
                    _reportedManualMode = true;
                    _log($"still accumulating after {budget:F0}s because automatic finish is off; " +
                         "press the hot key or Stop when the observation view looks complete.");
                    foreach (string note in solved.Report.Notes)
                    {
                        _log(note);
                    }
                }
                Status = $"Accumulating: {_reconstructor.FramesObserved} frames, " +
                         $"{_elapsed.Elapsed.TotalSeconds:F0}s - press the hot key to finish";
                Detail = "Automatic finish is off. Watch the observation window and finish when it looks right.";
                return default;
            }

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
            int radius = OffsetRadiusForCrosshair(_settings.CrosshairSize);
            long gridOffsets = (long)((2 * radius) + 1) * ((2 * radius) + 1);
            _log($"crosshair: watching {rect} ({rect.Width}x{rect.Height}) at the centre of {source}");
            // Stated rather than left to be discovered: the grid has to cover the whole square or the outer
            // pixels are not modelled at all, and covering it costs 88 bytes per offset.
            _log($"crosshair: the offset grid is {(2 * radius) + 1}x{(2 * radius) + 1} " +
                 $"({gridOffsets * 88 / 1024.0 / 1024.0:F1} MB), sized to cover the square");
            Status = "Watching for a crosshair...";
            Detail = "Aim at something that moves, so the scene behind the crosshair changes.";
        }
        else if (_roiRect.Left != rect.Left || _roiRect.Top != rect.Top)
        {
            // The geometry a crosshair sits on has to be stable for the accumulation to mean anything:
            // an offset must always refer to the same screen pixel.
            _log($"the watched window moved ({_roiRect} -> {rect}); restarting the accumulation");
            _reconstructor = new CursorReconstructor(new ReconstructionOptions
            {
                NeighbourDistance = _settings.CrosshairNeighbourDistance,
                MaxOffsetRadius = OffsetRadiusForCrosshair(_settings.CrosshairSize),
            });
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
        _hotspotX = hotspotX;
        _hotspotY = hotspotY;
        _observedFrames++;

        _reconstructor.Observe(_roiRect, _roi, hotspotX, hotspotY, BackgroundSource.SpatialNeighbourMedian);
        RefreshObservation();

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
            if (_settings.AutoFinish && !HolesAcceptable(solved.Report))
            {
                if (!_reportedHoles)
                {
                    _reportedHoles = true;
                    _log($"a shape was solved but {solved.Report.InteriorHolePixels} of its " +
                         $"{solved.Report.CoveredPixels} covered pixels are holes " +
                         $"({solved.Report.InteriorHoleFraction:P1}, over the " +
                         $"{_optionsHoleLimit:P1} limit), so the capture keeps watching rather than saving a " +
                         "shape with gaps in it. Turn off 'Finish automatically' to accept it as it stands.");
                }
                return default;
            }

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
            if (!_settings.AutoFinish)
            {
                if (!_reportedManualMode)
                {
                    _reportedManualMode = true;
                    _log($"still watching after {_settings.ReconstructSeconds:F0}s because automatic finish is " +
                         "off; press the hot key or Stop when the observation view looks complete.");
                    foreach (string note in solved.Report.Notes)
                    {
                        _log(note);
                    }
                }
                Status = $"Watching: {_reconstructor.FramesObserved} frames, " +
                         $"{_elapsed.Elapsed.TotalSeconds:F0}s - press the hot key to finish";
                Detail = "Automatic finish is off. Watch the observation window and finish when it looks right.";
                return default;
            }

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
    /// Brings the observation view up to date and records the frame, if anyone is watching.
    /// </summary>
    /// <remarks>
    /// Called from the capture tick rather than from a display timer, so the panels describe the frames that
    /// were actually captured rather than whatever happened to be current when the screen was repainted. A
    /// view driven by its own timer shows a sampled, aliased version of the process - and for a reconstruction
    /// that converges over tens of frames, that is the difference between watching it fill in and watching it
    /// jump.
    /// </remarks>
    private void RefreshObservation()
    {
        if (!ObservationEnabled || _roi is null || _reconstructor is null)
        {
            return;
        }

        View.Refresh(_roi, _roiRect, _reconstructor, _hotspotX, _hotspotY);

        if (History.Enabled)
        {
            History.Record(View, _observedFrames, _hotspotX, _hotspotY);
        }
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










