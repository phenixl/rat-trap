using RatTrap.Capture;
using RatTrap.Interop;

namespace RatTrap.Pointer;

/// <summary>How the cursor hotspot was located inside a pointer-shape bitmap.</summary>
public enum PointerAnchorSource
{
    /// <summary>The shape's own <c>HotSpot</c> field was used.</summary>
    ShapeField = 0,
    /// <summary>The hotspot was measured against <c>GetCursorPos</c> because the field was unusable.</summary>
    Measured = 1,
    /// <summary>Neither was available; the top-left was assumed.</summary>
    AssumedTopLeft = 2,
}

/// <summary>The resolved relationship between the compositor's pointer report and the hotspot.</summary>
public readonly record struct PointerAnchor(
    int HotspotX,
    int HotspotY,
    PointerAnchorSource Source,
    string Explanation);

/// <summary>
/// Works out where the hotspot is inside a DXGI pointer-shape bitmap.
/// </summary>
/// <remarks>
/// <para>
/// This is not a documented lookup, because the documentation does not settle it. The
/// relationship between <c>DXGI_OUTDUPL_POINTER_POSITION.Position</c>, the shape's
/// <c>HotSpot</c> field, and <c>GetCursorPos</c> is ambiguous in the reference material, so this
/// program measures it instead. The measurement on the development machine
/// (<c>docs/measured-behaviour.md</c>, section 5) is:
/// </para>
/// <code>
///   GetCursorPos (settled)      = (1739,1954)   the hotspot, by definition
///   DXGI pointer in desktop     = (1727,1943)
///   difference                  = (12,11)
///   shape HotSpot field         = (11,11)
/// </code>
/// <para>
/// The difference equals the shape's own hotspot field to within a pixel, which means the reported
/// position is the bitmap's <b>top-left</b> and the field is the hotspot inside it. The residual
/// pixel is explained by the pointer moving between the two samples, since they cannot be taken
/// atomically.
/// </para>
/// <para>
/// The field is preferred because it needs no assumption about when the two samples were taken,
/// but it is not trusted blindly: a driver reporting (0,0) while the measurement clearly disagrees
/// gets the measured value instead, and either way the discrepancy is reported rather than hidden.
/// </para>
/// </remarks>
public static class PointerAnchorResolver
{
    public static PointerAnchor Resolve(in PointerShapeSnapshot shape, MonitorDescriptor monitor,
        bool havePointerPosition, POINT pointerPositionInOutput, POINT currentCursorPosition)
    {
        bool fieldIsUsable = shape.HotspotX != 0 || shape.HotspotY != 0;

        if (!havePointerPosition)
        {
            return fieldIsUsable
                ? new PointerAnchor(shape.HotspotX, shape.HotspotY, PointerAnchorSource.ShapeField,
                    "Used the shape's HotSpot field; the duplication session reported no pointer position to cross-check it against.")
                : new PointerAnchor(0, 0, PointerAnchorSource.AssumedTopLeft,
                    "Neither a HotSpot field nor a pointer position was available; assuming the top-left corner.");
        }

        int reportedX = monitor.Rect.Left + pointerPositionInOutput.X;
        int reportedY = monitor.Rect.Top + pointerPositionInOutput.Y;
        int measuredX = currentCursorPosition.X - reportedX;
        int measuredY = currentCursorPosition.Y - reportedY;

        if (!fieldIsUsable)
        {
            // A (0,0) field cannot be distinguished from "not filled in", so trust the measurement.
            return new PointerAnchor(measuredX, measuredY, PointerAnchorSource.Measured,
                $"The shape's HotSpot field was (0,0) but the measured offset from the reported pointer " +
                $"position to GetCursorPos was ({measuredX},{measuredY}); using the measured value.");
        }

        int discrepancy = Math.Max(Math.Abs(measuredX - shape.HotspotX), Math.Abs(measuredY - shape.HotspotY));
        string explanation = discrepancy <= 1
            ? $"Cross-checked against GetCursorPos: measured offset ({measuredX},{measuredY}) matches the " +
              $"shape's HotSpot field ({shape.HotspotX},{shape.HotspotY})."
            : $"The shape's HotSpot field ({shape.HotspotX},{shape.HotspotY}) disagrees with the measured " +
              $"offset ({measuredX},{measuredY}) from GetCursorPos by {discrepancy}px, which is larger than " +
              "pointer movement explains. Using the field, but the exported cursor may be off by that much.";

        return new PointerAnchor(shape.HotspotX, shape.HotspotY, PointerAnchorSource.ShapeField, explanation);
    }
}
