using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RatTrap.Capture;
using RatTrap.Imaging;
using RatTrap.Interop;
using RatTrap.Reconstruct;

namespace RatTrap.Ui;

/// <summary>
/// A floating window that shows the reconstruction working: three panels and a transport.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the main window rather than swapped into its preview area, for two reasons that both come down
/// to space. Three panels side by side need width the preview does not have, and a capture being watched is
/// usually a game occupying the whole screen, so a view that lives inside the main window would be hidden
/// exactly when it is wanted. This window is owned by the main window, so it floats above it, travels with it
/// and is destroyed with it, while still being a real top-level window that can be dragged to another monitor.
/// </para>
/// <para>
/// The panels are refreshed by the capture tick, not by this window's timer - see
/// <c>CaptureSession.RefreshObservation</c>. A view driven by its own timer would show a sampled, aliased
/// version of a process that converges over tens of frames, which is the difference between watching a model
/// fill in and watching it jump. The timer here only repaints.
/// </para>
/// <para>
/// State is in static fields because a <c>[UnmanagedCallersOnly]</c> window procedure cannot capture an
/// instance. There is at most one of these windows, and its fields are only touched from the UI thread.
/// </para>
/// </remarks>
internal static class ObservationWindow
{
    private const int IdPanel = 1400;
    private const int IdOldest = 1410;
    private const int IdBack = 1411;
    private const int IdPause = 1412;
    private const int IdForward = 1413;
    private const int IdNewest = 1414;
    private const int IdCaption = 1420;

    private const uint TimerId = 1;
    private const int TimerIntervalMs = 200;
    private const int ButtonHeight = 30;


    // --- window state ---
    private static IntPtr _window;
    private static IntPtr _font;
    private static IntPtr _icon;
    private static CaptureSession? _session;
    private static readonly ReplayCursor Cursor = new();
    private static int _paints;
    private static int _nativeExceptions;

    // --- canvas device-independent bitmap ---
    private static IntPtr _dib;
    private static IntPtr _bits;
    private static IntPtr _memoryDc;
    private static IntPtr _previousBitmap;
    private static int _dibWidth;
    private static int _dibHeight;

    public static bool IsOpen => _window != IntPtr.Zero;

    /// <summary>The replay position, exposed so the self-check can drive the same object the buttons drive.</summary>
    internal static ReplayCursor Replay => Cursor;

    // Self-check state for the scripted run. Static for the same reason as everything else here.
    private static int _selfStep;
    private static int _selfTicks;
    private static int _selfPauseCount;
    private static int _selfPauseIndex;
    private static long _selfPauseObserved;
    internal static List<string> SelfCheckLines { get; } = [];

    /// <summary>Why the panels might be empty, for the self-check report.</summary>
    public static string LastReason { get; private set; } = "not open";

    public static int Paints => _paints;

    /// <summary>Opens the window, or brings it to the front if it is already open.</summary>
    public static void Open(IntPtr owner, CaptureSession? session, IntPtr font, IntPtr icon)
    {
        _session = session;
        _font = font;
        _icon = icon;

        if (_window != IntPtr.Zero)
        {
            User32.ShowWindow(_window, User32.SW_SHOW);
            User32.SetForegroundWindow(_window);
            Attach(session);
            return;
        }

        IntPtr instance = Kernel32.GetModuleHandleW(null);
        bool registered;
        unsafe
        {
            registered = RegisterClass(instance, "RatTrapObserve", &ObserveProc);
        }
        if (!registered)
        {
            LastReason = "failed to register the observation window class";
            return;
        }

        var monitors = Monitors.Enumerate();
        int dpi = (int)(monitors.Count > 0 ? monitors[0].DpiX : 96);
        int scale = Math.Max(1, dpi / 96);

        // Wide by default, because the point of the window is that three panels fit side by side.
        _window = User32.CreateWindowExW(User32.WS_EX_TOOLWINDOW, "RatTrapObserve",
            $"rat-trap {Program.VersionText} - reconstruction observation",
            User32.WS_OVERLAPPEDWINDOW, 120, 140, 1100 * scale, 440 * scale,
            owner, IntPtr.Zero, instance, IntPtr.Zero);

        if (_window == IntPtr.Zero)
        {
            LastReason = "CreateWindowEx failed for the observation window";
            return;
        }

        User32.ShowWindow(_window, User32.SW_SHOW);
        User32.UpdateWindow(_window);
        User32.SetTimer(_window, (UIntPtr)TimerId, TimerIntervalMs, IntPtr.Zero);
        Attach(session);
        LastReason = "opened";
    }

    /// <summary>
    /// Runs one step of the scripted self-check against the real window, session and transport.
    /// </summary>
    /// <remarks>
    /// The property that needs a clock and cannot be tested by arithmetic: a paused view must stop moving while
    /// the recording behind it keeps growing. Getting that wrong would freeze the record at the moment somebody
    /// looked at it, which is when they most want it to keep filling in. So this pauses, waits a tick, and
    /// compares a frozen index against a count that must have moved.
    /// </remarks>
    /// <returns>True when the script has finished.</returns>
    internal static bool RunSelfCheckStep()
    {
        _selfTicks++;
        if (_selfTicks < 4 || _session is null)
        {
            return false;
        }

        ReconstructionHistory history = _session.History;
        ReconstructionView view = _session.View;

        switch (_selfStep)
        {
            case 0:
                if (history.TotalRecorded == 0)
                {
                    SelfCheckLines.Add("observation window: SKIPPED - no frames were recorded. session says: " +
                                       $"{_session.Status} / {_session.Detail}");
                    _selfStep = 4;
                    return false;
                }

                SelfCheckLines.Add($"observation window: open={IsOpen}, {_paints} paints, " +
                                   $"{view.Refreshes} view refreshes, {history.TotalRecorded} frames, " +
                                   $"ring {history.Count}/{history.Capacity}, " +
                                   $"{history.Bytes / 1024.0 / 1024.0:F1} MB, 1/{history.Scale} scale");
                SelfCheckLines.Add($"observation window: model covers {view.ModelCells} offsets, " +
                                   $"{view.PairsRecorded} pairs, {view.CoveredPixels} covered, " +
                                   $"refresh {view.LastRefreshMilliseconds:F2} ms, " +
                                   $"background rebuild {view.LastBackgroundMilliseconds:F2} ms " +
                                   $"({view.BackgroundRebuilds} rebuilds)");
                SelfCheckLines.Add($"{Ok(IsOpen)} the observation window is open");
                SelfCheckLines.Add($"{Ok(history.Count > 0 && history.Count <= history.Capacity)} " +
                                   "the recording is populated and bounded");
                SelfCheckLines.Add($"{Ok(view.Refreshes > 0)} the view was refreshed by the capture tick");
                SelfCheckLines.Add($"{Ok(_paints > 0)} the observation window painted itself");

                _selfPauseCount = history.Count;
                _selfPauseObserved = _session.ObservedFrames;
                Cursor.Pause(history.Count);
                _selfPauseIndex = Cursor.Resolve(history.Count);
                _selfStep = 1;
                return false;

            case 1:
                SelfCheckLines.Add($"{Ok(Cursor.Paused)} pausing parked the view");
                SelfCheckLines.Add($"{Ok(Cursor.Resolve(history.Count) == _selfPauseIndex)} " +
                                   $"the parked view did not move (still {_selfPauseIndex + 1})");
                SelfCheckLines.Add($"{Ok(_session.ObservedFrames > _selfPauseObserved)} " +
                                   $"the capture continued while paused " +
                                   $"({_selfPauseObserved} -> {_session.ObservedFrames} frames)");
                SelfCheckLines.Add($"observation window: paused at {_selfPauseIndex + 1}; recording went " +
                                   $"{_selfPauseCount} -> {history.Count} frames");
                _selfStep = 2;
                return false;

            case 2:
                int before = Cursor.Resolve(history.Count);
                int back = Cursor.StepBack(history.Count);
                SelfCheckLines.Add($"{Ok(back == Math.Max(0, before - 1))} " +
                                   $"stepping back moved exactly one frame ({before} -> {back})");

                int forward = Cursor.StepForward(history.Count);
                SelfCheckLines.Add($"{Ok(forward == Math.Min(history.Count - 1, back + 1))} " +
                                   $"stepping forward moved exactly one frame ({back} -> {forward})");

                Cursor.JumpOldest();
                SelfCheckLines.Add($"{Ok(Cursor.Resolve(history.Count) == 0)} the oldest button reached frame 1");
                SelfCheckLines.Add($"{Ok(Cursor.StepBack(history.Count) == 0)} stepping back at the oldest clamps");

                Cursor.JumpNewest();
                SelfCheckLines.Add($"{Ok(!Cursor.Paused && Cursor.Resolve(history.Count) == history.Count - 1)} " +
                                   "the newest button resumed following");
                _selfStep = 3;
                return false;

            default:
                SelfCheckLines.Add($"observation window: {Cursor.Steps} transport steps exercised");
                return true;
        }
    }

    private static string Ok(bool condition) => condition ? "ok  " : "FAIL";
    /// <summary>Points the window at a session, switching observation on for as long as it is open.</summary>
    /// <remarks>
    /// The observation work is gated on this, so that nothing is paid for while the window is shut: no view
    /// refresh, no <c>Solve()</c> per frame, no recording. Closing the window stops all three and frees the
    /// recording with the window.
    /// </remarks>
    public static void Attach(CaptureSession? session)
    {
        _session = session;
        if (_window == IntPtr.Zero)
        {
            return;
        }

        if (session is not null)
        {
            session.ObservationEnabled = true;
            session.History.Enabled = true;
        }
        Cursor.Resume();
    }

    /// <summary>Closes the window if it is open.</summary>
    public static void Close()
    {
        if (_window != IntPtr.Zero)
        {
            User32.DestroyWindow(_window);
        }
    }

    /// <summary>Releases the session and the recording, and reports that the window is gone.</summary>
    public static void Detach()
    {
        if (_session is not null)
        {
            _session.ObservationEnabled = false;
            _session.History.Enabled = false;
            _session.History.Clear();
        }
        _session = null;
        LastReason = "closed";
    }

    private static unsafe bool RegisterClass(IntPtr instance, string name,
        delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, IntPtr, IntPtr> proc)
    {
        IntPtr namePtr = Marshal.StringToHGlobalUni(name);
        try
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = 0x0002 | 0x0001,
                lpfnWndProc = (IntPtr)proc,
                hInstance = instance,
                hIcon = _icon,
                hIconSm = _icon,
                hCursor = User32.LoadCursorW(IntPtr.Zero, new IntPtr(32512)),
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
    // Window procedure
    // -----------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr ObserveProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
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
                    User32.InvalidateRect(hwnd, IntPtr.Zero, false);
                    return IntPtr.Zero;

                case User32.WM_PAINT:
                    _paints++;
                    Paint(hwnd);
                    return IntPtr.Zero;

                case User32.WM_ERASEBKGND:
                    return new IntPtr(1);

                case User32.WM_KEYDOWN:
                    // Keyboard equivalents, because the transport is the only thing this window does.
                    switch ((int)(wParam.ToUInt64() & 0xFFFF))
                    {
                        case User32.VK_SPACE:
                            Cursor.TogglePause(FrameCount);
                            return IntPtr.Zero;
                        case User32.VK_LEFT:
                            Cursor.StepBack(FrameCount);
                            return IntPtr.Zero;
                        case User32.VK_RIGHT:
                            Cursor.StepForward(FrameCount);
                            return IntPtr.Zero;
                        case User32.VK_HOME:
                            Cursor.JumpOldest();
                            return IntPtr.Zero;
                        case User32.VK_END:
                            Cursor.JumpNewest();
                            return IntPtr.Zero;
                    }
                    break;

                case User32.WM_COMMAND:
                    OnCommand((int)(wParam.ToUInt64() & 0xFFFF));
                    return IntPtr.Zero;

                case User32.WM_CLOSE:
                    User32.DestroyWindow(hwnd);
                    return IntPtr.Zero;

                case User32.WM_DESTROY:
                    if (_session is not null)
                    {
                        _session.ObservationEnabled = false;
                        _session.History.Enabled = false;
                    }
                    _session = null;
                    _window = IntPtr.Zero;
                    ReleaseDib();
                    LastReason = "closed";
                    MainWindow.OnObservationClosed();
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            _nativeExceptions++;
            MainWindow.LogObservationProblem($"{ex.GetType().Name}: {ex.Message}");
        }

        return User32.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void CreateChildren(IntPtr window)
    {
        IntPtr instance = Kernel32.GetModuleHandleW(null);

        CreateControl(window, instance, "BUTTON", "|<", IdOldest, User32.BS_PUSHBUTTON);
        CreateControl(window, instance, "BUTTON", "<", IdBack, User32.BS_PUSHBUTTON);
        CreateControl(window, instance, "BUTTON", "Pause", IdPause, User32.BS_PUSHBUTTON);
        CreateControl(window, instance, "BUTTON", ">", IdForward, User32.BS_PUSHBUTTON);
        CreateControl(window, instance, "BUTTON", ">|", IdNewest, User32.BS_PUSHBUTTON);
        CreateControl(window, instance, "STATIC", string.Empty, IdCaption, User32.SS_LEFT);

        if (_font != IntPtr.Zero)
        {
            foreach (int id in new[] { IdOldest, IdBack, IdPause, IdForward, IdNewest, IdCaption })
            {
                User32.SendMessageW(User32.GetDlgItem(window, id), User32.WM_SETFONT,
                    (UIntPtr)(ulong)_font, new IntPtr(1));
            }
        }
    }

    private static void CreateControl(IntPtr parent, IntPtr instance, string className, string text, int id, uint style)
        => User32.CreateWindowExW(0, className, text, style | User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_TABSTOP,
            0, 0, 10, 10, parent, new IntPtr(id), instance, IntPtr.Zero);

    private static void Layout(IntPtr window)
    {
        User32.GetClientRect(window, out RECT client);
        int dpi = (int)User32.GetDpiForWindow(window);
        int scale = Math.Max(1, dpi / 96);
        int gap = 8 * scale;
        int buttonHeight = ButtonHeight * scale;
        int y = client.Height - buttonHeight - gap;

        int x = gap;
        foreach ((int id, int width) in new[] { (IdOldest, 34), (IdBack, 34), (IdPause, 74), (IdForward, 34), (IdNewest, 34) })
        {
            Move(window, id, x, y, width * scale, buttonHeight);
            x += (width * scale) + (gap / 2);
        }

        Move(window, IdCaption, x + gap, y + (6 * scale), Math.Max(40, client.Width - x - (gap * 2)), 20 * scale);
    }

    private static void Move(IntPtr window, int id, int x, int y, int width, int height)
    {
        IntPtr control = User32.GetDlgItem(window, id);
        if (control != IntPtr.Zero)
        {
            User32.MoveWindow(control, x, y, width, height, true);
        }
    }

    private static void OnCommand(int id)
    {
        switch (id)
        {
            case IdOldest: Cursor.JumpOldest(); break;
            case IdBack: Cursor.StepBack(FrameCount); break;
            case IdPause: Cursor.TogglePause(FrameCount); break;
            case IdForward: Cursor.StepForward(FrameCount); break;
            case IdNewest: Cursor.JumpNewest(); break;
            default: return;
        }
        RefreshCaption();
        if (_window != IntPtr.Zero)
        {
            User32.InvalidateRect(_window, IntPtr.Zero, false);
        }
    }

    private static int FrameCount => _session?.History.Count ?? 0;

    // -----------------------------------------------------------------------------
    // Painting
    // -----------------------------------------------------------------------------

    private static unsafe void Paint(IntPtr window)
    {
        IntPtr dc = User32.BeginPaint(window, out PAINTSTRUCT paint);
        try
        {
            User32.GetClientRect(window, out RECT client);
            if (client.Width <= 0 || client.Height <= 0 || !EnsureDib(client.Width, client.Height))
            {
                return;
            }

            Fill(new ArgbPixel(0x24, 0x24, 0x24, 255));

            int gap = 8;
            int header = 34;
            int footer = ButtonHeight + (gap * 3);
            int panelWidth = Math.Max(16, (client.Width - (gap * 4)) / 3);
            int panelHeight = Math.Max(16, client.Height - header - footer);

            ReconstructionHistory? history = _session?.History;
            ReconstructionView? view = _session?.View;
            int index = Cursor.Resolve(FrameCount);
            bool replaying = Cursor.Paused && history is { Count: > 0 } && index >= 0;

            bool drawn = false;
            if (replaying && history is not null)
            {
                history.TryGetFrame(index, out HistoryFrame frame);
                int x = gap;
                BlitRgb(x, header, panelWidth, panelHeight, history.CompositeOf(index),
                    history.BlockWidth, history.BlockHeight);
                Caption(x, header - 15, panelWidth, "composite (as captured)", 0x00B0B0B0);
                x += panelWidth + gap;
                BlitRgb(x, header, panelWidth, panelHeight, history.BackgroundOf(index),
                    history.BlockWidth, history.BlockHeight);
                Caption(x, header - 15, panelWidth, "background estimate", 0x00B0B0B0);
                x += panelWidth + gap;
                BlitModel(x, header, panelWidth, panelHeight, history.ModelKindOf(index),
                    history.ModelAlphaOf(index), history.BlockWidth, history.BlockHeight);
                Caption(x, header - 15, panelWidth, "model: hue = class, brightness = alpha", 0x00B0B0B0);
                Caption(gap, client.Height - footer + 2, client.Width - (gap * 2),
                    $"paused on {index + 1}/{history.Count} of {history.TotalRecorded} recorded. {frame.Describe()}",
                    0x00D8D8D8);
                drawn = true;
            }
            else if (view is { Composite: not null, Background: not null, Model: not null })
            {
                int x = gap;
                BlitBitmap(x, header, panelWidth, panelHeight, view.Composite);
                Caption(x, header - 15, panelWidth, "composite (as captured)", 0x00B0B0B0);
                x += panelWidth + gap;
                BlitBitmap(x, header, panelWidth, panelHeight, view.Background);
                Caption(x, header - 15, panelWidth, "background estimate", 0x00B0B0B0);
                x += panelWidth + gap;
                BlitBitmap(x, header, panelWidth, panelHeight, view.Model);
                Caption(x, header - 15, panelWidth, "model: hue = class, brightness = alpha", 0x00B0B0B0);
                Caption(gap, client.Height - footer + 2, client.Width - (gap * 2),
                    history is { Count: > 0 }
                        ? $"live: {history.Count}/{history.Capacity} frames, " +
                          $"{history.Bytes / 1024.0 / 1024.0:F1} MB, 1/{history.Scale} scale, " +
                          $"refresh {view.LastRefreshMilliseconds:F2} ms, {view.Summary}"
                        : $"{view.Summary}",
                    0x00D8D8D8);
                drawn = true;
            }

            if (!drawn)
            {
                // Distinguish the three reasons an empty window has, because they need different answers.
                string reason;
                if (_session is null)
                {
                    reason = "no capture in progress - press Start in the main window, then watch the model fill in";
                }
                else if (_session.CompositorOwned)
                {
                    reason = "the operating system is drawing this cursor, so there is nothing to reconstruct - " +
                             "switch Target to Crosshair, or point at an application that draws its own cursor";
                }
                else
                {
                    reason = "waiting for the first captured frame";
                }
                LastReason = reason;
                Caption(gap, header, client.Width - (gap * 2), reason, 0x00D8D8D8);
            }
            else
            {
                LastReason = "drawing panels";
            }

            var info = new BITMAPINFO { bmiHeader = BITMAPINFOHEADER.Create(client.Width, -client.Height, 32) };
            Gdi32.SetDIBitsToDevice(dc, 0, 0, (uint)client.Width, (uint)client.Height, 0, 0, 0,
                (uint)client.Height, _bits, ref info, Gdi32.DIB_RGB_COLORS);
        }
        finally
        {
            User32.EndPaint(window, ref paint);
        }
    }

    private static void RefreshCaption()
    {
        if (_window == IntPtr.Zero)
        {
            return;
        }
        IntPtr caption = User32.GetDlgItem(_window, IdCaption);
        if (caption != IntPtr.Zero)
        {
            User32.SetWindowTextW(caption, Cursor.Describe(FrameCount));
        }
    }

    private static bool EnsureDib(int width, int height)
    {
        if (_dib != IntPtr.Zero && _dibWidth == width && _dibHeight == height)
        {
            return true;
        }

        ReleaseDib();

        IntPtr screen = User32.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            _memoryDc = Gdi32.CreateCompatibleDC(screen);
            var info = new BITMAPINFO { bmiHeader = BITMAPINFOHEADER.Create(width, -height, 32) };
            _dib = Gdi32.CreateDIBSection(screen, ref info, Gdi32.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero || _bits == IntPtr.Zero || _memoryDc == IntPtr.Zero)
            {
                ReleaseDib();
                return false;
            }
            _previousBitmap = Gdi32.SelectObject(_memoryDc, _dib);
            _dibWidth = width;
            _dibHeight = height;
            return true;
        }
        finally
        {
            User32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static void ReleaseDib()
    {
        if (_memoryDc != IntPtr.Zero)
        {
            if (_previousBitmap != IntPtr.Zero)
            {
                Gdi32.SelectObject(_memoryDc, _previousBitmap);
                _previousBitmap = IntPtr.Zero;
            }
            Gdi32.DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }
        if (_dib != IntPtr.Zero)
        {
            Gdi32.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }
        _bits = IntPtr.Zero;
        _dibWidth = 0;
        _dibHeight = 0;
    }

    private static unsafe void Fill(ArgbPixel colour)
    {
        byte* bits = (byte*)_bits;
        int count = _dibWidth * _dibHeight;
        for (int i = 0; i < count; i++)
        {
            bits[(i * 4)] = colour.B;
            bits[(i * 4) + 1] = colour.G;
            bits[(i * 4) + 2] = colour.R;
            bits[(i * 4) + 3] = 255;
        }
    }

    /// <summary>Draws a caption into the DIB, so it lands in the same blit as the panels.</summary>
    private static void Caption(int x, int y, int width, string text, int colour)
    {
        if (_memoryDc == IntPtr.Zero)
        {
            return;
        }
        Gdi32.SetBkMode(_memoryDc, Gdi32.TRANSPARENT);
        Gdi32.SetTextColor(_memoryDc, colour);
        IntPtr previous = Gdi32.SelectObject(_memoryDc, _font);
        var rect = RECT.FromSize(x, y, Math.Max(16, width), 16);
        User32.DrawTextW(_memoryDc, text, -1, ref rect, User32.DT_LEFT | User32.DT_NOPREFIX);
        Gdi32.SelectObject(_memoryDc, previous);
    }

    private static unsafe void BlitBitmap(int dx, int dy, int dw, int dh, ArgbBitmap source)
    {
        if (_bits == IntPtr.Zero || source.Width <= 0 || source.Height <= 0)
        {
            return;
        }
        byte* bits = (byte*)_bits;
        for (int y = 0; y < dh; y++)
        {
            int cy = dy + y;
            if (cy < 0 || cy >= _dibHeight)
            {
                continue;
            }
            int sy = (int)((long)y * source.Height / dh);
            byte* row = bits + ((long)cy * _dibWidth * 4);
            for (int x = 0; x < dw; x++)
            {
                int cx = dx + x;
                if (cx < 0 || cx >= _dibWidth)
                {
                    continue;
                }
                int s = ((sy * source.Width) + (int)((long)x * source.Width / dw)) * 4;
                row[cx * 4] = source.Pixels[s];
                row[(cx * 4) + 1] = source.Pixels[s + 1];
                row[(cx * 4) + 2] = source.Pixels[s + 2];
                row[(cx * 4) + 3] = 255;
            }
        }
    }

    private static unsafe void BlitRgb(int dx, int dy, int dw, int dh, ReadOnlySpan<byte> rgb, int sw, int sh)
    {
        if (_bits == IntPtr.Zero || sw <= 0 || sh <= 0 || rgb.Length < sw * sh * 3)
        {
            return;
        }
        byte* bits = (byte*)_bits;
        for (int y = 0; y < dh; y++)
        {
            int cy = dy + y;
            if (cy < 0 || cy >= _dibHeight)
            {
                continue;
            }
            int sy = (int)((long)y * sh / dh);
            byte* row = bits + ((long)cy * _dibWidth * 4);
            for (int x = 0; x < dw; x++)
            {
                int cx = dx + x;
                if (cx < 0 || cx >= _dibWidth)
                {
                    continue;
                }
                int s = ((sy * sw) + (int)((long)x * sw / dw)) * 3;
                row[cx * 4] = rgb[s];
                row[(cx * 4) + 1] = rgb[s + 1];
                row[(cx * 4) + 2] = rgb[s + 2];
                row[(cx * 4) + 3] = 255;
            }
        }
    }

    private static unsafe void BlitModel(int dx, int dy, int dw, int dh,
        ReadOnlySpan<byte> kind, ReadOnlySpan<byte> alpha, int sw, int sh)
    {
        if (_bits == IntPtr.Zero || sw <= 0 || sh <= 0 || kind.Length < sw * sh)
        {
            return;
        }
        byte* bits = (byte*)_bits;
        for (int y = 0; y < dh; y++)
        {
            int cy = dy + y;
            if (cy < 0 || cy >= _dibHeight)
            {
                continue;
            }
            int sy = (int)((long)y * sh / dh);
            byte* row = bits + ((long)cy * _dibWidth * 4);
            for (int x = 0; x < dw; x++)
            {
                int cx = dx + x;
                if (cx < 0 || cx >= _dibWidth)
                {
                    continue;
                }
                int s = (sy * sw) + (int)((long)x * sw / dw);
                byte k = kind[s];
                ArgbPixel colour = k == 255
                    ? new ArgbPixel(0x18, 0x18, 0x18, 255)
                    : ReconstructionView.PanelColour((PixelModelKind)k, alpha.Length > s ? alpha[s] : (byte)0);
                row[cx * 4] = colour.B;
                row[(cx * 4) + 1] = colour.G;
                row[(cx * 4) + 2] = colour.R;
                row[(cx * 4) + 3] = 255;
            }
        }
    }
}
