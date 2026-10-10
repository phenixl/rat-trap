namespace RatTrap.Capture;

/// <summary>Creates capture backends, applying fallbacks and reporting why.</summary>
public static class CaptureFactory
{
    /// <summary>
    /// Creates the single capture source a session should use for a monitor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Exactly one source, deliberately.</b> A Desktop Duplication session is exclusive per
    /// output. Asking for a second one on the same monitor fails with <c>E_INVALIDARG</c> and can
    /// disturb the first: measured while the GUI opened a duplication for the pointer shape and then
    /// asked this factory for another, after which the pointer shape never arrived at all, even
    /// though opening a single duplication reports it on the very first frame.
    /// </para>
    /// <para>
    /// One source is enough because the two roles do not conflict. Duplication excludes only the
    /// <i>system</i> cursor, which it hands over separately through <c>GetFramePointerShape</c>; a
    /// cursor that an application draws into its own frame is ordinary pixels and <b>is</b> present
    /// in the duplication image. So a single duplication is simultaneously the pointer-shape
    /// provider and the composite source the reconstruction solver needs, and no pairing of
    /// backends is required.
    /// </para>
    /// <para>
    /// An earlier version of this file offered a "composite source + background plate source" pair,
    /// on the assumption that GDI includes the cursor while duplication excludes it. Measurement
    /// disproved that on modern Windows - both exclude it, see <c>docs/measured-behaviour.md</c> -
    /// and the pair is gone rather than left as a trap.
    /// </para>
    /// </remarks>
    public static ICaptureSource Create(MonitorDescriptor monitor, CaptureBackend backend, out IReadOnlyList<string> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var notes = new List<string>();

        if (backend is CaptureBackend.Auto or CaptureBackend.DxgiDuplication)
        {
            try
            {
                var dxgi = new DxgiDuplicationSource(monitor);
                notes.Add($"Using DXGI Desktop Duplication on {monitor.DeviceName} " +
                          $"(image {dxgi.DuplicationDesc.Width}x{dxgi.DuplicationDesc.Height}, " +
                          $"system-memory image: {(dxgi.UsesSystemMemoryMapping ? "yes" : "no")}).");
                diagnostics = notes;
                return dxgi;
            }
            catch (CaptureBackendUnavailableException ex)
            {
                if (backend == CaptureBackend.DxgiDuplication)
                {
                    throw;
                }
                notes.Add($"DXGI Desktop Duplication unavailable, falling back to GDI: {ex.Message}");
            }
        }

        var gdi = new GdiScreenSource(monitor);
        notes.Add($"Using GDI BitBlt on {monitor.DeviceName}. This backend cannot report the " +
                  "compositor's pointer shape, so the system cursor can only be read through the GDI " +
                  "cursor APIs, at its native size.");
        diagnostics = notes;
        return gdi;
    }

    /// <summary>
    /// The cursor-shape provider behind a capture source, when it has one.
    /// </summary>
    /// <remarks>
    /// Declared as a helper rather than having callers test for the concrete type, so that adding a
    /// second shape-reporting backend later does not require touching every call site.
    /// </remarks>
    public static IPointerShapeProvider? AsPointerShapeProvider(ICaptureSource source)
        => source as IPointerShapeProvider;
}
