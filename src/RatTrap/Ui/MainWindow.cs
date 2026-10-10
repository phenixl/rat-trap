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
    private const int IdModeGroup = 1200;
    private const int IdModeCursor = 1201;
    private const int IdModeCrosshair = 1202;
    private const int IdToggle = 1210;
    private const int IdBrowse = 1211;
    private const int IdSelfTest = 1212;
    private const int IdLog = 1220;
    private const int IdSaveDir = 1230;
    private const int IdHotKeyInfo = 1231;

    private const uint CaptureTimerId = 1;
    private const uint SelfCheckTimerId = 2;
    private const int HotKeyId = 0x5254; // 'RT'
    private const int TimerIntervalMs = 100;

    // Edit control messages. Appending through them, rather than replacing the whole contents, is what
    // keeps the newest line visible; see AppendLog.
    private const uint EM_SETSEL = 0x00B1;
    private const uint EM_REPLACESEL = 0x00C2;
    private const uint EM_SCROLLCARET = 0x00B7;
    private const uint BM_SETCHECK = 0x00F1;
    private const uint BST_CHECKED = 1;
    private const uint BS_AUTORADIOBUTTON = 0x00000009;
    private const uint BS_GROUPBOX = 0x00000007;
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
        SelfCheckLog.AppendLine($"result                           : {(result == 0 && failures == 0 ? "PASS" : "FAIL")}");
        SelfCheckLog.AppendLine("--- session log ---");
        SelfCheckLog.Append(LogLines);
        FlushSelfCheck();

        return result == 0 && failures == 0 ? 0 : 1;
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

        _frame = User32.CreateWindowExW(0, "RatTrapFrame", "rat-trap - cursor and crosshair recovery",
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
            StartSession();
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
                    OnCommand((int)(wParam.ToUInt64() & 0xFFFF));
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
        IntPtr instance = Kernel32.GetModuleHandleW(null);

        _canvas = User32.CreateWindowExW(0, "RatTrapCanvas", string.Empty,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_BORDER,
            0, 0, 10, 10, frame, IntPtr.Zero, instance, IntPtr.Zero);

        _log = User32.CreateWindowExW(0, "EDIT", string.Empty,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_BORDER | User32.WS_VSCROLL |
            User32.ES_MULTILINE | User32.ES_AUTOVSCROLL | User32.ES_READONLY,
            0, 0, 10, 10, frame, new IntPtr(IdLog), instance, IntPtr.Zero);

        CreateControl(frame, instance, "BUTTON", "Target", IdModeGroup,
            User32.WS_CHILD | User32.WS_VISIBLE | BS_GROUPBOX);
        CreateControl(frame, instance, "BUTTON", "Cursor", IdModeCursor,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_TABSTOP | BS_AUTORADIOBUTTON | WS_GROUP);
        CreateControl(frame, instance, "BUTTON", "Crosshair", IdModeCrosshair,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_TABSTOP | BS_AUTORADIOBUTTON);

        CreateControl(frame, instance, "STATIC", string.Empty, IdSaveDir,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.SS_LEFT);
        CreateControl(frame, instance, "STATIC", string.Empty, IdHotKeyInfo,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.SS_LEFT);

        // One toggle instead of a Start/Stop pair, "Open folder" instead of a browse dialog, and no
        // per-format export buttons: every format is written automatically the moment a cursor exists,
        // so a button for each one was three chances to lose the result.
        CreateControl(frame, instance, "BUTTON", "Start", IdToggle,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_TABSTOP | User32.BS_PUSHBUTTON);
        CreateControl(frame, instance, "BUTTON", "Open folder", IdBrowse,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_TABSTOP | User32.BS_PUSHBUTTON);
        CreateControl(frame, instance, "BUTTON", "Self-test", IdSelfTest,
            User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_TABSTOP | User32.BS_PUSHBUTTON);

        if (_font != IntPtr.Zero)
        {
            foreach (int id in new[] { IdModeGroup, IdModeCursor, IdModeCrosshair, IdToggle, IdBrowse,
                                       IdSelfTest, IdLog, IdSaveDir, IdHotKeyInfo })
            {
                User32.SendMessageW(User32.GetDlgItem(frame, id), User32.WM_SETFONT,
                    (UIntPtr)(ulong)_font, new IntPtr(1));
            }
        }

        CheckRadio(_settings.Target == CaptureTarget.Crosshair ? IdModeCrosshair : IdModeCursor);
        SetText(IdSaveDir, $"Saving to {_settings.ResolveSaveDirectory()}");
        SetText(IdHotKeyInfo, _settings.HotKeyEnabled
            ? $"Hot key: {_settings.HotKey}"
            : "Hot key: disabled in rat-trap.ini");

        RegisterHotKey(frame);

        AppendLog("rat-trap");
        AppendLog("Start (or the hot key) acquires the target and saves every format automatically.");
        AppendLog("Cursor mode follows the pointer. Crosshair mode watches a fixed square at the centre of");
        AppendLog("the foreground window, so switch to the game and aim at something that moves.");
        if (_settings.UsingFallbackPath)
        {
            AppendLog($"note: the program directory is not writable, so settings live in {_settings.FilePath}");
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

        int top = gap;
        int modeHeight = (rowHeight * 2) + (gap * 3);

        Move(IdModeGroup, gap, top, 200 * scale, modeHeight);
        Move(IdModeCursor, gap + (12 * scale), top + (16 * scale), 90 * scale, rowHeight);
        Move(IdModeCrosshair, gap + (12 * scale), top + (16 * scale) + rowHeight + (gap / 2), 120 * scale, rowHeight);

        int infoLeft = gap + (210 * scale);
        int infoWidth = Math.Max(80, client.Width - infoLeft - gap);
        Move(IdSaveDir, infoLeft, top + (6 * scale), infoWidth, rowHeight);
        Move(IdHotKeyInfo, infoLeft, top + (6 * scale) + rowHeight, infoWidth, rowHeight);

        int contentTop = top + modeHeight + gap;
        int bottom = buttonHeight + (gap * 2);
        int contentHeight = Math.Max(10, client.Height - contentTop - bottom);
        int canvasWidth = Math.Max(10, (int)((client.Width - (gap * 3)) * 0.54));
        int logWidth = Math.Max(10, client.Width - canvasWidth - (gap * 3));

        MoveWindow(_canvas, gap, contentTop, canvasWidth, contentHeight);
        MoveWindow(_log, gap + canvasWidth + gap, contentTop, logWidth, contentHeight);

        int x = gap;
        int y = client.Height - buttonHeight - gap;
        foreach ((int id, int width) in new[] { (IdToggle, 100), (IdBrowse, 110), (IdSelfTest, 90) })
        {
            Move(id, x, y, width * scale, buttonHeight);
            x += (width * scale) + gap;
        }
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

    private static void OnCommand(int id)
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

            case IdBrowse:
                OpenSaveFolder();
                break;

            case IdSelfTest:
                RunSelfTestInGui();
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
        _session = new CaptureSession(_settings, AppendLog);
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

    private static void OpenSaveFolder()
    {
        try
        {
            string directory = _settings.ResolveSaveDirectory();
            Directory.CreateDirectory(directory);
            User32.ShellExecuteW(_frame, "open", directory, null, null, User32.SW_SHOWNORMAL);
            AppendLog($"opened {directory}");
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

    private static void Shutdown()
    {
        if (_frame != IntPtr.Zero && _hotKeyRegistered)
        {
            User32.UnregisterHotKey(_frame, HotKeyId);
            _hotKeyRegistered = false;
        }
        StopSession();
        ReleaseCanvasDib();
        _settings.Save();
    }
}

