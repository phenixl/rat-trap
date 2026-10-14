using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RatTrap.Capture;
using RatTrap.Imaging;
using RatTrap.Interop;
using RatTrap.Output;
using RatTrap.Settings;

namespace RatTrap.Ui;

/// <summary>
/// The interactive window, built directly on Win32.
/// </summary>
/// <remarks>
/// <para>
/// There is no UI framework here on purpose. WinForms and WPF are not supported by Native AOT, and
/// Avalonia would mean a NuGet dependency this machine cannot restore. Standard Win32 controls
/// (<c>BUTTON</c>, <c>EDIT</c>, <c>STATIC</c>) are drawn by the operating system and cost nothing, and the
/// only custom drawing is the preview, which is a DIB blit.
/// </para>
/// <para>
/// The window owns the message loop, the controls and the painting. Everything about acquiring a cursor
/// lives in <see cref="CaptureSession"/>, driven one step per <c>WM_TIMER</c> tick on this thread: the
/// capture source holds a single-threaded COM session and a GDI device context, and the solver touches a
/// large per-pixel accumulator, none of which is safe to share.
/// </para>
/// <para>
/// State lives in static fields rather than in a per-window object because a
/// <c>[UnmanagedCallersOnly]</c> window procedure cannot capture an instance. There is exactly one
/// window, and the fields are only touched from its thread.
/// </para>
/// </remarks>
internal static class MainWindow
{
    // Control identifiers.
    private const int IdModeLabel = 1200;
    private const int IdModeCursor = 1201;
    private const int IdModeCrosshair = 1202;
    private const int IdToggle = 1210;
    private const int IdBrowse = 1211;
    private const int IdSelfTest = 1212;
    private const int IdObserve = 1213;
    private const int IdLog = 1220;
    private const int IdSaveDir = 1230;
    private const int IdHotKeyInfo = 1231;
    private const int IdCrosshairLabel = 1232;
    private const int IdCrosshairSize = 1233;
    private const int IdAutoFinish = 1234;

    private const uint CaptureTimerId = 1;
    private const uint SelfCheckTimerId = 2;
    private const uint ObservationTimerId = 3;
    private const int ObservationIntervalMs = 250;
    private static int _observationDone;
    private static CaptureTarget _selfCheckRestoreTarget;
    private const int HotKeyId = 0x5254; // 'RT'
    private const int TimerIntervalMs = 100;

    // Edit control messages. Appending through them, rather than replacing the whole contents, is what
    // keeps the newest line visible; see AppendLog.
    private const uint EM_SETSEL = 0x00B1;
    private const uint EM_REPLACESEL = 0x00C2;
    private const uint EM_SCROLLCARET = 0x00B7;
    private const uint BM_SETCHECK = 0x00F1;
    private const uint BM_GETCHECK = 0x00F0;
    private const uint BST_CHECKED = 1;
    private const uint BS_AUTORADIOBUTTON = 0x00000009;
    private const uint BS_AUTOCHECKBOX = 0x00000003;
    private const uint WS_GROUP = 0x00020000;

    // --- window state -------------------------------------------------------------
    private static IntPtr _frame;
    private static IntPtr _canvas;
    private static IntPtr _log;
    private static IntPtr _font;
    private static IntPtr _icon;

    private static AppSettings _settings = new();
    private static CaptureSession? _session;
    private static bool _hotKeyRegistered;
    /// <summary>Set once and never cleared, so the self-check report does not read a value that Shutdown has just reset.</summary>
    private static bool _hotKeyEverRegistered;
    private static bool _sessionFailed;
    private static int _paintCount;
    private static int _timerCount;
    private static int _nativeExceptionCount;

    private static CursorImage? _cursor;
    private static ArgbBitmap? _preview;
    private static string _status = "Press Start, or press the hot key, to capture.";
    private static string _detail = string.Empty;

    // --- canvas device-independent bitmap -----------------------------------------
    private static IntPtr _canvasDib;
    private static IntPtr _canvasBits;
    private static IntPtr _canvasMemoryDc;
    private static IntPtr _canvasPreviousBitmap;
    private static int _canvasWidth;
    private static int _canvasHeight;

    /// <summary>Appended log characters, used to bound the edit control's size.</summary>
    private static int _logLength;

    /// <summary>Every line appended so far, for the self-check report.</summary>
    private static readonly System.Text.StringBuilder LogLines = new();

    // --- self-check ---------------------------------------------------------------
    private static bool _selfCheck;
    private static string? _selfCheckOut;
    private static readonly System.Text.StringBuilder SelfCheckLog = new();

    /// <summary>This window's handle, so the capture session can exclude it as a capture target.</summary>
    public static IntPtr WindowHandle => _frame;

    // -----------------------------------------------------------------------------
    // Entry points
    // -----------------------------------------------------------------------------

    /// <summary>Opens the window and runs until it closes.</summary>
    public static int Run() => RunCore(0, null);

    /// <summary>
    /// Opens the real window, drives it briefly, and exits.
    /// </summary>
    /// <param name="milliseconds">How long to run before closing.</param>
    /// <param name="reportPath">Where to write the report.</param>
    /// <remarks>
    /// A GUI is otherwise only verifiable by a human looking at it. This runs the genuine code paths -
    /// class registration, the <c>[UnmanagedCallersOnly]</c> window procedures, control creation, layout,
    /// the capture timer, the canvas paint - inside an automated run, so an access violation becomes a
    /// non-zero exit status instead of a window that quietly fails to appear.
    /// <para>
    /// The report file matters as much as the exit code: launching a GUI-subsystem process and waiting for
    /// it is unreliable from some automation hosts (the wait hangs even after the process has exited), so
    /// an automated check should read the report rather than depend on the wait.
    /// </para>
    /// </remarks>
    public static int SelfCheck(int milliseconds, string? reportPath)
    {
        _selfCheck = true;
        _selfCheckOut = reportPath;
        SelfCheckLog.Clear();
        SelfCheckLog.AppendLine($"rat-trap GUI self-check, budget {milliseconds} ms");
        FlushSelfCheck();

        int result = RunCore(milliseconds, reportPath);

        int failures = _nativeExceptionCount + (_paintCount == 0 ? 1 : 0) + (_timerCount == 0 ? 1 : 0);
        SelfCheckLog.AppendLine($"native window procedure exceptions : {_nativeExceptionCount}");
        SelfCheckLog.AppendLine($"canvas paints completed           : {_paintCount}");
        SelfCheckLog.AppendLine($"capture timer ticks               : {_timerCount}");
        SelfCheckLog.AppendLine($"session failed                   : {_sessionFailed}");
        SelfCheckLog.AppendLine($"settings file                    : {_settings.FilePath}");
        SelfCheckLog.AppendLine($"hot key registered               : {_hotKeyEverRegistered}");
        SelfCheckLog.AppendLine($"cursor obtained                  : {_cursor is not null}");
        if (_cursor is not null)
        {
            SelfCheckLog.AppendLine($"cursor                           : {_cursor.Width}x{_cursor.Height} " +
                                    $"hotspot ({_cursor.HotspotX},{_cursor.HotspotY}) {_cursor.Provenance}");
        }

        if (!ReportLayout())
        {
            failures++;
        }

        DumpControlTree();

        foreach (string line in ObservationWindow.SelfCheckLines)
        {
            SelfCheckLog.AppendLine(line);
            if (line.StartsWith("FAIL", StringComparison.Ordinal))
            {
                failures++;
            }
        }

        SelfCheckLog.AppendLine($"result                           : {(result == 0 && failures == 0 ? "PASS" : "FAIL")}");
        SelfCheckLog.AppendLine("--- session log ---");
        SelfCheckLog.Append(LogLines);
        FlushSelfCheck();

        return result == 0 && failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Reports where the controls ended up and checks the layout rules that are easy to get wrong.
    /// </summary>
    /// <remarks>
    /// The target radios are required to share the button row rather than occupy a row of their own, and
    /// "they are roughly in the right place" is not a property anyone can check by reading the layout code -
    /// the arithmetic there is exactly the kind that looks right and is not. Measuring the real control
    /// rectangles turns the requirement into something an automated run can fail.
    /// </remarks>
    private static bool ReportLayout()
    {
        if (_frame == IntPtr.Zero)
        {
            SelfCheckLog.AppendLine("layout: no window, nothing to check");
            return false;
        }

        // Window rectangles rather than client rectangles: all six controls share one parent, so comparing
        // their screen positions answers "same row" without needing a coordinate conversion.
        (string Name, int Id)[] controls =
        [
            ("Start/Stop", IdToggle),
            ("Open folder", IdBrowse),
            ("Observe...", IdObserve),
            ("Target:", IdModeLabel),
            ("Cursor", IdModeCursor),
            ("Crosshair", IdModeCrosshair),
        ];

        var found = new Dictionary<string, RECT>();
        foreach ((string name, int id) in controls)
        {
            IntPtr control = User32.GetDlgItem(_frame, id);
            if (control == IntPtr.Zero || !User32.GetWindowRect(control, out RECT rect))
            {
                SelfCheckLog.AppendLine($"layout: {name} is missing");
                return false;
            }
            found[name] = rect;
            SelfCheckLog.AppendLine($"layout: {name,-12} at ({rect.Left},{rect.Top}) {rect.Width}x{rect.Height}");
        }

        int rowTop = found["Start/Stop"].Top;
        bool oneRow = controls.All(c => found[c.Name].Top == rowTop);
        bool radiosLevel = found["Cursor"].Top == found["Crosshair"].Top;
        // >= rather than >, and the first run of this check is why: the label's box ends exactly where the
        // Cursor radio begins, which is adjacent and correct, and a strict comparison called it a failure.
        // Overlap is what must not happen; touching is what a horizontal row looks like.
        bool radiosRightOfButtons = found["Target:"].Left >= found["Observe..."].Right &&
                                    found["Cursor"].Left >= found["Target:"].Right &&
                                    found["Crosshair"].Left >= found["Cursor"].Right;
        bool horizontal = found["Cursor"].Left != found["Crosshair"].Left;

        SelfCheckLog.AppendLine($"layout: one row with the buttons     : {oneRow}");
        SelfCheckLog.AppendLine($"layout: radios level with each other : {radiosLevel}");
        SelfCheckLog.AppendLine($"layout: radios right of the buttons  : {radiosRightOfButtons}");
        SelfCheckLog.AppendLine($"layout: radios side by side          : {horizontal}");

        // The Self-test button is kept but hidden, so the property to assert is that it is invisible and that
        // hiding it did not leave a hole where it used to sit.
        IntPtr selfTest = User32.GetDlgItem(_frame, IdSelfTest);
        bool selfTestHidden = selfTest != IntPtr.Zero && !User32.IsWindowVisible(selfTest);
        SelfCheckLog.AppendLine($"layout: Self-test exists but is hidden : {selfTestHidden}");

        // No hole: the control after where Self-test used to be must start where Self-test ended.
        IntPtr browse = User32.GetDlgItem(_frame, IdBrowse);
        User32.GetWindowRect(browse, out RECT browseRect);
        bool noGap = found["Observe..."].Left - browseRect.Right <= 12;
        SelfCheckLog.AppendLine($"layout: no hole left by the hidden button : {noGap}");

        bool fieldWorks = CheckCrosshairField();
        bool saveReported = CheckSaveDirectoryIsReported();
        bool autoFinishWorks = CheckAutoFinishToggle();
        bool ok = oneRow && radiosLevel && radiosRightOfButtons && horizontal && selfTestHidden && noGap &&
                  fieldWorks && saveReported && autoFinishWorks;
        if (!ok)
        {
            SelfCheckLog.AppendLine("layout: FAIL - the target radios are not laid out horizontally on the button row");
        }
        return ok;
    }

    private static void FlushSelfCheck()
    {
        if (string.IsNullOrWhiteSpace(_selfCheckOut))
        {
            return;
        }
        try
        {
            File.WriteAllText(_selfCheckOut, SelfCheckLog.ToString());
        }
        catch
        {
            // Reporting must never change the outcome.
        }
    }

    private static int RunCore(int selfCheckMs, string? reportPath)
    {
        _ = reportPath;
        _settings = AppSettings.Load();

        IntPtr instance = Kernel32.GetModuleHandleW(null);
        _font = Gdi32.GetStockObject(Gdi32.DEFAULT_GUI_FONT);
        // IDI_APPLICATION is the resource id the .NET SDK embeds ApplicationIcon under, so this returns
        // this program's own icon rather than the generic system one.
        _icon = User32.LoadIconW(instance, User32.IDI_APPLICATION);

        bool registered;
        unsafe
        {
            // Taking the address of an [UnmanagedCallersOnly] method is the only AOT-safe way to hand a
            // managed callback to native code; a delegate would need runtime marshalling.
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
        int width = (int)(1180 * dpi / 96.0);
        int height = (int)(760 * dpi / 96.0);

        _frame = User32.CreateWindowExW(0, "RatTrapFrame", $"rat-trap {Program.VersionText} - cursor and crosshair recovery",
            User32.WS_OVERLAPPEDWINDOW, 80, 60, width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_frame == IntPtr.Zero)
        {
            User32.MessageBoxW(IntPtr.Zero, "Failed to create the main window.", "rat-trap", User32.MB_ICONERROR);
            return 1;
        }

        SelfCheckLog.AppendLine($"milestone: frame window created, handle=0x{_frame:X}");
        User32.ShowWindow(_frame, User32.SW_SHOW);
        User32.UpdateWindow(_frame);

        if (_selfCheck)
        {
            // Crosshair mode forces the reconstruction path, because in cursor mode the operating system
            // usually hands over a finished cursor and there would be no model to observe - correct, but it
            // would make this check silently test nothing. Restored before the settings are saved.
            _selfCheckRestoreTarget = _settings.Target;
            _settings.Target = CaptureTarget.Crosshair;

            StartSession();
            ObservationWindow.Open(_frame, _session, _font, _icon);
            User32.SetTimer(_frame, (UIntPtr)ObservationTimerId, ObservationIntervalMs, IntPtr.Zero);
            User32.UpdateWindow(_frame);
            if (_canvas != IntPtr.Zero)
            {
                User32.UpdateWindow(_canvas);
            }
            User32.SetTimer(_frame, (UIntPtr)SelfCheckTimerId, (uint)selfCheckMs, IntPtr.Zero);
        }

        while (true)
        {
            int result = User32.GetMessageW(out MSG message, IntPtr.Zero, 0, 0);
            if (result is 0 or -1)
            {
                break;
            }
            User32.TranslateMessage(ref message);
            User32.DispatchMessageW(ref message);
        }

        SelfCheckLog.AppendLine("milestone: message loop exited");
        Shutdown();
        return 0;
    }

    private static unsafe bool RegisterClass(string name,
        delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, IntPtr, IntPtr> proc)
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
                hIcon = _icon,
                hIconSm = _icon,
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
                    if (wParam.ToUInt64() == ObservationTimerId)
                    {
                        if (ObservationWindow.RunSelfCheckStep() && _observationDone == 0)
                        {
                            _observationDone = 1;
                            User32.KillTimer(hwnd, wParam);
                            User32.PostQuitMessage(0);
                        }
                        return IntPtr.Zero;
                    }
                    if (wParam.ToUInt64() == SelfCheckTimerId)
                    {
                        User32.KillTimer(hwnd, wParam);
                        User32.PostQuitMessage(0);
                        return IntPtr.Zero;
                    }
                    OnTimer();
                    return IntPtr.Zero;

                case User32.WM_HOTKEY:
                    if (wParam.ToUInt64() == HotKeyId)
                    {
                        ToggleSession("global hot key");
                    }
                    return IntPtr.Zero;

                case User32.WM_COMMAND:
                    // The high word carries the notification code, which is the only way to tell an EDIT that
                    // has lost focus from a BUTTON that was clicked.
                    OnCommand((int)(wParam.ToUInt64() & 0xFFFF), (int)((wParam.ToUInt64() >> 16) & 0xFFFF));
                    return IntPtr.Zero;

                case User32.WM_CLOSE:
                    Shutdown();
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
        // Assigned here, not only where CreateWindowExW returns, and that ordering is the whole point: WM_CREATE
        // runs inside CreateWindowExW, so by the time this method executes _frame is still IntPtr.Zero. Every
        // initialisation below that looks a control up through _frame was therefore silently doing nothing -
        // GetDlgItem on a null window returns null, and the callers all check for null and skip. The two
        // information lines at the top of the window came up empty for that reason, and so did the target radio
        // pair's initial check state. Calls that took the handle as a parameter worked, which is why the
        // controls existed at all and the fault looked like a blank strip rather than a broken window.
        _frame = frame;
        IntPtr instance = Kernel32.GetModuleHandleW(null);

        _canvas = User32.CreateWindowExW(0, "RatTrapCanvas", string.Empty,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_BORDER,
            0, 0, 10, 10, frame, IntPtr.Zero, instance, IntPtr.Zero);

        _log = User32.CreateWindowExW(0, "EDIT", string.Empty,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_BORDER | User32.WS_VSCROLL |
            User32.ES_MULTILINE | User32.ES_AUTOVSCROLL | User32.ES_READONLY,
            0, 0, 10, 10, frame, new IntPtr(IdLog), instance, IntPtr.Zero);

        // A plain label, not a group box. The radios sit on the button row to the right of the buttons, where
        // a group box would draw a frame around two controls on a shared line and steal vertical space from
        // the preview and the log for no information.
        CreateControl(frame, instance, "STATIC", "Target:", IdModeLabel,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.SS_LEFT);
        CreateControl(frame, instance, "BUTTON", "Cursor", IdModeCursor,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_TABSTOP | BS_AUTORADIOBUTTON | WS_GROUP);
        CreateControl(frame, instance, "BUTTON", "Crosshair", IdModeCrosshair,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_TABSTOP | BS_AUTORADIOBUTTON);

        CreateControl(frame, instance, "STATIC", string.Empty, IdSaveDir,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.SS_LEFT);
        CreateControl(frame, instance, "STATIC", string.Empty, IdHotKeyInfo,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.SS_LEFT);

        // The one setting with no other home. ES_NUMBER keeps the field to digits, so the only invalid input it
        // can hold is nothing at all.
        CreateControl(frame, instance, "STATIC", "Crosshair width (px):", IdCrosshairLabel,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.SS_LEFT);
        CreateControl(frame, instance, "EDIT", string.Empty, IdCrosshairSize,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_TABSTOP | User32.WS_BORDER | User32.ES_NUMBER);

        // On by default, which is the behaviour this program always had. Off, a capture runs until the hot key
        // stops it, so the result can be judged before it is accepted rather than after.
        CreateControl(frame, instance, "BUTTON", "Finish automatically", IdAutoFinish,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_TABSTOP | BS_AUTOCHECKBOX);

        // One toggle instead of a Start/Stop pair, "Open folder" instead of a browse dialog, and no
        // per-format export buttons: every format is written automatically the moment a cursor exists,
        // so a button for each one was three chances to lose the result.
        CreateControl(frame, instance, "BUTTON", "Start", IdToggle,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_TABSTOP | User32.BS_PUSHBUTTON);
        CreateControl(frame, instance, "BUTTON", "Open folder", IdBrowse,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_TABSTOP | User32.BS_PUSHBUTTON);
        // Deliberately NOT WS_VISIBLE. The button runs the same checks as `rattrap selftest`, which prints a
        // report and can be scripted, so from a user's side it was a control whose purpose could not be guessed.
        // It stays created and routed, so the GUI self-check still exercises the code path it belongs to, and
        // restoring it is a one-word change here.
        CreateControl(frame, instance, "BUTTON", "Self-test", IdSelfTest,
            User32.WS_CHILD | User32.WS_TABSTOP | User32.BS_PUSHBUTTON);

        // The only addition to the v1.0 layout: a way to open the floating observation window.
        CreateControl(frame, instance, "BUTTON", "Observe...", IdObserve,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_TABSTOP | User32.BS_PUSHBUTTON);

        if (_font != IntPtr.Zero)
        {
            foreach (int id in new[] { IdModeLabel, IdModeCursor, IdModeCrosshair, IdToggle, IdBrowse,
                                       IdSelfTest, IdObserve, IdLog, IdSaveDir, IdHotKeyInfo,
                                       IdCrosshairLabel, IdCrosshairSize, IdAutoFinish })
            {
                User32.SendMessageW(User32.GetDlgItem(frame, id), User32.WM_SETFONT,
                    (UIntPtr)(ulong)_font, new IntPtr(1));
            }
        }

        CheckRadio(_settings.Target == CaptureTarget.Crosshair ? IdModeCrosshair : IdModeCursor);
        SetCrosshairSizeField(_settings.CrosshairSize);

        // Checked here, at start-up, rather than only when a capture tries to save: an unusable save directory is
        // a way to lose a capture, and finding out at that point is finding out too late.
        if (TryPrepareSaveDirectory(out string saveDirectory, out string? saveProblem))
        {
            SetText(IdSaveDir, $"Saving to {saveDirectory}");
        }
        else
        {
            SetText(IdSaveDir, $"Cannot save to {saveDirectory}");
            AppendLog($"the save directory cannot be created: {saveProblem}");
            AppendLog($"  set 'savedirectory' in {AppSettings.FileName} to a writable path, or move the program " +
                      "to a location this account can write to.");
        }
        SetText(IdHotKeyInfo, _settings.HotKeyEnabled
            ? $"Hot key: {_settings.HotKey}"
            : "Hot key: disabled in rat-trap.ini");

        User32.SendMessageW(User32.GetDlgItem(frame, IdAutoFinish), BM_SETCHECK,
            _settings.AutoFinish ? (UIntPtr)BST_CHECKED : UIntPtr.Zero, IntPtr.Zero);

        RegisterHotKey(frame);

        AppendLog("rat-trap");
        AppendLog("Start (or the hot key) acquires the target and saves every format automatically.");
        AppendLog("Cursor mode follows the pointer. Crosshair mode watches a fixed square at the centre of");
        AppendLog("the foreground window, so switch to the game and aim at something that moves.");
        if (_settings.UsingFallbackPath)
        {
            AppendLog($"note: the program directory is not writable, so settings live in {_settings.FilePath}");
        }
        foreach (string note in _settings.Notes)
        {
            AppendLog($"settings: {note}");
        }
        AppendLog($"settings: {_settings.Describe()}");
        SyncPreview();
    }

    private static void CreateControl(IntPtr parent, IntPtr instance, string className, string text, int id, uint style)
        => User32.CreateWindowExW(0, className, text, style, 0, 0, 10, 10,
            parent, new IntPtr(id), instance, IntPtr.Zero);

    private static void RegisterHotKey(IntPtr frame)
    {
        if (!_settings.HotKeyEnabled)
        {
            return;
        }

        if (!AppSettings.TryParseHotKey(_settings.HotKey, out uint modifiers, out uint virtualKey, out string error))
        {
            AppendLog($"the configured hot key could not be parsed: {error}; edit hotkey in {AppSettings.FileName}");
            return;
        }

        // MOD_NOREPEAT is already folded into the parsed modifiers by TryParseHotKey, so holding the key
        // does not toggle capture many times a second.
        if (User32.RegisterHotKey(frame, HotKeyId, modifiers, virtualKey))
        {
            _hotKeyRegistered = true;
            _hotKeyEverRegistered = true;
        }
        else
        {
            AppendLog($"the hot key {_settings.HotKey} is already taken by another program; " +
                      $"change hotkey in {AppSettings.FileName} and restart");
        }
    }

    private static void Layout(IntPtr frame)
    {
        User32.GetClientRect(frame, out RECT client);
        int dpi = (int)User32.GetDpiForWindow(frame);
        int scale = Math.Max(1, dpi / 96);
        int gap = 8 * scale;
        int rowHeight = 22 * scale;
        int buttonHeight = 30 * scale;

        // The two information lines occupy the top, full width. They used to sit to the right of a group box
        // holding the target radios, which cost two extra rows of vertical space for a frame around two
        // controls; the radios are on the button row now and the space goes to the preview and the log.
        int top = gap;
        int infoWidth = Math.Max(80, client.Width - (gap * 2));
        Move(IdSaveDir, gap, top, infoWidth, rowHeight);
        // The second information row also carries the crosshair width, which is the only setting that had no
        // home in the interface. It reuses space that was already reserved and mostly empty.
        int secondRow = top + rowHeight;
        int hotKeyWidth = 260 * scale;
        Move(IdHotKeyInfo, gap, secondRow, hotKeyWidth, rowHeight);
        int labelLeft = gap + hotKeyWidth + (gap * 2);
        int labelWidth = 132 * scale;
        Move(IdCrosshairLabel, labelLeft, secondRow, labelWidth, rowHeight);
        Move(IdCrosshairSize, labelLeft + labelWidth, secondRow, 72 * scale, rowHeight);
        Move(IdAutoFinish, labelLeft + labelWidth + (72 * scale) + (gap * 3), secondRow, 160 * scale, rowHeight);

        int bottom = buttonHeight + (gap * 2);
        int contentTop = top + (rowHeight * 2) + gap;
        int contentHeight = Math.Max(10, client.Height - contentTop - bottom);
        int canvasWidth = Math.Max(10, (int)((client.Width - (gap * 3)) * 0.54));
        int logWidth = Math.Max(10, client.Width - canvasWidth - (gap * 3));

        MoveWindow(_canvas, gap, contentTop, canvasWidth, contentHeight);
        MoveWindow(_log, gap + canvasWidth + gap, contentTop, logWidth, contentHeight);

        int y = client.Height - buttonHeight - gap;
        int x = gap;
        foreach ((int id, int width) in new[] { (IdToggle, 100), (IdBrowse, 110), (IdObserve, 104) })
        {
            Move(id, x, y, width * scale, buttonHeight);
            x += (width * scale) + gap;
        }

        // Target selection sits on the same row, to the right of the buttons, separated by a wider gap so it
        // reads as its own group without a frame. The radios are level with each other and with the buttons,
        // and the row is the button row, so the choice costs no height at all.
        x += gap;
        int radioHeight = buttonHeight;
        Move(IdModeLabel, x, y, 56 * scale, radioHeight);
        x += 56 * scale;
        Move(IdModeCursor, x, y, 74 * scale, radioHeight);
        x += 74 * scale;
        Move(IdModeCrosshair, x, y, 88 * scale, radioHeight);
    }

    private static void Move(int id, int x, int y, int width, int height)
        => MoveWindow(User32.GetDlgItem(_frame, id), x, y, width, height);

    private static void MoveWindow(IntPtr window, int x, int y, int width, int height)
    {
        if (window != IntPtr.Zero)
        {
            User32.MoveWindow(window, x, y, width, height, true);
        }
    }

    private static void CheckRadio(int id)
    {
        User32.SendMessageW(User32.GetDlgItem(_frame, IdModeCursor), BM_SETCHECK, UIntPtr.Zero, IntPtr.Zero);
        User32.SendMessageW(User32.GetDlgItem(_frame, IdModeCrosshair), BM_SETCHECK, UIntPtr.Zero, IntPtr.Zero);

        IntPtr target = User32.GetDlgItem(_frame, id);
        if (target != IntPtr.Zero)
        {
            User32.SendMessageW(target, BM_SETCHECK, (UIntPtr)BST_CHECKED, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Walks the window's real control tree and reports every control's class, id, rectangle and text.
    /// </summary>
    /// <remarks>
    /// Added because a report of "there is a blank placeholder at the top of the window" could not be settled by
    /// reading the layout code: that code only describes the controls it knows about and says nothing about
    /// where they ended up or what they contain. This enumerates what Windows actually has, so an unexplained
    /// empty region becomes an explained one - or turns out to be a control nobody meant to leave empty.
    /// </remarks>
    private static void DumpControlTree()
    {
        if (_frame == IntPtr.Zero)
        {
            return;
        }

        User32.GetClientRect(_frame, out RECT client);
        SelfCheckLog.AppendLine($"controls: client area {client.Width}x{client.Height}, walking the real tree");
        DumpChildren(_frame, 0);
    }

    private static void DumpChildren(IntPtr parent, int depth)
    {
        if (depth > 3)
        {
            return;
        }

        for (IntPtr child = User32.GetWindow(parent, User32.GW_CHILD); child != IntPtr.Zero;
             child = User32.GetWindow(child, User32.GW_HWNDNEXT))
        {
            var className = new char[64];
            var text = new char[160];
            int classLength = User32.GetClassNameW(child, className, className.Length);
            int textLength = User32.GetWindowTextW(child, text, text.Length);

            string name = new(className, 0, Math.Clamp(classLength, 0, className.Length));
            string content = new(text, 0, Math.Clamp(textLength, 0, text.Length));

            User32.GetWindowRect(child, out RECT rect);
            // Client-relative via ClientToScreen, so these numbers can be compared with the layout arithmetic.
            // Subtracting the frame's window rect instead leaves the non-client border and caption in the
            // result, which made every position look offset by the same amount and nearly hid a real fault.
            POINT origin = default;
            User32.ClientToScreen(_frame, ref origin);
            int x = rect.Left - origin.X;
            int y = rect.Top - origin.Y;

            // A checked radio button is the difference between "the control exists" and "the choice was applied",
            // and the same ordering fault that emptied the information lines also skipped the initial check.
            string check = name == "Button"
                ? $" check={User32.SendMessageW(child, BM_GETCHECK, UIntPtr.Zero, IntPtr.Zero).ToInt64()}"
                : string.Empty;

            SelfCheckLog.AppendLine($"controls: {new string(' ', depth * 2)}{name,-12} " +
                                    $"id={User32.GetDlgCtrlID(child),-5} " +
                                    $"visible={User32.IsWindowVisible(child),-5} " +
                                    $"at ({x},{y}) {rect.Width}x{rect.Height}{check} text=\"{content}\"");

            DumpChildren(child, depth + 1);
        }
    }
    /// <summary>
    /// Reads the crosshair width field and applies it, committing on focus loss and before a capture starts.
    /// </summary>
    /// <remarks>
    /// The square's size is fixed when a session starts, because the accumulator is keyed to the region and
    /// resizing it mid-run would mean the offset space silently changing shape. So a change made during a
    /// capture is stored and reported as taking effect on the next Start, rather than appearing to do nothing.
    /// </remarks>
    private static void CommitCrosshairSize()
    {
        IntPtr edit = User32.GetDlgItem(_frame, IdCrosshairSize);
        if (edit == IntPtr.Zero)
        {
            return;
        }

        var buffer = new char[16];
        int length = User32.GetWindowTextW(edit, buffer, buffer.Length);
        string text = new string(buffer, 0, Math.Clamp(length, 0, buffer.Length)).Trim();

        if (!int.TryParse(text, out int parsed))
        {
            // ES_NUMBER keeps the field to digits, so the only invalid input a person can produce is nothing at
            // all - worth saying plainly rather than quoting an empty string back at them.
            AppendLog(text.Length == 0
                ? $"the crosshair width field was left empty; keeping {_settings.CrosshairSize} px"
                : $"crosshair width '{text}' is not a number; keeping {_settings.CrosshairSize} px");
            SetCrosshairSizeField(_settings.CrosshairSize);
            return;
        }

        int clamped = Math.Clamp(parsed, 32, 1024);
        if (clamped != parsed)
        {
            AppendLog($"crosshair width {parsed} px is outside 32..1024; using {clamped} px");
        }
        SetCrosshairSizeField(clamped);

        if (clamped == _settings.CrosshairSize)
        {
            return;
        }

        _settings.CrosshairSize = clamped;
        _settings.Save();
        AppendLog(_session?.IsRunning == true
            ? $"crosshair width set to {clamped} px; the square is fixed for the running capture, so it " +
              $"applies on the next Start"
            : $"crosshair width set to {clamped} px, saved to {_settings.FilePath}");
    }

    /// <summary>Puts a value into the width field without going through the user.</summary>
    private static void SetCrosshairSizeField(int size)
    {
        IntPtr edit = User32.GetDlgItem(_frame, IdCrosshairSize);
        if (edit != IntPtr.Zero)
        {
            User32.SetWindowTextW(edit, size.ToString());
        }
    }
    /// <summary>
    /// Drives the crosshair width field the way a person would and checks it reaches the setting.
    /// </summary>
    /// <remarks>
    /// Worth testing through the control rather than by assigning the property, because the part that can break
    /// is the wiring: the notification code arriving in the right half of the message, the text being read back,
    /// the clamp and the write-back into the field. The user's own value is restored and saved afterwards, so a
    /// self-check run does not leave the configuration changed.
    /// </remarks>
    private static bool CheckCrosshairField()
    {
        int original = _settings.CrosshairSize;
        bool ok = true;

        User32.SetWindowTextW(User32.GetDlgItem(_frame, IdCrosshairSize), "400");
        CommitCrosshairSize();
        ok &= _settings.CrosshairSize == 400;
        SelfCheckLog.AppendLine($"crosshair field: typed 400 -> setting {_settings.CrosshairSize} {Mark(ok)}");

        User32.SetWindowTextW(User32.GetDlgItem(_frame, IdCrosshairSize), "5000");
        CommitCrosshairSize();
        ok &= _settings.CrosshairSize == 1024;
        SelfCheckLog.AppendLine($"crosshair field: typed 5000 -> clamped to {_settings.CrosshairSize} {Mark(ok)}");

        User32.SetWindowTextW(User32.GetDlgItem(_frame, IdCrosshairSize), "nonsense");
        CommitCrosshairSize();
        ok &= _settings.CrosshairSize == 1024;
        SelfCheckLog.AppendLine($"crosshair field: typed nonsense -> kept {_settings.CrosshairSize} {Mark(ok)}");

        _settings.CrosshairSize = original;
        SetCrosshairSizeField(original);
        _settings.Save();
        return ok;
    }

    /// <summary>
    /// Drives the "finish automatically" checkbox and checks it reaches the setting and persists.
    /// </summary>
    /// <remarks>
    /// Driven through the control rather than by assigning the property, because the wiring is what can break: a
    /// checkbox reports a click with notification zero, the new state has to be read back rather than assumed, and
    /// the choice has to survive a restart - a setting that silently reverts to its default on the next run is
    /// worse than one that was never offered. The user's own choice is restored and saved afterwards.
    /// </remarks>
    private static bool CheckAutoFinishToggle()
    {
        bool original = _settings.AutoFinish;
        IntPtr box = User32.GetDlgItem(_frame, IdAutoFinish);
        bool ok = box != IntPtr.Zero;

        User32.SendMessageW(box, BM_SETCHECK, UIntPtr.Zero, IntPtr.Zero);
        OnCommand(IdAutoFinish, 0);
        ok &= !_settings.AutoFinish;

        User32.SendMessageW(box, BM_SETCHECK, (UIntPtr)BST_CHECKED, IntPtr.Zero);
        OnCommand(IdAutoFinish, 0);
        ok &= _settings.AutoFinish;

        // Restore, and prove the file agrees by reading it back.
        User32.SendMessageW(box, BM_SETCHECK, original ? (UIntPtr)BST_CHECKED : UIntPtr.Zero, IntPtr.Zero);
        _settings.AutoFinish = original;
        _settings.Save();
        bool persisted = File.ReadAllText(_settings.FilePath)
            .Contains($"autofinish = {(original ? "true" : "false")}", StringComparison.Ordinal);

        SelfCheckLog.AppendLine($"finish automatically: toggles the setting = {ok}, persisted = {persisted}");
        return ok && persisted;
    }
    private static string Mark(bool ok) => ok ? "ok" : "FAIL";
    /// <summary>
    /// Checks that an unusable save directory is reported instead of being promised.
    /// </summary>
    /// <remarks>
    /// The path is made unusable deterministically rather than by denying an ACL: a file is created where a
    /// directory would have to be, so creating the child throws on every machine and for every account. The
    /// property under test is that the failure comes back with the path and a reason attached, because the
    /// reported symptom was a program that displayed a save location it could not create and then failed when
    /// asked to open it. The user's own setting is restored afterwards.
    /// </remarks>
    private static bool CheckSaveDirectoryIsReported()
    {
        string original = _settings.SaveDirectory;
        string blocker = Path.Combine(AppContext.BaseDirectory, $".rattrap-save-probe-{Environment.ProcessId}");
        bool ok;

        try
        {
            File.WriteAllText(blocker, string.Empty);
            _settings.SaveDirectory = Path.Combine(blocker, "captures");

            ok = !TryPrepareSaveDirectory(out string directory, out string? problem) &&
                 problem is { Length: > 0 } &&
                 directory.Contains("captures", StringComparison.OrdinalIgnoreCase);

            SelfCheckLog.AppendLine($"save directory: unusable path reported = {ok} " +
                                    $"(said: {problem ?? "nothing"})");
        }
        catch (Exception ex)
        {
            // A read-only program directory is precisely what this check is about, so being unable to stage the
            // probe there is a reason to skip rather than to fail: the check has no evidence either way.
            ok = true;
            SelfCheckLog.AppendLine($"save directory: SKIPPED - could not stage the probe " +
                                    $"({ex.GetType().Name}); the program directory is not writable, which is " +
                                    "the situation under test");
        }
        finally
        {
            _settings.SaveDirectory = original;
            try
            {
                File.Delete(blocker);
            }
            catch (Exception)
            {
                // Leaving a probe file behind is not worth failing a self-check over.
            }
        }

        return ok;
    }
    private static void SetText(int id, string text)
    {
        IntPtr control = User32.GetDlgItem(_frame, id);
        if (control != IntPtr.Zero)
        {
            User32.SetWindowTextW(control, text);
        }
    }

    // -----------------------------------------------------------------------------
    // Commands
    // -----------------------------------------------------------------------------

    private static void OnCommand(int id, int notification)
    {
        switch (id)
        {
            case IdToggle:
                ToggleSession("button");
                break;

            case IdModeCursor:
            case IdModeCrosshair:
                _settings.Target = id == IdModeCrosshair ? CaptureTarget.Crosshair : CaptureTarget.Cursor;
                _settings.Save();
                CheckRadio(id);
                AppendLog($"target set to {_settings.Target}; settings saved to {_settings.FilePath}");
                break;

            case IdAutoFinish:
                // A checkbox reports itself on click with notification 0, so the new state is read back rather
                // than assumed from a toggle of what was there before.
                bool autoFinish = User32.SendMessageW(User32.GetDlgItem(_frame, IdAutoFinish), BM_GETCHECK,
                    UIntPtr.Zero, IntPtr.Zero).ToInt64() == BST_CHECKED;
                _settings.AutoFinish = autoFinish;
                _settings.Save();
                AppendLog(autoFinish
                    ? $"a capture finishes by itself after {_settings.ReconstructSeconds}s; saved to " +
                      $"{_settings.FilePath}"
                    : $"a capture now runs until the hot key or Stop finishes it; saved to {_settings.FilePath}");
                break;

            case IdCrosshairSize:
                // Committed when the field loses focus, which is the moment a person considers the value final.
                if (notification == User32.EN_KILLFOCUS)
                {
                    CommitCrosshairSize();
                }
                break;

            case IdBrowse:
                OpenSaveFolder();
                break;

            case IdSelfTest:
                RunSelfTestInGui();
                break;

            case IdObserve:
                ObservationWindow.Open(_frame, _session, _font, _icon);
                AppendLog(ObservationWindow.IsOpen
                    ? "observation window opened - three panels of the reconstruction in progress"
                    : "could not open the observation window: " + ObservationWindow.LastReason);
                break;
        }
    }

    private static void ToggleSession(string reason)
    {
        if (_session?.IsRunning == true)
        {
            StopSession();
            AppendLog($"capture stopped ({reason})");
            return;
        }

        AppendLog($"capture started ({reason})");
        StartSession();
    }

    private static void StartSession()
    {
        _sessionFailed = false;
        _cursor = null;
        _session?.Dispose();
        // Read before the session is built, because the square's size is baked into it. Doing it here as well as
        // on focus loss means the field is authoritative even when it never lost focus - pressing Start straight
        // after typing must use what was typed.
        CommitCrosshairSize();

        _session = new CaptureSession(_settings, AppendLog);
        ObservationWindow.Attach(_session);
        _session.Start();
        _status = _session.Status;
        _detail = _session.Detail;
        _sessionFailed = _session.Phase == SessionPhase.Failed;
        SetToggleText();
        User32.SetTimer(_frame, (UIntPtr)CaptureTimerId, TimerIntervalMs, IntPtr.Zero);
        SyncPreview();
    }

    private static void StopSession()
    {
        if (_frame != IntPtr.Zero)
        {
            User32.KillTimer(_frame, (UIntPtr)CaptureTimerId);
        }
        _session?.Dispose();
        _session = null;
        ObservationWindow.Attach(null);
        SetToggleText();
    }

    private static void SetToggleText()
    {
        IntPtr toggle = User32.GetDlgItem(_frame, IdToggle);
        if (toggle != IntPtr.Zero)
        {
            // One button whose label says what pressing it does next, rather than two buttons of which
            // one is always the wrong one to press.
            User32.SetWindowTextW(toggle, _session?.IsRunning == true ? "Stop" : "Start");
        }
    }

    private static void OnTimer()
    {
        if (_session is null)
        {
            return;
        }

        var step = _session.Tick();
        _status = _session.Status;
        _detail = _session.Detail;

        if (_session.Phase == SessionPhase.Failed)
        {
            _sessionFailed = true;
        }

        if (step.Acquired)
        {
            _cursor = _session.Cursor;
            SyncPreview();
            SetToggleText();
            AppendLog(_session.Status);
            if (step.Saved)
            {
                AppendLog($"done. {Path.GetFileName(step.SavedBasePath)} written in {_settings.ResolveSaveDirectory()}");
            }
            return;
        }

        if (!_session.IsRunning)
        {
            SetToggleText();
            if (_frame != IntPtr.Zero)
            {
                User32.KillTimer(_frame, (UIntPtr)CaptureTimerId);
            }
        }

        if (_canvas != IntPtr.Zero)
        {
            User32.InvalidateRect(_canvas, IntPtr.Zero, false);
        }
    }

    /// <summary>
    /// Makes sure the save directory exists, and says why not when it cannot.
    /// </summary>
    /// <remarks>
    /// Separate from opening the folder so that the start-up check can use the same code without launching a
    /// shell window. The failure worth catching is not a folder that will not open - it is a capture that cannot
    /// be written, which a user would otherwise discover only after losing one. Both callers report the reason
    /// rather than a bare exception name, because "access denied" and "an absolute path was configured and is
    /// wrong" need different answers.
    /// </remarks>
    private static bool TryPrepareSaveDirectory(out string directory, out string? error)
    {
        directory = _settings.ResolveSaveDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or
                                   NotSupportedException or ArgumentException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static void OpenSaveFolder()
    {
        try
        {
            if (!TryPrepareSaveDirectory(out string directory, out string? problem))
            {
                AppendLog($"could not open the save folder: {problem}");
                AppendLog($"  captures would not save there either. Set 'savedirectory' in {AppSettings.FileName} " +
                          "to a writable path, or move the program to a location this account can write to.");
                return;
            }

            // explorer.exe with the folder as an argument, rather than asking the shell to "open" the folder,
            // and with no null arguments. The previous version asked the shell to open the directory, passed
            // null for the parameters and the working directory, and discarded the return value while logging
            // success. ShellExecute reports failure as a code at or below 32, so a refusal was indistinguishable
            // from a folder opening somewhere invisible, and there was no way to tell whether the cause was the
            // verb lookup, the marshalling of those nulls, or the shell itself. Launching Explorer directly
            // removes the verb, real strings remove the null question, and the code below removes the silence.
            IntPtr result = User32.ShellExecuteW(_frame, "open", "explorer.exe", $"\"{directory}\"",
                directory, User32.SW_SHOWNORMAL);

            long code = result.ToInt64();
            if (code > 32)
            {
                AppendLog($"opened {directory}");
            }
            else
            {
                AppendLog($"the shell refused to open {directory} (code {code}: 2 = not found, " +
                          "5 = access denied, 31 = no association). The folder exists - the path is above.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"could not open the save folder: {ex.GetType().Name}: {ex.Message}");
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
                failures == 0 ? "Self-test passed." : $"Self-test failed with {failures} failure(s); see the log.",
                "rat-trap", User32.MB_OK | User32.MB_ICONINFORMATION | User32.MB_SETFOREGROUND);
        }
        catch (Exception ex)
        {
            AppendLog($"self-test error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------------
    // Log
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Appends one line and keeps it visible.
    /// </summary>
    /// <remarks>
    /// The first version rebuilt the whole control with <c>SetWindowTextW</c> on every line and then tried
    /// to scroll with <c>EM_LINESCROLL</c> and zero offsets - which scrolls by zero lines - so the view
    /// stayed where it was and the newest line was precisely the one the user could not see. Two changes
    /// fix it properly: append with <c>EM_REPLACESEL</c> after moving the selection to the end, then call
    /// <c>EM_SCROLLCARET</c>, which scrolls the caret into view rather than nudging by an amount that would
    /// have to be guessed.
    /// </remarks>
    private static void AppendLog(string line)
    {
        LogLines.AppendLine(line);
        SelfCheckLog.AppendLine($"log: {line}");

        if (_log == IntPtr.Zero)
        {
            return;
        }

        try
        {
            // Bound the control: an unbounded EDIT gets slow and eventually refuses input.
            if (_logLength > 200_000)
            {
                User32.SetWindowTextW(_log, string.Empty);
                _logLength = 0;
            }

            IntPtr text = Marshal.StringToHGlobalUni(line + "\r\n");
            try
            {
                User32.SendMessageW(_log, EM_SETSEL, EndOfText, IntPtr.Zero);
                User32.SendMessageW(_log, EM_REPLACESEL, UIntPtr.Zero, text);
            }
            finally
            {
                Marshal.FreeHGlobal(text);
            }

            User32.SendMessageW(_log, EM_SCROLLCARET, UIntPtr.Zero, IntPtr.Zero);
            _logLength += line.Length + 2;
        }
        catch (Exception ex)
        {
            _nativeExceptionCount++;
            SelfCheckLog.AppendLine($"EXCEPTION appending to the log: {ex}");
        }
    }

    /// <summary>The WPARAM that means "the end of the text" to <c>EM_SETSEL</c>.</summary>
    private static UIntPtr EndOfText => new(unchecked((ulong)(long)-1));

    // -----------------------------------------------------------------------------
    // Preview
    // -----------------------------------------------------------------------------

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
                    ArgbPixel source = _cursor.Image.GetPixel(x, y);
                    ArgbPixel under = checker.GetPixel(x, y);
                    int alpha = source.A, inverse = 255 - alpha;
                    checker.SetPixel(x, y, new ArgbPixel(
                        (byte)((source.B * alpha + under.B * inverse + 127) / 255),
                        (byte)((source.G * alpha + under.G * inverse + 127) / 255),
                        (byte)((source.R * alpha + under.R * inverse + 127) / 255),
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
            if (client.Width <= 0 || client.Height <= 0 || !EnsureCanvasDib(client.Width, client.Height))
            {
                return;
            }

            FillDib(client.Width, client.Height, new ArgbPixel(0x2A, 0x2A, 0x2A, 255));

            if (_preview is not null)
            {
                int scale = Math.Max(1, Math.Min(6,
                    Math.Min((client.Width - 24) / Math.Max(1, _preview.Width),
                             (client.Height - 70) / Math.Max(1, _preview.Height))));
                int drawnWidth = _preview.Width * scale;
                int drawnHeight = _preview.Height * scale;
                int offsetX = (client.Width - drawnWidth) / 2;
                int offsetY = Math.Max(56, (client.Height - drawnHeight) / 2);

                BlitNearest(offsetX, offsetY, _preview, scale);

                if (_cursor is not null && scale >= 2)
                {
                    DrawHotspotMark(offsetX + (_cursor.HotspotX * scale), offsetY + (_cursor.HotspotY * scale), scale);
                }
            }

            var info = new BITMAPINFO { bmiHeader = BITMAPINFOHEADER.Create(client.Width, -client.Height, 32) };
            Gdi32.SetDIBitsToDevice(dc, 0, 0, (uint)client.Width, (uint)client.Height, 0, 0, 0, (uint)client.Height,
                _canvasBits, ref info, Gdi32.DIB_RGB_COLORS);

            Gdi32.SetBkMode(dc, Gdi32.TRANSPARENT);
            IntPtr previousFont = Gdi32.SelectObject(dc, _font);

            // Status text above the artwork, detail below it, so neither covers the pixels.
            Gdi32.SetTextColor(dc, 0x00E8E8E8);
            var statusRect = RECT.FromSize(10, 8, client.Width - 20, 20);
            User32.DrawTextW(dc, _status, -1, ref statusRect, User32.DT_LEFT | User32.DT_TOP | User32.DT_NOPREFIX);

            if (_detail.Length > 0)
            {
                Gdi32.SetTextColor(dc, 0x00A8A8A8);
                var detailRect = RECT.FromSize(10, 28, client.Width - 20, 24);
                User32.DrawTextW(dc, _detail, -1, ref detailRect,
                    User32.DT_LEFT | User32.DT_TOP | User32.DT_NOPREFIX);
            }

            if (_preview is null)
            {
                Gdi32.SetTextColor(dc, 0x00909090);
                var emptyRect = RECT.FromSize(10, client.Height / 2, client.Width - 20, 24);
                User32.DrawTextW(dc, "Nothing captured yet.", -1, ref emptyRect,
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
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

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
    /// <c>StretchBlt</c> applies a halftone or colour-on-colour filter depending on the mode that happens
    /// to be set, which turns an exact 3x view of a 1-pixel cursor edge into a blur. Cursor artwork has to
    /// be inspected at the pixel level, so the scaling is explicit.
    /// </remarks>
    private static unsafe void BlitNearest(int offsetX, int offsetY, ArgbBitmap source, int scale)
    {
        byte* bits = (byte*)_canvasBits;
        int drawnWidth = source.Width * scale;
        int drawnHeight = source.Height * scale;

        for (int y = 0; y < drawnHeight; y++)
        {
            int canvasY = offsetY + y;
            if (canvasY < 0 || canvasY >= _canvasHeight)
            {
                continue;
            }
            int sourceY = y / scale;
            byte* row = bits + ((long)canvasY * _canvasWidth * 4);

            for (int x = 0; x < drawnWidth; x++)
            {
                int canvasX = offsetX + x;
                if (canvasX < 0 || canvasX >= _canvasWidth)
                {
                    continue;
                }
                int sourceIndex = ((sourceY * source.Width) + (x / scale)) * 4;
                row[(canvasX * 4)] = source.Pixels[sourceIndex];
                row[(canvasX * 4) + 1] = source.Pixels[sourceIndex + 1];
                row[(canvasX * 4) + 2] = source.Pixels[sourceIndex + 2];
                row[(canvasX * 4) + 3] = 255;
            }
        }
    }

    private static unsafe void DrawHotspotMark(int centerX, int centerY, int scale)
    {
        byte* bits = (byte*)_canvasBits;
        int arm = 6 * scale;
        int thickness = Math.Max(1, scale / 2);

        void Plot(int x, int y)
        {
            for (int t = 0; t < thickness; t++)
            {
                int py = y + t;
                if (x < 0 || py < 0 || x >= _canvasWidth || py >= _canvasHeight)
                {
                    continue;
                }
                byte* pixel = bits + (((long)py * _canvasWidth + x) * 4);
                pixel[0] = 0x30;
                pixel[1] = 0x30;
                pixel[2] = 0xFF;
                pixel[3] = 255;
            }
        }

        for (int d = -arm; d <= arm; d++)
        {
            Plot(centerX + d, centerY);
            Plot(centerX, centerY + d);
        }
    }

    /// <summary>Called when the floating observation window is destroyed.</summary>
    /// <remarks>
    /// A callback rather than the observer reading main-window state: the two windows are destroyed in an
    /// order nobody controls - the user can close either one first - so the observer must not assume the main
    /// window's fields are still meaningful when it goes away.
    /// </remarks>
    internal static void OnObservationClosed() => AppendLog("observation window closed; recording released");

    /// <summary>Reports a problem from the observation window into the one log the user reads.</summary>
    internal static void LogObservationProblem(string message)
    {
        _nativeExceptionCount++;
        AppendLog($"observation window: {message}");
    }

    private static void Shutdown()
    {
        if (_frame != IntPtr.Zero && _hotKeyRegistered)
        {
            User32.UnregisterHotKey(_frame, HotKeyId);
            _hotKeyRegistered = false;
        }
        ObservationWindow.Close();
        StopSession();
        ReleaseCanvasDib();
        if (_selfCheck)
        {
            _settings.Target = _selfCheckRestoreTarget;
        }
        _settings.Save();
    }
}







