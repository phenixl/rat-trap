using RatTrap.Interop;

namespace RatTrap.Pointer;

/// <summary>
/// Makes the composition engine re-announce the cursor shape.
/// </summary>
/// <remarks>
/// <para>
/// Desktop Duplication only fills in the pointer shape on the frame where that shape
/// <i>changed</i>. On a machine where nobody is touching the mouse, no shape ever arrives, and a
/// tool that just waits looks broken even though the cursor is plainly visible on screen. Measured
/// here: nine seconds of an idle desktop produced 83 frames and zero shape reports, while the same
/// code obtained a shape immediately during a session in which the mouse was moving.
/// </para>
/// <para>
/// So the pointer is displaced by one pixel and immediately put back. That is a real, if tiny,
/// modification of the user's input state, which is why it happens at most once per session, only
/// after a natural shape has failed to appear, only when the caller asks for it, and is undone
/// before the call returns. Callers that would rather not touch the pointer at all can turn it off
/// and take the GDI cursor bitmap instead, at the cost of native-size artwork and a less dependable
/// alpha channel.
/// </para>
/// </remarks>
internal static class PointerNudge
{
    /// <summary>
    /// Displaces the pointer one pixel and restores it. Returns false if the pointer could not be
    /// read or moved, which happens when the foreground window has captured the pointer.
    /// </summary>
    public static bool TryNudge(out POINT original)
    {
        original = default;
        if (!User32.GetCursorPos(out POINT position))
        {
            return false;
        }

        original = position;

        // The two moves need to be separated in time. Issued back to back they are coalesced into a
        // single position change and the compositor never recomposites - measured: a zero-delay
        // nudge produced no shape report at all, while the same nudge with a pause does.
        bool moved = User32.SetCursorPos(position.X + 1, position.Y);
        Thread.Sleep(NudgeSettleMs);
        bool restored = User32.SetCursorPos(position.X, position.Y);
        return moved && restored;
    }

    /// <summary>Gap between the two nudged positions, long enough for a recomposition to be observed.</summary>
    private const int NudgeSettleMs = 60;
}
