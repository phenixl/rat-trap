using System.Text;
using Microsoft.Win32.SafeHandles;
using RatTrap.Interop;

namespace RatTrap.Cli;

/// <summary>
/// Gives a GUI-subsystem executable usable stdout/stderr when it is launched from a
/// terminal.
/// </summary>
/// <remarks>
/// <para>
/// rat-trap is built as <c>WinExe</c> so that double-clicking it does not flash a console
/// window, and so a single binary serves both the GUI and the command line. The cost is
/// that the process has no console by default, so a CLI invocation needs two steps:
/// </para>
/// <list type="number">
///   <item>if stdout was already redirected (a pipe from a parent process or a shell
///     redirection), .NET's <c>Console</c> uses that handle directly and nothing is
///     needed;</item>
///   <item>otherwise attach to the parent process's console and reopen
///     <c>CONOUT$</c>/<c>CONIN$</c>, because the handles inherited at startup were
///     invalid for a GUI subsystem image.</item>
/// </list>
/// </remarks>
internal static class ConsoleBridge
{
    private static bool _initialized;

    /// <summary>True when text written to <see cref="Console"/> has somewhere to go.</summary>
    public static bool HasOutput { get; private set; }

    public static bool Ensure()
    {
        if (_initialized)
        {
            return HasOutput;
        }
        _initialized = true;

        if (Console.IsOutputRedirected)
        {
            // A parent process handed us a pipe; Console already works.
            HasOutput = true;
            return true;
        }

        if (Kernel32.GetConsoleWindow() != IntPtr.Zero)
        {
            HasOutput = true;
            return true;
        }

        if (!Kernel32.AttachConsole(Kernel32.ATTACH_PARENT_PROCESS))
        {
            HasOutput = false;
            return false;
        }

        HasOutput = true;
        TryReopen("CONOUT$", Kernel32.STD_OUTPUT_HANDLE, FileAccess.Write, out _);
        TryReopen("CONOUT$", Kernel32.STD_ERROR_HANDLE, FileAccess.Write, out _);
        TryReopen("CONIN$", Kernel32.STD_INPUT_HANDLE, FileAccess.Read, out _);

        // The console code page can be a non-UTF8 codepage; make accented output safe.
        _ = Kernel32.SetConsoleOutputCP(65001);

        Console.WriteLine();
        return true;
    }

    private static bool TryReopen(string deviceName, int stdHandle, FileAccess access, out Stream? stream)
    {
        stream = null;

        uint desiredAccess = access == FileAccess.Read
            ? Kernel32.GENERIC_READ
            : Kernel32.GENERIC_WRITE;
        if (access == FileAccess.ReadWrite)
        {
            desiredAccess = Kernel32.GENERIC_READ | Kernel32.GENERIC_WRITE;
        }

        IntPtr handle = Kernel32.CreateFileW(deviceName, desiredAccess,
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE,
            IntPtr.Zero, Kernel32.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return false;
        }

        Kernel32.SetStdHandle(stdHandle, handle);

        var safeHandle = new SafeFileHandle(handle, ownsHandle: true);
        var fileStream = new FileStream(safeHandle, access, bufferSize: 1024);
        stream = fileStream;

        switch (stdHandle)
        {
            case Kernel32.STD_OUTPUT_HANDLE:
                Console.SetOut(new StreamWriter(fileStream, new UTF8Encoding(false)) { AutoFlush = true });
                break;
            case Kernel32.STD_ERROR_HANDLE:
                Console.SetError(new StreamWriter(fileStream, new UTF8Encoding(false)) { AutoFlush = true });
                break;
            case Kernel32.STD_INPUT_HANDLE:
                Console.SetIn(new StreamReader(fileStream, Encoding.UTF8));
                break;
        }

        return true;
    }
}
