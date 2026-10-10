using RatTrap.Cli;

namespace RatTrap;

/// <summary>
/// Entry point. One binary serves both the GUI and the command line: with no arguments (or
/// with <c>gui</c>) it opens the window, otherwise it runs the named command and exits.
/// </summary>
internal static class Program
{
    private const string Version = "0.1.0";

    private static int Main(string[] args)
    {
        var cl = CommandLine.Parse(args);

        // Only wire up a console for command-line use; double-clicking the exe must not
        // create one.
        if (args.Length > 0)
        {
            ConsoleBridge.Ensure();
        }

        try
        {
            return cl.Command switch
            {
                "" or "gui" => RunGui(cl),
                "probe" => ProbeCommand.Run(cl),
                "help" or "--help" or "-h" => Help(),
                "version" or "--version" => Version0(),
                "capture" => CaptureCommand.Run(cl),
                "selftest" => RunSelfTest(),
                "reconstruct" => NotImplemented("reconstruct"),
                _ => Unknown(cl.Command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 2;
        }
    }

    private static int RunGui(CommandLine cl)
    {
        // A GUI cannot be verified by inspection in an automated run, so it can verify itself.
        return cl.GetBool("selfcheck")
            ? Ui.MainWindow.SelfCheck(cl.GetInt("selfcheck-ms", 2500), cl.Get("selfcheck-out"))
            : Ui.MainWindow.Run();
    }

    private static int NotImplemented(string command)
    {
        Console.Error.WriteLine($"Command '{command}' is not implemented yet.");
        return 3;
    }

    private static int RunSelfTest()
    {
        var lines = new List<string>();
        lines.Add("--- core: interop layout and reconstruction mathematics ---");
        lines.AddRange(SelfTest.CoreSelfTest.Run());
        lines.Add("--- output: PNG / CUR / ICO / ANI writers ---");
        lines.AddRange(Output.OutputSelfTest.Run());

        int failures = 0;
        int checks = 0;
        foreach (string line in lines)
        {
            Console.WriteLine(line);

            // Two modules report in their own style ("  ok   ..." and "PASS ..."), so accept
            // both rather than forcing one of them to change and losing the other's wording.
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("FAIL", StringComparison.Ordinal))
            {
                failures++;
                checks++;
            }
            else if (trimmed.StartsWith("ok ", StringComparison.Ordinal) ||
                     trimmed.StartsWith("PASS ", StringComparison.Ordinal))
            {
                checks++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"self-test PASSED ({checks} checks)"
            : $"self-test FAILED ({failures} of {checks} checks failed)");
        return failures == 0 ? 0 : 1;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        Help();
        return 1;
    }

    private static int Version0()
    {
        Console.WriteLine($"rat-trap {Version}");
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine($"""
            rat-trap {Version} - recover cursor bitmaps (with alpha) from screen capture

            USAGE
              rattrap [command] [options]

            COMMANDS
              gui                      Open the interactive window (default with no arguments).
              capture                  Acquire a cursor and write .cur / .png / .ani.
              probe                    Inspect monitors, the live cursor, Desktop Duplication and the
                                       interop layer. Also runs the GDI-vs-DXGI comparison that
                                       established that neither backend captures the system cursor.
              selftest                 Run the deterministic verification suite.
              help                     Show this text.
              version                  Show the version.

            OPTIONS
              --out <dir>              Where to write artefacts (default: rattrap-out).
              --preview <png>          Also write the cursor over a checkerboard.
              --cur <path>             Override the .cur path.
              --png <path>             Override the .png path.
              --ani <path>             Override the .ani path.
              --source <mode>          auto | compositor | reconstruct   (default: auto)
                                       compositor - the OS draws the cursor, so the compositor hands
                                         over its exact bitmap;
                                       reconstruct - the application draws its own cursor, so alpha
                                         and colour are solved from the captured pixels;
                                       auto - inspect what is actually available.
              --backend <name>         auto | dxgi | gdi   (default: auto)
              --monitor <index>        Monitor index as listed by 'probe'.
              --seconds <n>            Observation budget (default: 10).
              --roi <px>               Capture window size around the pointer (default: 160).
              --trim                   Shrink the output to the alpha bounding box.
              --no-nudge               Never touch the pointer. By default rat-trap displaces it by one
                                       pixel, once, and restores it, because the compositor reports a
                                       cursor shape only when it changes and an idle pointer therefore
                                       produces no report at all.
              --verbose                Per-frame detail, plus the model map for a reconstruction.
              --selfcheck[=true]       For 'gui': run the window headlessly, then exit.
              --selfcheck-ms <n>       For 'gui --selfcheck': how long to run (default: 2500).
              --selfcheck-out <path>   For 'gui --selfcheck': write the report here.

            EXAMPLES
              rattrap capture --out out\ --preview out\preview.png
              rattrap capture --source reconstruct --seconds 20 --trim
              rattrap gui --selfcheck --selfcheck-out gui-report.txt
              python tools\verify_cursor_output.py out\
            """);
        return 0;
    }
}




