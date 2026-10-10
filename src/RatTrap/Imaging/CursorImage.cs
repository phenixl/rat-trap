namespace RatTrap.Imaging;

/// <summary>How a recovered cursor should be written to a .cur / .ico / .ani file.</summary>
public enum CursorEncoding
{
    /// <summary>
    /// 32bpp BGRA with a real alpha channel, plus a derived 1bpp AND mask for legacy
    /// consumers. This is the normal, modern encoding and the default.
    /// </summary>
    AlphaBlend32 = 0,

    /// <summary>
    /// 32bpp BGRA whose colours are XORed into the desktop, with an all-zero AND mask.
    /// This is the only encoding that reproduces a screen-composited XOR cursor
    /// (DXGI <c>MASKED_COLOR</c>) with full fidelity: the file stores the mask itself,
    /// so Windows re-renders exactly what was on screen. <see cref="CursorImage.XorMaskBgra"/>
    /// must be set.
    /// </summary>
    XorMasked32 = 1,

    /// <summary>
    /// Legacy 1bpp cursor: a 1bpp AND mask plus a 1bpp XOR mask. Needed to reproduce a
    /// monochrome cursor exactly, because "invert the background" cannot be expressed by
    /// a 32bpp XOR colour (see docs/algorithm.md). <see cref="CursorImage.AndMask1Bpp"/>
    /// and <see cref="CursorImage.XorMask1Bpp"/> must be set.
    /// </summary>
    Monochrome1 = 2,
}

/// <summary>Where a <see cref="CursorImage"/> came from. Purely for reporting.</summary>
public enum CursorProvenance
{
    Unknown = 0,
    /// <summary>Recovered by the screen-capture inverse solver (the rat-trap core).</summary>
    Reconstructed = 1,
    /// <summary>Read straight from the live system cursor via GetCursorInfo/GetIconInfo.</summary>
    SystemCursorApi = 2,
    /// <summary>Taken from the compositor via DXGI Desktop Duplication GetFramePointerShape.</summary>
    DxgiPointerShape = 3,
    /// <summary>Loaded from a .cur/.ani/.ico file on disk.</summary>
    File = 4,
    /// <summary>Produced by the built-in synthetic self-test.</summary>
    Synthetic = 5,
}

/// <summary>
/// A complete cursor definition: the pixels, the hotspot, and enough information to
/// write it back out without losing how it was composited.
/// </summary>
public sealed class CursorImage
{
    /// <summary>
    /// The cursor as a straight-alpha BGRA image. For <see cref="CursorEncoding.XorMasked32"/>
    /// this is the mask rendered over black (which is what the user sees over a black
    /// background); use <see cref="XorMaskBgra"/> for bit-exact output.
    /// </summary>
    public ArgbBitmap Image { get; }

    /// <summary>Hotspot X in pixels, relative to the top-left of <see cref="Image"/>.</summary>
    public int HotspotX { get; init; }

    /// <summary>Hotspot Y in pixels, relative to the top-left of <see cref="Image"/>.</summary>
    public int HotspotY { get; init; }

    /// <summary>Encoding to use when writing a cursor file.</summary>
    public CursorEncoding PreferredEncoding { get; init; } = CursorEncoding.AlphaBlend32;

    /// <summary>Exact XOR mask bytes (straight BGRA, <c>W*H*4</c>) for <see cref="CursorEncoding.XorMasked32"/>.</summary>
    public byte[]? XorMaskBgra { get; init; }

    /// <summary>
    /// 1bpp AND mask for <see cref="CursorEncoding.Monochrome1"/>. Convention, used by
    /// every mask in this project: <b>top-down</b> rows, left-to-right bit order with
    /// bit 7 of the first byte being the leftmost pixel, each row padded to a
    /// <b>4-byte (DWORD)</b> boundary. Length must be
    /// <c>Height * (((Width + 31) / 32) * 4)</c>. The file writer flips to the
    /// bottom-up order that the ICO/CUR container requires.
    /// </summary>
    public byte[]? AndMask1Bpp { get; init; }

    /// <summary>1bpp XOR mask for <see cref="CursorEncoding.Monochrome1"/>. Same convention as <see cref="AndMask1Bpp"/>.</summary>
    public byte[]? XorMask1Bpp { get; init; }

    public CursorProvenance Provenance { get; init; } = CursorProvenance.Unknown;

    /// <summary>Human-readable note about how this image was obtained (shown in the UI / reports).</summary>
    public string? Note { get; init; }

    public int Width => Image.Width;
    public int Height => Image.Height;

    public CursorImage(ArgbBitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Image = image;
    }

    /// <summary>
    /// Shrinks the cursor to its alpha bounding box and moves the hotspot accordingly.
    /// </summary>
    /// <remarks>
    /// Cursor files are expected to be tight; a mostly-empty 256x256 cursor also makes
    /// the hotspot arithmetic harder to read for a human. Fully transparent images are
    /// returned unchanged.
    /// </remarks>
    public CursorImage Trim(byte alphaThreshold = 0)
    {
        var trimmed = Image.TrimByAlpha(alphaThreshold, out int dx, out int dy);
        if (ReferenceEquals(trimmed, Image))
        {
            return this;
        }

        return new CursorImage(trimmed)
        {
            HotspotX = Math.Clamp(HotspotX - dx, 0, Math.Max(0, trimmed.Width - 1)),
            HotspotY = Math.Clamp(HotspotY - dy, 0, Math.Max(0, trimmed.Height - 1)),
            PreferredEncoding = PreferredEncoding,
            // The auxiliary masks are only meaningful for the untrimmed geometry; if a
            // caller trims an exact-encoding cursor it must re-derive them.
            XorMaskBgra = null,
            AndMask1Bpp = null,
            XorMask1Bpp = null,
            Provenance = Provenance,
            Note = Note,
        };
    }

    /// <summary>Convenience factory for a plain alpha-blended cursor.</summary>
    public static CursorImage FromAlpha(ArgbBitmap image, int hotspotX, int hotspotY, CursorProvenance provenance)
        => new(image) { HotspotX = hotspotX, HotspotY = hotspotY, Provenance = provenance };
}
