using System.Globalization;
using System.Text;
using RatTrap.Interop;

namespace RatTrap.Settings;

/// <summary>What the tool is looking for.</summary>
public enum CaptureTarget
{
    /// <summary>Follow the mouse pointer and recover the cursor it is drawing.</summary>
    Cursor = 0,

    /// <summary>
    /// Watch a fixed square at the centre of the focused window and recover whatever is drawn
    /// there, which is how a first-person shooter's crosshair behaves: it does not move, the scene
    /// moves behind it.
    /// </summary>
    Crosshair = 1,
}

/// <summary>
/// Persistent preferences, stored as an INI file beside the executable.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately portable: the file sits next to the program rather than in the registry or in
/// <c>%APPDATA%</c>, so the whole thing can be carried on a USB stick and so a user can read and
/// edit every setting in a text editor. If the program's own directory is not writable - which
/// happens when it is unpacked under <c>Program Files</c> - it falls back to
/// <c>%LOCALAPPDATA%\rat-trap</c> and says so, rather than silently losing the changes.
/// </para>
/// <para>
/// INI rather than JSON on purpose: no serializer, therefore nothing to trim or reflect over
/// under Native AOT, and a format a human can fix by hand.
/// </para>
/// </remarks>
public sealed class AppSettings
{
    public const string FileName = "rat-trap.ini";

    /// <summary>Where the settings were loaded from (or would be written to).</summary>
    public string FilePath { get; private set; } = string.Empty;

    /// <summary>True when the program directory was not writable and the fallback is in use.</summary>
    public bool UsingFallbackPath { get; private set; }

    // --- values -------------------------------------------------------------------
    public CaptureTarget Target { get; set; } = CaptureTarget.Cursor;

    /// <summary>Directory for automatically saved cursors. Relative paths resolve against the program directory.</summary>
    public string SaveDirectory { get; set; } = "captures";

    /// <summary>Automatically write every supported format as soon as a cursor is recovered.</summary>
    public bool AutoSave { get; set; } = true;

    /// <summary>Play a system sound when a capture starts, stops, or saves a result.</summary>
    public bool PlaySound { get; set; } = true;

    /// <summary>
    /// Which cue to play for a completed save: <c>builtin</c>, or the path of a WAV file to use instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three cues are compiled into the program rather than named by a system sound alias, and that is
    /// a correction rather than a preference. The system events that mean "began" and "ended" are
    /// <c>DeviceConnect</c> and <c>DeviceDisconnect</c>, which are the hardware-insert and hardware-remove
    /// events: using them for "capture started" sounds like a USB device being plugged in. A scheme alias
    /// can also be missing, be remapped, or point several events at one file, so distinctness was not
    /// something it could guarantee.
    /// </para>
    /// <para>
    /// Values are case-insensitive. Anything other than <c>builtin</c> is treated as a path to a WAV file,
    /// and a path that does not exist falls back to the built-in cue with a line in the log rather than
    /// failing silently.
    /// </para>
    /// </remarks>
    public string SaveSound { get; set; } = BuiltinSound;

    /// <summary>Which cue to play when a capture starts: <c>builtin</c>, or a path to a WAV file.</summary>
    public string StartSound { get; set; } = BuiltinSound;

    /// <summary>Which cue to play when a capture is stopped: <c>builtin</c>, or a path to a WAV file.</summary>
    public string StopSound { get; set; } = BuiltinSound;

    /// <summary>The setting value that means "use the cue compiled into the program".</summary>
    public const string BuiltinSound = "builtin";

    /// <summary>
    /// The value of the retired <c>soundalias</c> key, if the file still has one.
    /// </summary>
    /// <remarks>
    /// Kept only so that the window can tell the user the setting has been replaced, once, instead of the
    /// old value either being ignored in silence or being mistaken for a file path.
    /// </remarks>
    public string? LegacySoundAlias { get; private set; }

    /// <summary>True when a configured cue value means the embedded cue rather than a file.</summary>
    public static bool IsBuiltinSound(string? value)
        => string.IsNullOrWhiteSpace(value) ||
           value.Trim().Equals(BuiltinSound, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Things the user should be told about their settings file, collected while loading it.
    /// </summary>
    /// <remarks>
    /// Notes rather than log calls, because loading a file should not depend on a logger, and collected
    /// rather than emitted on use, because a message repeated on every capture stops being read. The window
    /// prints these once at start-up.
    /// </remarks>
    public List<string> Notes { get; } = [];

    /// <summary>How long to accumulate observations before solving, in seconds.</summary>
    public int ReconstructSeconds { get; set; } = 8;

    /// <summary>Shrink the saved cursor to its alpha bounding box.</summary>
    public bool Trim { get; set; } = false;

    /// <summary>
    /// Displace the pointer by one pixel, once, when the compositor has not reported a cursor
    /// shape. Needed because the compositor only reports a shape when it changes.
    /// </summary>
    public bool NudgePointer { get; set; } = true;

    /// <summary>Side of the fixed capture square used by crosshair mode.</summary>
    public int CrosshairSize { get; set; } = 256;

    /// <summary>
    /// Distance, in pixels, at which crosshair mode samples background from around each pixel.
    /// Must exceed the crosshair's stroke width for the sample to land off the crosshair.
    /// </summary>
    public int CrosshairNeighbourDistance { get; set; } = 9;

    /// <summary>Global hot key that starts and stops capture, e.g. <c>Ctrl+Insert</c>.</summary>
    public string HotKey { get; set; } = "Ctrl+Insert";

    /// <summary>Enable the global hot key.</summary>
    public bool HotKeyEnabled { get; set; } = true;

    /// <summary>Write the model classification map to the log after each reconstruction.</summary>
    public bool VerboseModelMap { get; set; } = false;

    // --- hot key parsing ----------------------------------------------------------

    /// <summary>Virtual-key codes for the key names this program accepts in a hot key.</summary>
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["INSERT"] = 0x2D,
        ["DELETE"] = 0x2E,
        ["HOME"] = 0x24,
        ["END"] = 0x23,
        ["PAGEUP"] = 0x21,
        ["PAGEDOWN"] = 0x22,
        ["SPACE"] = 0x20,
        ["TAB"] = 0x09,
        ["BACKSPACE"] = 0x08,
        ["ENTER"] = 0x0D,
        ["ESC"] = 0x1B,
        ["ESCAPE"] = 0x1B,
        ["PAUSE"] = 0x13,
        ["PRINTSCREEN"] = 0x2C,
        ["NUMPAD0"] = 0x60,
        ["NUMPAD1"] = 0x61,
        ["NUMPAD2"] = 0x62,
        ["NUMPAD3"] = 0x63,
        ["NUMPAD4"] = 0x64,
        ["NUMPAD5"] = 0x65,
        ["NUMPAD6"] = 0x66,
        ["NUMPAD7"] = 0x67,
        ["NUMPAD8"] = 0x68,
        ["NUMPAD9"] = 0x69,
    };

    /// <summary>
    /// Parses a hot key description such as <c>Ctrl+Insert</c> or <c>Ctrl+Shift+F9</c>.
    /// </summary>
    /// <remarks>
    /// A parse failure is reported rather than silently ignored, because a hot key that does not
    /// work is otherwise indistinguishable from a hot key that was never registered, and the user
    /// would have no way to tell which.
    /// </remarks>
    public static bool TryParseHotKey(string? text, out uint modifiers, out uint virtualKey, out string error)
    {
        modifiers = 0;
        virtualKey = 0;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "the hot key is empty";
            return false;
        }

        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "the hot key is empty";
            return false;
        }

        uint key = 0;
        bool haveKey = false;

        foreach (string part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= User32.MOD_CONTROL;
                    continue;
                case "ALT":
                    modifiers |= User32.MOD_ALT;
                    continue;
                case "SHIFT":
                    modifiers |= User32.MOD_SHIFT;
                    continue;
                case "WIN":
                case "WINDOWS":
                    modifiers |= User32.MOD_WIN;
                    continue;
            }

            if (haveKey)
            {
                error = $"'{text}' names more than one key ('{part}' is the second)";
                return false;
            }

            if (NamedKeys.TryGetValue(part, out uint named))
            {
                key = named;
                haveKey = true;
                continue;
            }

            // F1..F24
            if (part.Length >= 2 && (part[0] is 'F' or 'f') &&
                int.TryParse(part[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int fn) &&
                fn is >= 1 and <= 24)
            {
                key = (uint)(0x70 + fn - 1);
                haveKey = true;
                continue;
            }

            // A single letter or digit, which is how the virtual-key codes are laid out for ASCII.
            if (part.Length == 1)
            {
                char c = char.ToUpperInvariant(part[0]);
                if (c is >= 'A' and <= 'Z')
                {
                    key = c;
                    haveKey = true;
                    continue;
                }
                if (c is >= '0' and <= '9')
                {
                    key = c;
                    haveKey = true;
                    continue;
                }
            }

            error = $"'{part}' is not a key name this program understands";
            return false;
        }

        if (!haveKey)
        {
            error = $"'{text}' has modifiers but no key";
            return false;
        }

        virtualKey = key;
        // MOD_NOREPEAT stops the key's auto-repeat from toggling capture many times per second.
        modifiers |= User32.MOD_NOREPEAT;
        return true;
    }

    // --- persistence --------------------------------------------------------------

    /// <summary>
    /// Where the settings file and the default save directory go when the program directory is not writable.
    /// </summary>
    /// <remarks>
    /// Named once because both users of it must agree. They did not: the settings file fell back here while the
    /// save directory did not, so an unelevated run from a protected location wrote its preferences successfully
    /// and then advertised a save directory it could not create.
    /// </remarks>
    public static string FallbackRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rat-trap");

    /// <summary>Resolves the settings path, preferring the program directory.</summary>
    private static string ResolvePath(out bool usingFallback, out string? storageProblem)
    {
        usingFallback = false;
        storageProblem = null;
        string programDirectory = AppContext.BaseDirectory;
        string candidate = Path.Combine(programDirectory, FileName);

        try
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }

            // Probe writability without leaving anything behind.
            string probe = Path.Combine(programDirectory, $".rattrap-write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return candidate;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            usingFallback = true;

            // Creating the fallback root can itself fail, and this is the branch that is supposed to be the
            // graceful one. It was not: an unhandled UnauthorizedAccessException here took down the whole
            // program before a window appeared, on a machine where the program directory is read-only and the
            // per-user location is unavailable too - a locked-down or redirected profile, for instance. Running
            // without persisting settings is a poor outcome; refusing to start is a worse one, and saying
            // nothing about it is worse still.
            try
            {
                Directory.CreateDirectory(FallbackRoot);
                return Path.Combine(FallbackRoot, FileName);
            }
            catch (Exception fallbackFailure) when (fallbackFailure is UnauthorizedAccessException or IOException)
            {
                storageProblem = $"{fallbackFailure.GetType().Name}: {fallbackFailure.Message}";
                // Still read from beside the program: a settings file there may be readable even though it
                // cannot be written, and honouring it beats ignoring it.
                return candidate;
            }
        }
    }

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        settings.FilePath = ResolvePath(out bool fallback, out string? storageProblem);
        settings.UsingFallbackPath = fallback;
        if (storageProblem is not null)
        {
            settings.Notes.Add($"settings cannot be written anywhere this program can reach " +
                               $"({storageProblem}); changes will be lost when it closes. Run it from a location " +
                               $"this account can write to, or create {FallbackRoot} yourself.");
        }

        if (!File.Exists(settings.FilePath))
        {
            return settings;
        }

        try
        {
            foreach (string rawLine in File.ReadAllLines(settings.FilePath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] is ';' or '#' or '[')
                {
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                string key = line[..equals].Trim();
                string value = line[(equals + 1)..].Trim();
                settings.Apply(key, value);
            }
        }
        catch (Exception)
        {
            // A corrupt settings file must not stop the program from starting; defaults are fine
            // and the next Save() rewrites it.
        }

        settings.Clamp();
        return settings;
    }

    private void Apply(string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "target":
                Target = value.Equals("crosshair", StringComparison.OrdinalIgnoreCase)
                    ? CaptureTarget.Crosshair
                    : CaptureTarget.Cursor;
                break;
            case "savedirectory": SaveDirectory = value; break;
            case "autosave": AutoSave = ParseBool(value, AutoSave); break;
            case "playsound": PlaySound = ParseBool(value, PlaySound); break;
            case "savesound": SaveSound = value; break;
            // The old key named a system sound alias. It is recognised rather than ignored so that the
            // change is announced, but it is deliberately NOT copied into SaveSound: the value is an alias
            // such as FaxBeep, which is not a file, and quietly carrying it over would log a complaint about
            // a setting the user never wrote in this form. The key disappears when the file is next saved.
            case "soundalias":
                Notes.Add($"the 'soundalias' setting ({value}) has been replaced: the cues are now built into " +
                          "the program, so startsound/stopsound/savesound take 'builtin' or a path to a .wav");
                break;
            case "startsound": StartSound = value; break;
            case "stopsound": StopSound = value; break;
            case "reconstructseconds": ReconstructSeconds = ParseInt(value, ReconstructSeconds); break;
            case "trim": Trim = ParseBool(value, Trim); break;
            case "nudgepointer": NudgePointer = ParseBool(value, NudgePointer); break;
            case "crosshairsize": CrosshairSize = ParseInt(value, CrosshairSize); break;
            case "crosshairneighbourdistance": CrosshairNeighbourDistance = ParseInt(value, CrosshairNeighbourDistance); break;
            case "hotkey": HotKey = value; break;
            case "hotkeyenabled": HotKeyEnabled = ParseBool(value, HotKeyEnabled); break;
            case "verbosemodelmap": VerboseModelMap = ParseBool(value, VerboseModelMap); break;
        }
    }

    private static bool ParseBool(string value, bool fallback)
        => value.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };

    /// <summary>
    /// Reduces a cue setting to <c>builtin</c> or an existing file, recording a note when it does neither.
    /// </summary>
    /// <remarks>
    /// The earlier version of this setting held a system sound alias, so a config written by that version
    /// contains <c>savesound = FaxBeep</c> under the same key name this one uses. Left alone, that value is
    /// neither <c>builtin</c> nor a file, and the fall-back would fire on every single capture while the
    /// file never corrected itself. Normalising at load makes the message a one-off and lets the next save
    /// write a value that is actually valid.
    /// </remarks>
    private string NormalizeSound(string value, string key)
    {
        if (IsBuiltinSound(value))
        {
            return BuiltinSound;
        }

        string candidate = value.Trim();
        if (File.Exists(candidate))
        {
            return candidate;
        }

        Notes.Add($"the {key} setting names '{candidate}', which is neither 'builtin' nor an existing file, " +
                  "so the built-in cue is used");
        return BuiltinSound;
    }

    private static int ParseInt(string value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;

    private void Clamp()
    {
        CrosshairSize = Math.Clamp(CrosshairSize, 32, 1024);
        CrosshairNeighbourDistance = Math.Clamp(CrosshairNeighbourDistance, 2, 64);
        ReconstructSeconds = Math.Clamp(ReconstructSeconds, 1, 600);
        if (string.IsNullOrWhiteSpace(SaveDirectory))
        {
            SaveDirectory = "captures";
        }
        StartSound = NormalizeSound(StartSound, "startsound");
        StopSound = NormalizeSound(StopSound, "stopsound");
        SaveSound = NormalizeSound(SaveSound, "savesound");
        if (string.IsNullOrWhiteSpace(HotKey))
        {
            HotKey = "Ctrl+Insert";
        }
    }

    /// <summary>Absolute path of the directory automatically saved cursors go into.</summary>
    public string ResolveSaveDirectory()
    {
        string directory = SaveDirectory;
        if (Path.IsPathRooted(directory))
        {
            // An absolute path is an instruction rather than a default, so it is honoured even when it turns out
            // to be unusable. Saving somewhere the user did not ask for, silently, would be worse than reporting
            // that the place they chose cannot be written to.
            return Path.GetFullPath(directory);
        }

        // A relative path means "beside the program", which is only meaningful when the program directory can be
        // written to. When it cannot - an installed copy under Program Files, a read-only share, or simply an
        // unelevated run from such a location - the save directory has to follow the settings file to the
        // fallback root. It previously did not, and the symptom was a program that showed a save path it could
        // not create, refused to open that folder, and would then have failed to save a capture into it.
        string root = UsingFallbackPath ? FallbackRoot : AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(root, directory));
    }

    public void Save()
    {
        var text = new StringBuilder();
        text.AppendLine("; rat-trap settings. Portable: this file lives beside the program.");
        text.AppendLine("; Delete it to return to defaults. Every value can be edited by hand.");
        text.AppendLine();
        text.AppendLine("[capture]");
        text.AppendLine($"; cursor | crosshair   (crosshair watches a fixed square at the centre of the focused window)");
        text.AppendLine($"target = {(Target == CaptureTarget.Crosshair ? "crosshair" : "cursor")}");
        text.AppendLine("; relative paths resolve against the program directory");
        text.AppendLine($"savedirectory = {SaveDirectory}");
        text.AppendLine($"autosave = {Bool(AutoSave)}");
        text.AppendLine($"playsound = {Bool(PlaySound)}");
        text.AppendLine("; 'builtin' plays the cue compiled into the program. Any other value is a path to a WAV file.");
        text.AppendLine("; The three built-in cues are distinct: the start cue sweeps upward, the stop cue is the same");
        text.AppendLine("; sound swept downward, and the save cue is a darker, warmer tone, so all three are told apart");
        text.AppendLine("; by ear alone - which matters when the window is behind a fullscreen game.");
        text.AppendLine($"startsound = {StartSound}");
        text.AppendLine($"stopsound = {StopSound}");
        text.AppendLine($"savesound = {SaveSound}");
        text.AppendLine($"; how long to accumulate observations before solving, in seconds");
        text.AppendLine($"reconstructseconds = {ReconstructSeconds}");
        text.AppendLine($"trim = {Bool(Trim)}");
        text.AppendLine($"nudgepointer = {Bool(NudgePointer)}");
        text.AppendLine($"verbosemodelmap = {Bool(VerboseModelMap)}");
        text.AppendLine();
        text.AppendLine("[crosshair]");
        text.AppendLine($"crosshairsize = {CrosshairSize}");
        text.AppendLine("; must exceed the crosshair's stroke width, or the background sample lands on it");
        text.AppendLine($"crosshairneighbourdistance = {CrosshairNeighbourDistance}");
        text.AppendLine();
        text.AppendLine("[hotkey]");
        text.AppendLine("; modifiers Ctrl/Alt/Shift/Win plus one key, e.g. Ctrl+Insert, Ctrl+Shift+F9");
        text.AppendLine($"hotkey = {HotKey}");
        text.AppendLine($"hotkeyenabled = {Bool(HotKeyEnabled)}");

        try
        {
            File.WriteAllText(FilePath, text.ToString());
        }
        catch (Exception)
        {
            // Losing a settings write is not worth interrupting a capture for.
        }
    }

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>One-line summary for the log.</summary>
    public string Describe()
        => $"target={Target}, out={ResolveSaveDirectory()}, hotkey={(HotKeyEnabled ? HotKey : "off")}, " +
           $"autosave={AutoSave}, sound={PlaySound}";
}


