using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RatTrap.Capture;
using RatTrap.Imaging;
using RatTrap.Interop;
using RatTrap.Output;
using RatTrap.Pointer;
using RatTrap.Reconstruct;

namespace RatTrap.Ui;

/// <summary>
/// The interactive window, built directly on Win32.
/// </summary>
/// <remarks>
/// <para>
/// There is no framework here on purpose. WinForms and WPF are not supported by Native AOT, and
/// Avalonia would mean a NuGet dependency, which this machine cannot restore. Standard Win32
/// controls (<c>BUTTON</c>, <c>EDIT</c>) are drawn by the operating system and cost nothing, and the
/// only custom drawing is the preview, which is a DIB blit.
/// </para>
/// <para>
/// The window owns exactly one capture session, driven from a <c>WM_TIMER</c> tick on the UI
/// thread. A tick does at most one rectangle read plus, in reconstruction mode, a bounded number of
/// per-pixel updates, so the message loop stays responsive without threads. That matters because
/// everything here touches single-threaded COM (the duplicate device) and single-threaded Win32
/// (the GDI capture target).
/// </para>
/// <para>
/// State lives in static fields rather than in a per-window object because a
/// <c>[UnmanagedCallersOnly]</c> window procedure cannot capture an instance. There is exactly one
/// window, and the fields are only touched from that window's thread.
/// </para>
/// </remarks>
internal static class MainWindow
{
    private const int IdStart = 1001;
    private const int IdStop = 1002;
    private const int IdExportPng = 1003;
    private const int IdExportCur = 1004;
    private const int IdExportAni = 1005;
    private const int IdExportAll = 1006;
    private const int IdSelfTest = 1007;
    private const int IdLog = 1100;
    private const uint TimerId = 1;
    private const int TimerIntervalMs = 100;

    // --- window state -------------------------------------------------------------
    private static IntPtr _frame;
    private static IntPtr _canvas;
    private static IntPtr _log;
    private static IntPtr _font;
    private static bool _running;
    private static bool _sessionFailed;

    // --- capture session ----------------------------------------------------------
    private static MonitorDescriptor? _monitor;
    private static ICaptureSource? _source;
    private static IPointerShapeProvider? _shapeProvider;
    private static CursorReconstructor? _reconstructor;
    private static ArgbBitmap? _roi;
    private static RECT _roiRect;

    private static CursorImage? _cursor;
    private static ArgbBitmap? _preview;
    private static ulong _lastShapeHash;
    private static CaptureMode _mode;
    private static int _ticksSinceStart;

    /// <summary>Which acquisition strategy the session has settled on.</summary>
    private enum CaptureMode
    {
        /// <summary>Asking the compositor, which is exact; gives up after a couple of seconds.</summary>
        WaitingForCompositor,
        /// <summary>The compositor is drawing the cursor and hands over its bitmap.</summary>
        Compositor,
        /// <summary>An application is drawing its own cursor, so the solver is used.</summary>
        Reconstructing,
    }

    private static string _status = "Press Start to capture the cursor.";
    private static string _detail = string.Empty;
    private static readonly StringBuilder LogBuilder = new();

    // --- painting -----------------------------------------------------------------
    private static IntPtr _canvasDib;
    private static IntPtr _canvasBits;
    private static IntPtr _canvasMemoryDc;
    private static IntPtr _canvasPreviousBitmap;
    private static int _canvasWidth;
    private static int _canvasHeight;

    private static bool _selfCheck;
    private static int _selfCheckDeadlineMs;
    private static string? _selfCheckOutputPath;
    private static readonly StringBuilder SelfCheckLog = new();
    private static int _paintCount;
    private static int _timerCount;
    private static int _nativeExceptionCount;

    /// <summary>
    /// Creates the real window and pumps messages for a short while, then exits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists because a GUI is otherwise only verifiable by a human looking at it. This runs the
    /// genuine code paths - class registration, the <c>[UnmanagedCallersOnly]</c> window
    /// procedures, control creation, layout, the capture timer and the canvas paint - inside an
    /// automated run, so an access violation or an exception that never crosses back into native
    /// code turns into a non-zero exit status instead of a window that quietly fails to appear.
    /// </para>
    /// <para>
    /// It also writes a structured report to a file. That is not redundant with the exit code:
    /// launching a GUI-subsystem process and waiting for it is unreliable from some automation
    /// hosts (the wait itself hangs even though the process has already exited), so the report is
    /// what an automated check should actually read.
    /// </para>
    /// </remarks>
    public static int SelfCheck(int milliseconds, string? reportPath)
    {
        _selfCheck = true;
        _selfCheckDeadlineMs = milliseconds;
        _selfCheckOutputPath = reportPath;
        SelfCheckLog.Clear();
        SelfCheckLog.AppendLine($"rattrap GUI self-check, budget {milliseconds} ms");

        // Written immediately and rewritten at every milestone. If the process dies or hangs
        // somewhere unexpected, the file says exactly how far it got, which is the only reliable
        // way to diagnose a window procedure failure from outside the process.
        FlushSelfCheck();

        int result = Run();

        int failures = _nativeExceptionCount + (_paintCount == 0 ? 1 : 0) + (_timerCount == 0 ? 1 : 0);
        SelfCheckLog.AppendLine($"native window procedure exceptions : {_nativeExceptionCount}");
        SelfCheckLog.AppendLine($"canvas paints completed           : {_paintCount}");
        SelfCheckLog.AppendLine($"capture timer ticks               : {_timerCount}");
        SelfCheckLog.AppendLine($"session reported a failure        : {_sessionFailed}");
        SelfCheckLog.AppendLine($"capture source available          : {_source is not null}");
        SelfCheckLog.AppendLine($"pointer-shape provider available  : {_shapeProvider is not null}");
        SelfCheckLog.AppendLine($"final mode                        : {_mode}");
        SelfCheckLog.AppendLine($"cursor obtained                   : {_cursor is not null}");
        if (_cursor is not null)
        {
            SelfCheckLog.AppendLine($"cursor                            : {_cursor.Width}x{_cursor.Height} " +
                                    $"hotspot ({_cursor.HotspotX},{_cursor.HotspotY}) {_cursor.Provenance}");
        }
        SelfCheckLog.AppendLine($"result                            : {(result == 0 && failures == 0 ? "PASS" : "FAIL")}");
        SelfCheckLog.AppendLine("--- session log ---");
        SelfCheckLog.Append(LogBuilder.ToString());
        FlushSelfCheck();

        return result == 0 && failures == 0 ? 0 : 1;
    }

    private static void FlushSelfCheck()
    {
        if (string.IsNullOrWhiteSpace(_selfCheckOutputPath))
        {
            return;
        }

        try
        {
            File.WriteAllText(_selfCheckOutputPath, SelfCheckLog.ToString());
        }
        catch
        {
            // Reporting must never change the outcome.
        }
    }

    private static void Milestone(string text)
    {
        SelfCheckLog.AppendLine($"milestone: {text}");
        FlushSelfCheck();
    }

    public static int Run()
    {
        Milestone("Run entered");
        IntPtr instance = Kernel32.GetModuleHandleW(null);
        _font = Gdi32.GetStockObject(Gdi32.DEFAULT_GUI_FONT);

        bool registered;
        unsafe
        {
            // Taking the address of an [UnmanagedCallersOnly] method is the only AOT-safe way to
            // hand a managed callback to native code; a delegate would need runtime marshalling.
            registered = RegisterClass("RatTrapFrame", &FrameProc)
                      && RegisterClass("RatTrapCanvas", &CanvasProc);
        }

        if (!registered)
        {
            User32.MessageBoxW(IntPtr.Zero, "Failed to register the window classes.", "rat-trap", User32.MB_ICONERROR);
            return 1;
        }

        var monitors = Monitors.Enumerate();
        int dpi = (int)(monitors.Count > 0 ? monitors[0].DpiX : 96);
        int width = (int)(1120 * dpi / 96.0);
        int height = (int)(720 * dpi / 96.0);

        Milestone("classes registered");
        _frame = User32.CreateWindowExW(0, "RatTrapFrame", "rat-trap - cursor recovery",
            User32.WS_OVERLAPPEDWINDOW, 80, 60, width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_frame == IntPtr.Zero)
        {
            User32.MessageBoxW(IntPtr.Zero, "Failed to create the main window.", "rat-trap", User32.MB_ICONERROR);
            return 1;
        }

        Milestone($"frame window created, handle=0x{_frame:X}");
        User32.ShowWindow(_frame, User32.SW_SHOW);
        User32.UpdateWindow(_frame);

        if (_selfCheck)
        {
            // Exercise the whole pipeline rather than just creating a window: start a session,
            // force a synchronous paint, then let the timer run.
            StartSession();
            User32.UpdateWindow(_frame);
            if (_canvas != IntPtr.Zero)
            {
                User32.UpdateWindow(_canvas);
            }
            User32.SetTimer(_frame, (UIntPtr)(TimerId + 1), (uint)_selfCheckDeadlineMs, IntPtr.Zero);
        }

        Milestone("entering message loop");
        while (true)
        {
            int result = User32.GetMessageW(out MSG message, IntPtr.Zero, 0, 0);
            if (result == 0) break;
            if (result == -1) break;
            User32.TranslateMessage(ref message);
            User32.DispatchMessageW(ref message);
        }

        Milestone("message loop exited");
        Shutdown();
        return 0;
    }

    private static unsafe bool RegisterClass(string name, delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, IntPtr, IntPtr> proc)
    {
        IntPtr namePtr = Marshal.StringToHGlobalUni(name);
        try
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = 0x0002 | 0x0001, // CS_HREDRAW | CS_VREDRAW
                lpfnWndProc = (IntPtr)proc,
                hInstance = Kernel32.GetModuleHandleW(null),
                hCursor = User32.LoadCursorW(IntPtr.Zero, new IntPtr(32512)), // IDC_ARROW
                hbrBackground = IntPtr.Zero,
                lpszClassName = namePtr,
            };
            return User32.RegisterClassExW(ref windowClass) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }
    }

    // -----------------------------------------------------------------------------
    // Frame window
    // -----------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr FrameProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (message)
            {
                case User32.WM_CREATE:
                    CreateChildren(hwnd);
                    return IntPtr.Zero;

                case User32.WM_SIZE:
                    Layout(hwnd);
                    return IntPtr.Zero;

                case User32.WM_TIMER:
                    _timerCount++;
                    if (wParam.ToUInt64() == TimerId + 1)
                    {
                        // Self-check only: run for the requested time, then close the loop.
                        User32.KillTimer(hwnd, wParam);
                        User32.PostQuitMessage(0);
                        return IntPtr.Zero;
                    }
                    OnTimer();
                    return IntPtr.Zero;

                case User32.WM_COMMAND:
                    OnCommand((int)(wParam.ToUInt64() & 0xFFFF));
                    return IntPtr.Zero;

                case User32.WM_CLOSE:
                    StopSession();
                    User32.DestroyWindow(hwnd);
                    return IntPtr.Zero;

                case User32.WM_DESTROY:
                    ReleaseCanvasDib();
                    User32.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            // An exception must never cross back into native code.
            _nativeExceptionCount++;
            SelfCheckLog.AppendLine($"EXCEPTION in frame procedure: {ex}");
            AppendLog($"internal error: {ex.GetType().Name}: {ex.Message}");
            StopSession();
        }

        return User32.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void CreateChildren(IntPtr frame)
    {
        IntPtr instance = Kernel32.GetModuleHandleW(null);

        _canvas = User32.CreateWindowExW(0, "RatTrapCanvas", string.Empty,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_BORDER,
            0, 0, 10, 10, frame, IntPtr.Zero, instance, IntPtr.Zero);

        _log = User32.CreateWindowExW(0, "EDIT", string.Empty,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_BORDER | User32.WS_VSCROLL |
            User32.ES_MULTILINE | User32.ES_AUTOVSCROLL | User32.ES_READONLY,
            0, 0, 10, 10, frame, new IntPtr(IdLog), instance, IntPtr.Zero);

        CreateButton(frame, instance, IdStart, "Start");
        CreateButton(frame, instance, IdStop, "Stop");
        CreateButton(frame, instance, IdExportPng, "PNG");
        CreateButton(frame, instance, IdExportCur, "CUR");
        CreateButton(frame, instance, IdExportAni, "ANI");
        CreateButton(frame, instance, IdExportAll, "Export all");
        CreateButton(frame, instance, IdSelfTest, "Self-test");

        if (_font != IntPtr.Zero)
        {
            foreach (int id in new[] { IdStart, IdStop, IdExportPng, IdExportCur, IdExportAni, IdExportAll, IdSelfTest, IdLog })
            {
                User32.SendMessageW(User32.GetDlgItem(frame, id), User32.WM_SETFONT, (UIntPtr)(ulong)_font, new IntPtr(1));
            }
        }

        AppendLog("rat-trap");
        AppendLog("Start begins capture. The tool first asks the compositor for the operating system's own");
        AppendLog("cursor; if the OS is not drawing one (a fullscreen game drawing its own), it switches to");
        AppendLog("reconstruction, and you should move the pointer across a textured area.");
        SyncPreview();
    }

    private static void CreateButton(IntPtr parent, IntPtr instance, int id, string text)
        => User32.CreateWindowExW(0, "BUTTON", text,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_TABSTOP | User32.BS_PUSHBUTTON,
            0, 0, 10, 10, parent, new IntPtr(id), instance, IntPtr.Zero);

    private static void Layout(IntPtr frame)
    {
        User32.GetClientRect(frame, out RECT client);
        int dpi = (int)User32.GetDpiForWindow(frame);
        int scale = Math.Max(1, dpi / 96);
        int buttonHeight = 30 * scale;
        int gap = 8 * scale;
        int bottom = buttonHeight + (gap * 2);

        int contentTop = gap;
        int contentHeight = Math.Max(10, client.Height - bottom - contentTop);
        int canvasWidth = Math.Max(10, (int)((client.Width - (gap * 3)) * 0.56));
        int logWidth = Math.Max(10, client.Width - canvasWidth - (gap * 3));

        Move(_canvas, gap, contentTop, canvasWidth, contentHeight);
        Move(_log, gap + canvasWidth + gap, contentTop, logWidth, contentHeight);

        int x = gap;
        int y = client.Height - buttonHeight - gap;
        foreach (int id in new[] { IdStart, IdStop, IdExportPng, IdExportCur, IdExportAni, IdExportAll, IdSelfTest })
        {
            IntPtr control = User32.GetDlgItem(frame, id);
            int width = id switch
            {
                IdExportAll => 90 * scale,
                IdSelfTest => 90 * scale,
                _ => 70 * scale,
            };
            Move(control, x, y, width, buttonHeight);
            x += width + gap;
        }
    }

    private static void Move(IntPtr window, int x, int y, int width, int height)
    {
        if (window == IntPtr.Zero) return;
        User32.MoveWindow(window, x, y, width, height, true);
    }

    // -----------------------------------------------------------------------------
    // Commands
    // -----------------------------------------------------------------------------

    private static void OnCommand(int id)
    {
        switch (id)
        {
            case IdStart: StartSession(); break;
            case IdStop: StopSession(); break;
            case IdSelfTest: RunSelfTestInGui(); break;
            case IdExportPng: Export("png"); break;
            case IdExportCur: Export("cur"); break;
            case IdExportAni: Export("ani"); break;
            case IdExportAll: Export("all"); break;
        }
    }

    private static void StartSession()
    {
        if (_running) return;

        try
        {
            User32.GetCursorPos(out POINT cursor);
            var monitors = Monitors.Enumerate();
            if (monitors.Count == 0)
            {
                AppendLog("no monitors reported by the system");
                return;
            }

            _monitor = Monitors.FromPoint(cursor.X, cursor.Y);
            _sessionFailed = false;
            _cursor = null;
            _lastShapeHash = 0;
            _ticksSinceStart = 0;
            _mode = CaptureMode.WaitingForCompositor;

            AppendLog($"monitor: [{_monitor.Index}] {_monitor.DeviceName} {_monitor.Rect} dpi={_monitor.DpiX} scale={_monitor.Scale:F2}");

            // One capture source, for the whole session. A Desktop Duplication session is exclusive
            // per output: opening a second one on the same monitor fails with E_INVALIDARG, and an
            // earlier version of this window did exactly that (one duplication for the pointer shape,
            // another through the factory), after which the pointer shape never arrived at all. One
            // duplication serves both roles anyway, because it excludes only the system cursor while
            // still containing any cursor an application draws for itself.
            try
            {
                _source = CaptureFactory.Create(_monitor, CaptureBackend.Auto, out IReadOnlyList<string> diagnostics);
                foreach (string line in diagnostics) AppendLog(line);

                _shapeProvider = CaptureFactory.AsPointerShapeProvider(_source);
                if (_shapeProvider is null)
                {
                    AppendLog("this backend cannot report the compositor's pointer shape; the compositor path " +
                              "will use the GDI cursor bitmap instead");
                }

                _reconstructor = new CursorReconstructor();
                _roi = new ArgbBitmap(Math.Min(160, _monitor.Width), Math.Min(160, _monitor.Height));
                _status = _shapeProvider is null
                    ? "Reading the GDI cursor bitmap..."
                    : "Waiting for the compositor to report a pointer shape...";
            }
            catch (Exception ex)
            {
                AppendLog($"capture backend unavailable: {ex.Message}");
                _source = null;
                _shapeProvider = null;
            }

            _running = true;
            User32.SetTimer(_frame, (UIntPtr)TimerId, TimerIntervalMs, IntPtr.Zero);
            SyncPreview();
        }
        catch (Exception ex)
        {
            AppendLog($"start failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void StopSession()
    {
        if (_frame != IntPtr.Zero)
        {
            User32.KillTimer(_frame, (UIntPtr)TimerId);
        }
        _running = false;

        _source?.Dispose();
        _source = null;
        _shapeProvider = null;
        _reconstructor = null;
        _roi = null;
    }

    private static void OnTimer()
    {
        if (!_running || _monitor is null || _sessionFailed) return;

        try
        {
            _ticksSinceStart++;

            switch (_mode)
            {
                case CaptureMode.WaitingForCompositor:
                    if (TryCompositorTick())
                    {
                        _mode = CaptureMode.Compositor;
                        return;
                    }

                    // The compositor only announces a shape when it changes, so an untouched mouse can
                    // produce no report at all. Nudge once, then give up and reconstruct.
                    if (_ticksSinceStart == 10)
                    {
                        // Two independent attempts, because either can be the one that works. A fresh
                        // duplication always delivers a full initial frame including the pointer shape,
                        // and the one-pixel nudge supplies the position change the compositor waits for.
                        try
                        {
                            _source?.Reinitialize();
                            AppendLog("no compositor shape yet; reopened the duplication session to force a " +
                                      "fresh initial frame");
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"could not reopen the duplication session: {ex.Message}");
                        }

                        if (PointerNudge.TryNudge(out _))
                        {
                            AppendLog("also nudged the pointer one pixel so the compositor re-announces the cursor");
                        }
                    }

                    if (_ticksSinceStart > 20)
                    {
                        _mode = CaptureMode.Reconstructing;
                        AppendLog("no compositor pointer shape appeared: the operating system is not drawing the");
                        AppendLog("cursor, so an application is probably drawing its own. Switching to reconstruction.");
                        _status = "Reconstructing - move the pointer across a textured area.";
                    }
                    return;

                case CaptureMode.Compositor:
                    TryCompositorTick();
                    return;

                case CaptureMode.Reconstructing:
                    ReconstructionTick();
                    return;
            }
        }
        catch (Exception ex)
        {
            _sessionFailed = true;
            AppendLog($"capture stopped: {ex.GetType().Name}: {ex.Message}");
            _status = "Capture failed. See the log.";
            SyncPreview();
        }
    }

    /// <summary>Reads the compositor's pointer shape. Returns true once a cursor is in hand.</summary>
    private static bool TryCompositorTick()
    {
        if (_source is null || _monitor is null) return false;

        if (_shapeProvider is null)
        {
            // No duplication, so the compositor path can only fall back to the GDI cursor bitmap.
            var gdi = SystemCursorReader.Read();
            if (gdi.Cursor is null) return false;
            _cursor = gdi.Cursor;
            _status = $"GDI cursor bitmap: {_cursor.Width}x{_cursor.Height}, " +
                      $"hotspot ({_cursor.HotspotX},{_cursor.HotspotY})";
            _detail = gdi.Notes;
            AppendLog(_status);
            AppendLog($"  {gdi.Notes}");
            SyncPreview();
            return true;
        }

        User32.GetCursorPos(out POINT cursor);
        int width = Math.Min(96, _monitor.Width);
        int height = Math.Min(96, _monitor.Height);
        var scratch = EnsureRoi(width, height);
        RECT roi = RECT.FromSize(cursor.X - (width / 2), cursor.Y - (height / 2), width, height).ClampInside(_monitor.Rect);

        // A non-zero timeout matters here: GetFramePointerShape is only valid for a frame that was
        // actually acquired, and AcquireNextFrame(0) times out immediately when nothing has changed,
        // so a zero timeout silently never reports the cursor at all.
        var result = _source!.Capture(roi, scratch, 100);
        if (result.Status == CaptureStatus.NeedsReinitialize)
        {
            AppendLog($"compositor: reinitialising ({result.Error})");
            _source!.Reinitialize();
            return false;
        }

        if (!_shapeProvider.TryGetPointerShape(out PointerShapeSnapshot shape) || !shape.Visible)
        {
            return _cursor is not null;
        }

        ulong hash = Hashing.Fnv1a64(shape.Buffer);
        if (hash == _lastShapeHash && _cursor is not null)
        {
            return true;
        }

        _lastShapeHash = hash;
        var decoded = PointerShapeDecoder.Decode(shape);
        if (!decoded.Success)
        {
            AppendLog($"compositor: shape decode failed: {decoded.Error}");
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
        _cursor = new CursorImage(image.Image)
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

        _status = $"Compositor cursor: {_cursor.Width}x{_cursor.Height}, " +
                  $"hotspot ({_cursor.HotspotX},{_cursor.HotspotY}), {_cursor.PreferredEncoding}";
        _detail = $"type {shape.ShapeType}; {decoded.Notes}";
        AppendLog(_status);
        AppendLog($"  anchor ({anchor.Source}): {anchor.Explanation}");
        SyncPreview();
        return true;
    }

    /// <summary>Feeds one observation into the solver and periodically re-solves.</summary>
    private static void ReconstructionTick()
    {
        if (_reconstructor is null || _source is null || _monitor is null || _roi is null) return;

        User32.GetCursorPos(out POINT cursor);
        RECT roi = RECT.FromSize(cursor.X - (_roi.Width / 2), cursor.Y - (_roi.Height / 2),
            _roi.Width, _roi.Height).ClampInside(_monitor.Rect);

        var result = _source.Capture(roi, _roi, 20);
        if (result.Status == CaptureStatus.NeedsReinitialize)
        {
            _source.Reinitialize();
            return;
        }
        if (!result.HasPixels) return;

        _reconstructor.Observe(roi, _roi, cursor.X, cursor.Y);
        _roiRect = roi;

        _status = $"Reconstructing: {_reconstructor.FramesObserved} frames, " +
                  $"background {_reconstructor.BackgroundPixelCount}px";
        _detail = "Move the pointer across a textured area so the cursor varies against different backgrounds.";

        // Solving is far more expensive than observing, so only do it occasionally.
        if (_reconstructor.FramesObserved % 10 != 0) return;

        var solved = _reconstructor.Solve();
        if (!solved.Success)
        {
            return;
        }

        _cursor = solved.Cursor;
        _status = $"Reconstructed cursor: {_cursor!.Width}x{_cursor.Height}, " +
                  $"hotspot ({_cursor.HotspotX},{_cursor.HotspotY}); " +
                  $"{solved.Report.AlphaSolvedPixels} solved, " +
                  $"{solved.Report.OpaqueAssumedPixels + solved.Report.UnresolvedPixels} assumed, " +
                  $"{solved.Report.TransparentPixels} transparent";
        _detail = $"pairs={solved.Report.PairsRecorded}, sceneChanges={solved.Report.SceneChanges}, " +
                  $"encoding={_cursor.PreferredEncoding}";
        SyncPreview();
    }

    private static ArgbBitmap EnsureRoi(int width, int height)
    {
        if (_roi is null || _roi.Width != width || _roi.Height != height)
        {
            _roi = new ArgbBitmap(width, height);
        }
        return _roi;
    }

    // -----------------------------------------------------------------------------
    // Preview
    // -----------------------------------------------------------------------------

    /// <summary>Rebuilds the composited preview bitmap that the canvas blits.</summary>
    private static void SyncPreview()
    {
        if (_cursor is null)
        {
            _preview = null;
        }
        else
        {
            var checker = ArgbBitmap.CreateCheckerboard(_cursor.Width, _cursor.Height, 8,
                new ArgbPixel(0x50, 0x50, 0x50, 255), new ArgbPixel(0x9C, 0x9C, 0x9C, 255));
            for (int y = 0; y < _cursor.Height; y++)
            {
                for (int x = 0; x < _cursor.Width; x++)
                {
                    ArgbPixel src = _cursor.Image.GetPixel(x, y);
                    ArgbPixel under = checker.GetPixel(x, y);
                    int a = src.A, inv = 255 - a;
                    checker.SetPixel(x, y, new ArgbPixel(
                        (byte)((src.B * a + under.B * inv + 127) / 255),
                        (byte)((src.G * a + under.G * inv + 127) / 255),
                        (byte)((src.R * a + under.R * inv + 127) / 255),
                        255));
                }
            }
            _preview = checker;
        }

        if (_canvas != IntPtr.Zero)
        {
            User32.InvalidateRect(_canvas, IntPtr.Zero, false);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr CanvasProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        try
        {
            return CanvasProcCore(hwnd, message, wParam, lParam);
        }
        catch (Exception ex)
        {
            _nativeExceptionCount++;
            SelfCheckLog.AppendLine($"EXCEPTION in canvas procedure: {ex}");
            return User32.DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private static IntPtr CanvasProcCore(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case User32.WM_PAINT:
                _paintCount++;
                PaintCanvas(hwnd);
                return IntPtr.Zero;

            case 0x0014: // WM_ERASEBKGND: the paint handler fills everything.
                return new IntPtr(1);
        }
        return User32.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static unsafe void PaintCanvas(IntPtr hwnd)
    {
        IntPtr dc = User32.BeginPaint(hwnd, out PAINTSTRUCT paint);
        try
        {
            User32.GetClientRect(hwnd, out RECT client);
            if (client.Width <= 0 || client.Height <= 0) return;

            if (!EnsureCanvasDib(client.Width, client.Height))
            {
                return;
            }

            // Background.
            FillDib(client.Width, client.Height, new ArgbPixel(0x2A, 0x2A, 0x2A, 255));

            if (_preview is not null)
            {
                int scale = Math.Max(1, Math.Min(6,
                    Math.Min((client.Width - 24) / Math.Max(1, _preview.Width),
                             (client.Height - 60) / Math.Max(1, _preview.Height))));
                int drawnWidth = _preview.Width * scale;
                int drawnHeight = _preview.Height * scale;
                int offsetX = (client.Width - drawnWidth) / 2;
                int offsetY = Math.Max(8, (client.Height - drawnHeight) / 2);

                BlitNearest(offsetX, offsetY, _preview, scale);

                if (_cursor is not null && scale >= 2)
                {
                    int hx = offsetX + (_cursor.HotspotX * scale);
                    int hy = offsetY + (_cursor.HotspotY * scale);
                    DrawCrosshair(hx, hy, scale);
                }
            }

            var info = new BITMAPINFO { bmiHeader = BITMAPINFOHEADER.Create(client.Width, -client.Height, 32) };
            Gdi32.SetDIBitsToDevice(dc, 0, 0, (uint)client.Width, (uint)client.Height, 0, 0, 0, (uint)client.Height,
                _canvasBits, ref info, Gdi32.DIB_RGB_COLORS);

            // Text after the bitmap so it is not overwritten.
            Gdi32.SetBkMode(dc, Gdi32.TRANSPARENT);
            IntPtr previousFont = Gdi32.SelectObject(dc, _font);
            Gdi32.SetTextColor(dc, 0x00E8E8E8);

            var statusRect = RECT.FromSize(10, 8, client.Width - 20, 20);
            User32.DrawTextW(dc, _status, -1, ref statusRect, User32.DT_LEFT | User32.DT_TOP | User32.DT_NOPREFIX);

            if (_detail.Length > 0)
            {
                Gdi32.SetTextColor(dc, 0x00A8A8A8);
                var detailRect = RECT.FromSize(10, client.Height - 30, client.Width - 20, 24);
                User32.DrawTextW(dc, _detail, -1, ref detailRect, User32.DT_LEFT | User32.DT_TOP | User32.DT_NOPREFIX);
            }

            if (_preview is null)
            {
                Gdi32.SetTextColor(dc, 0x00909090);
                var emptyRect = RECT.FromSize(10, client.Height / 2, client.Width - 20, 40);
                User32.DrawTextW(dc, "No cursor yet. Press Start.", -1, ref emptyRect,
                    User32.DT_LEFT | User32.DT_TOP | User32.DT_NOPREFIX);
            }

            Gdi32.SelectObject(dc, previousFont);
        }
        finally
        {
            User32.EndPaint(hwnd, ref paint);
        }
    }

    private static bool EnsureCanvasDib(int width, int height)
    {
        if (_canvasDib != IntPtr.Zero && _canvasWidth == width && _canvasHeight == height)
        {
            return true;
        }

        ReleaseCanvasDib();

        IntPtr screenDc = User32.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;

        try
        {
            _canvasMemoryDc = Gdi32.CreateCompatibleDC(screenDc);
            var info = new BITMAPINFO { bmiHeader = BITMAPINFOHEADER.Create(width, -height, 32) };
            _canvasDib = Gdi32.CreateDIBSection(screenDc, ref info, Gdi32.DIB_RGB_COLORS, out _canvasBits, IntPtr.Zero, 0);
            if (_canvasDib == IntPtr.Zero || _canvasBits == IntPtr.Zero || _canvasMemoryDc == IntPtr.Zero)
            {
                ReleaseCanvasDib();
                return false;
            }
            _canvasPreviousBitmap = Gdi32.SelectObject(_canvasMemoryDc, _canvasDib);
            _canvasWidth = width;
            _canvasHeight = height;
            return true;
        }
        finally
        {
            User32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void ReleaseCanvasDib()
    {
        if (_canvasMemoryDc != IntPtr.Zero)
        {
            if (_canvasPreviousBitmap != IntPtr.Zero)
            {
                Gdi32.SelectObject(_canvasMemoryDc, _canvasPreviousBitmap);
                _canvasPreviousBitmap = IntPtr.Zero;
            }
            Gdi32.DeleteDC(_canvasMemoryDc);
            _canvasMemoryDc = IntPtr.Zero;
        }
        if (_canvasDib != IntPtr.Zero)
        {
            Gdi32.DeleteObject(_canvasDib);
            _canvasDib = IntPtr.Zero;
        }
        _canvasBits = IntPtr.Zero;
        _canvasWidth = 0;
        _canvasHeight = 0;
    }

    private static unsafe void FillDib(int width, int height, ArgbPixel colour)
    {
        byte* bits = (byte*)_canvasBits;
        for (int y = 0; y < height; y++)
        {
            byte* row = bits + ((long)y * width * 4);
            for (int x = 0; x < width; x++)
            {
                row[(x * 4)] = colour.B;
                row[(x * 4) + 1] = colour.G;
                row[(x * 4) + 2] = colour.R;
                row[(x * 4) + 3] = 255;
            }
        }
    }

    /// <summary>Nearest-neighbour scale blit, written out rather than delegated to GDI.</summary>
    /// <remarks>
    /// <c>StretchBlt</c> would apply a halftone or colour-on-colour filter depending on the mode
    /// that happens to be set, which turns an exact 3x view of a 1-pixel cursor edge into a blur.
    /// Cursor artwork has to be inspected at the pixel level, so the scaling is explicit.
    /// </remarks>
    private static unsafe void BlitNearest(int offsetX, int offsetY, ArgbBitmap source, int scale)
    {
        byte* bits = (byte*)_canvasBits;
        int drawnWidth = source.Width * scale;
        int drawnHeight = source.Height * scale;

        for (int y = 0; y < drawnHeight; y++)
        {
            int canvasY = offsetY + y;
            if (canvasY < 0 || canvasY >= _canvasHeight) continue;
            int sourceY = y / scale;
            byte* row = bits + ((long)canvasY * _canvasWidth * 4);

            for (int x = 0; x < drawnWidth; x++)
            {
                int canvasX = offsetX + x;
                if (canvasX < 0 || canvasX >= _canvasWidth) continue;
                int sourceIndex = ((sourceY * source.Width) + (x / scale)) * 4;
                row[(canvasX * 4)] = source.Pixels[sourceIndex];
                row[(canvasX * 4) + 1] = source.Pixels[sourceIndex + 1];
                row[(canvasX * 4) + 2] = source.Pixels[sourceIndex + 2];
                row[(canvasX * 4) + 3] = 255;
            }
        }
    }

    private static unsafe void DrawCrosshair(int centerX, int centerY, int scale)
    {
        byte* bits = (byte*)_canvasBits;
        int arm = 6 * scale;
        int thickness = Math.Max(1, scale / 2);

        void Plot(int x, int y)
        {
            for (int t = 0; t < thickness; t++)
            {
                int py = y + t;
                if (x < 0 || py < 0 || x >= _canvasWidth || py >= _canvasHeight) continue;
                byte* pixel = bits + (((long)py * _canvasWidth + x) * 4);
                pixel[0] = 0x30; pixel[1] = 0x30; pixel[2] = 0xFF; pixel[3] = 255;
            }
        }

        for (int d = -arm; d <= arm; d++)
        {
            Plot(centerX + d, centerY);
            Plot(centerX, centerY + d);
        }
    }

    // -----------------------------------------------------------------------------
    // Export and self-test
    // -----------------------------------------------------------------------------

    private static void Export(string what)
    {
        if (_cursor is null)
        {
            AppendLog("nothing to export yet - press Start and wait for a cursor");
            return;
        }

        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "rattrap-out");
            Directory.CreateDirectory(directory);

            if (what is "png" or "all")
            {
                string path = Path.Combine(directory, "cursor.png");
                PngWriter.WriteFile(path, _cursor.Image);
                AppendLog($"wrote {path}");
            }
            if (what is "cur" or "all")
            {
                string path = Path.Combine(directory, "cursor.cur");
                CursorFileWriter.WriteCur(path, _cursor);
                AppendLog($"wrote {path}");
            }
            if (what is "ani" or "all")
            {
                string path = Path.Combine(directory, "cursor.ani");
                AniWriter.WriteFile(path, [_cursor]);
                AppendLog($"wrote {path}");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"export failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RunSelfTestInGui()
    {
        try
        {
            var lines = new List<string>();
            lines.AddRange(SelfTest.CoreSelfTest.Run());
            lines.AddRange(OutputSelfTest.Run());

            int failures = lines.Count(l => l.TrimStart().StartsWith("FAIL", StringComparison.Ordinal));
            AppendLog($"self-test: {(failures == 0 ? "PASSED" : $"FAILED ({failures})")}");
            foreach (string line in lines.Where(l => l.TrimStart().StartsWith("FAIL", StringComparison.Ordinal)))
            {
                AppendLog("  " + line);
            }

            User32.MessageBoxW(_frame,
                failures == 0
                    ? "Self-test passed."
                    : $"Self-test failed with {failures} failure(s); see the log.",
                "rat-trap", User32.MB_OK | User32.MB_ICONINFORMATION | User32.MB_SETFOREGROUND);
        }
        catch (Exception ex)
        {
            AppendLog($"self-test error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void AppendLog(string line)
    {
        LogBuilder.AppendLine(line);
        if (LogBuilder.Length > 32000)
        {
            LogBuilder.Remove(0, 8000);
        }

        if (_log != IntPtr.Zero)
        {
            User32.SetWindowTextW(_log, LogBuilder.ToString());
            User32.SendMessageW(_log, 0x0115, UIntPtr.Zero, IntPtr.Zero); // EM_LINESCROLL to the end
        }
    }

    private static void Shutdown()
    {
        StopSession();
        ReleaseCanvasDib();
    }
}










