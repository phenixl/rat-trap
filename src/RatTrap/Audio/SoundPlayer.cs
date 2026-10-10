using System.Runtime.InteropServices;
using RatTrap.Interop;

namespace RatTrap.Audio;

/// <summary>Which feedback cue to play.</summary>
internal enum SoundCue
{
    /// <summary>Capture started or armed.</summary>
    Start,

    /// <summary>Capture stopped because the user asked it to.</summary>
    Stop,

    /// <summary>A result was written to disk.</summary>
    Save,
}

/// <summary>
/// Plays the three feedback cues that are compiled into this program.
/// </summary>
/// <remarks>
/// <para>
/// The cues are embedded rather than taken from the operating system's sound scheme, for a reason that
/// forced the change rather than being a preference: the two system events that mean "began" and "ended"
/// are <c>DeviceConnect</c> and <c>DeviceDisconnect</c>, which are the hardware-insert and hardware-remove
/// events. Announcing "capture started" with the sound Windows plays when a USB device is plugged in is
/// actively misleading. On top of that, a scheme alias can be missing, can be remapped, and can point
/// several events at one file - on the machine this was written on, <c>FaxBeep</c>, <c>MailBeep</c> and
/// <c>Notification.Mail</c> all resolve to the same WAV - so "three distinct cues" was not something an
/// alias scheme could guarantee. The source audio is CC0, so the result can be redistributed with this
/// program.
/// </para>
/// <para>
/// <see cref="User32.PlaySoundW"/> with <c>SND_MEMORY</c> plays a WAV image straight out of memory, which
/// is the whole reason no file has to be written to disk or shipped beside the executable. The catch is
/// that with <c>SND_ASYNC</c> the buffer must stay valid for as long as the sound is playing, and a
/// managed array can be moved by the collector at any moment. The cues are therefore copied once into
/// unmanaged memory and never freed: three small blocks held for the life of the process, which is both
/// the simplest correct answer and immune to the case where a cue is still playing during shutdown.
/// </para>
/// </remarks>
internal static class SoundPlayer
{
    private static readonly IntPtr[] Cues = new IntPtr[3];
    private static readonly int[] CueLengths = new int[3];
    private static bool _loaded;

    /// <summary>
    /// Decodes the embedded cues into memory. Idempotent, and safe to call before every playback.
    /// </summary>
    /// <returns>False if no cue could be prepared, which means playback is impossible on this host.</returns>
    public static bool EnsureLoaded()
    {
        if (_loaded)
        {
            return Cues[0] != IntPtr.Zero;
        }

        _loaded = true;
        string[] encoded = [SoundCues.StartBase64, SoundCues.StopBase64, SoundCues.SaveBase64];

        for (int index = 0; index < encoded.Length; index++)
        {
            try
            {
                byte[] wav = Convert.FromBase64String(encoded[index]);
                IntPtr block = Marshal.AllocHGlobal(wav.Length);
                Marshal.Copy(wav, 0, block, wav.Length);
                Cues[index] = block;
                CueLengths[index] = wav.Length;
            }
            catch (Exception)
            {
                // A cue that cannot be decoded is a silent cue, not a failed capture.
                Cues[index] = IntPtr.Zero;
            }
        }

        return Cues[0] != IntPtr.Zero;
    }

    /// <summary>Plays one of the embedded cues.</summary>
    /// <param name="cue">Which cue.</param>
    /// <returns>
    /// True if the sound was queued. False covers both "the cue is unavailable" and "there is no audio
    /// device", and the caller reports it rather than retrying, because neither is actionable here.
    /// </returns>
    public static bool Play(SoundCue cue)
    {
        if (!EnsureLoaded())
        {
            return false;
        }

        IntPtr block = Cues[(int)cue];
        if (block == IntPtr.Zero)
        {
            return false;
        }

        // SND_ASYNC so a slow or absent audio device can never stall the message loop that is driving the
        // capture. SND_NODEFAULT so a machine that cannot play the cue is silent rather than beeping.
        return User32.PlaySoundWFromMemory(block, IntPtr.Zero,
            User32.SND_MEMORY | User32.SND_ASYNC | User32.SND_NODEFAULT);
    }

    /// <summary>Length of an embedded cue, for the self-test and the log.</summary>
    public static int LengthOf(SoundCue cue)
        => EnsureLoaded() ? CueLengths[(int)cue] : 0;

    /// <summary>Plays a WAV file from disk, which is how a user substitutes their own cue.</summary>
    public static bool PlayFile(string path)
        => User32.PlaySoundW(path, IntPtr.Zero,
            User32.SND_FILENAME | User32.SND_ASYNC | User32.SND_NODEFAULT);
}

