namespace RatTrap.Reconstruct;

/// <summary>
/// Where a replay is looking: following the newest frame, or parked on one and stepping around.
/// </summary>
/// <remarks>
/// <para>
/// The interesting part of a replay is not the drawing, it is this: a position that can follow the head as it
/// grows, freeze without stopping the thing that is growing, and step by exactly one frame without running off
/// either end. Keeping it as a plain object rather than as state inside a window procedure means those
/// properties can be asserted by a test, which is where the clamping and the off-by-one mistakes live.
/// </para>
/// <para>
/// Pausing is deliberately only about the view. The recording continues behind it, so a paused replay is a
/// window onto a run that is still happening, and the frame it is parked on stays put while the count beside
/// it grows. That distinction is the whole reason a pause button is safe to offer during a capture.
/// </para>
/// </remarks>
internal sealed class ReplayCursor
{
    /// <summary>True while the view is parked instead of following the newest frame.</summary>
    public bool Paused { get; private set; }

    /// <summary>The parked position, or -1 when following the newest frame.</summary>
    public int ParkedIndex { get; private set; } = -1;

    /// <summary>Total steps taken, which the self-check reports so a dead control is visible.</summary>
    public int Steps { get; private set; }

    /// <summary>The frame to display, given how many frames exist. Clamps rather than trusting the caller.</summary>
    public int Resolve(int count)
    {
        if (count <= 0)
        {
            return -1;
        }
        if (!Paused || ParkedIndex < 0)
        {
            return count - 1;
        }
        return Math.Clamp(ParkedIndex, 0, count - 1);
    }

    /// <summary>Freezes the view on whatever is currently displayed.</summary>
    public void Pause(int count)
    {
        Steps++;
        ParkedIndex = Resolve(count);
        Paused = count > 0;
    }

    /// <summary>Goes back to following the newest frame.</summary>
    public void Resume()
    {
        Steps++;
        Paused = false;
        ParkedIndex = -1;
    }

    /// <summary>Toggles between following and parked.</summary>
    public void TogglePause(int count)
    {
        if (Paused)
        {
            Resume();
        }
        else
        {
            Pause(count);
        }
    }

    /// <summary>Moves one frame towards the oldest, pausing first if it was following.</summary>
    /// <returns>The frame now displayed.</returns>
    public int StepBack(int count)
    {
        Steps++;
        if (count <= 0)
        {
            return -1;
        }

        // Stepping from a live view starts at the head, not at zero: the frame you were just looking at is the
        // one you want to move away from.
        int from = Resolve(count);
        Paused = true;
        ParkedIndex = Math.Max(0, from - 1);
        return ParkedIndex;
    }

    /// <summary>Moves one frame towards the newest, without ever passing it. Does not resume.</summary>
    /// <returns>The frame now displayed.</returns>
    public int StepForward(int count)
    {
        Steps++;
        if (count <= 0)
        {
            return -1;
        }

        int from = Resolve(count);
        Paused = true;
        ParkedIndex = Math.Min(count - 1, from + 1);
        return ParkedIndex;
    }

    /// <summary>Jumps to the oldest frame and parks there.</summary>
    public int JumpOldest()
    {
        Steps++;
        Paused = true;
        ParkedIndex = 0;
        return 0;
    }

    /// <summary>Jumps to the newest frame and follows it again.</summary>
    public void JumpNewest() => Resume();

    public string Describe(int count)
        => Paused && count > 0
            ? $"paused on {Resolve(count) + 1}/{count}"
            : "following the newest frame";
}
