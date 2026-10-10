using System.Runtime.InteropServices;
using RatTrap.Imaging;
using RatTrap.Interop;

namespace RatTrap.Pointer;

/// <summary>Outcome of reading the live system cursor through the GDI cursor APIs.</summary>
public readonly record struct SystemCursorReadResult(CursorImage? Cursor, string Notes, string? Error);

/// <summary>
/// Reads the live system cursor from <c>GetCursorInfo</c> / <c>GetIconInfo</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the fallback when the compositor's own pointer shape is unavailable (a duplication
/// session that has not yet been told about a shape change, or a machine where duplication is
/// refused entirely). It returns the cursor at its <b>native</b> size - 32x32 on
/// <c>SM_CXCURSOR</c> here - rather than the size actually drawn, which the user can enlarge up to
/// 256px through the accessibility pointer-size setting. So this path gives the right artwork at
/// the wrong scale, and <see cref="Note"/> says so.
/// </para>
/// <para>
/// Its alpha is also not dependable. Measured on the development machine, the colour bitmap's alpha
/// channel came back as <c>nonzero=105/1024, full255=0, max=254</c> - present, but not a clean mask
/// and with no fully opaque pixel at all. So the alpha is only trusted when it looks like a mask,
/// and otherwise it is reconstructed from the AND mask (which is what the OS uses for legacy
/// monochrome blitting anyway).
/// </para>
/// </remarks>
public static class SystemCursorReader
{
    public static SystemCursorReadResult Read()
    {
        var info = CURSORINFO.Create();
        if (!User32.GetCursorInfo(ref info))
        {
            return new SystemCursorReadResult(null, string.Empty,
                $"GetCursorInfo failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        if (info.hCursor == IntPtr.Zero)
        {
            return new SystemCursorReadResult(null, string.Empty,
                "No cursor is currently set (hCursor was null).");
        }

        if ((info.flags & CURSORINFO.CURSOR_SHOWING) == 0)
        {
            return new SystemCursorReadResult(null, string.Empty,
                "The system cursor is hidden (CURSOR_SHOWING is clear), which is exactly the case where " +
                "an application draws its own cursor and the reconstruction path is needed.");
        }

        // CopyIcon first: the handle from GetCursorInfo belongs to the system and must not be
        // destroyed, and it can be changed underneath us at any moment.
        IntPtr owned = User32.CopyIcon(info.hCursor);
        if (owned == IntPtr.Zero)
        {
            owned = info.hCursor; // read-only use; do not destroy
        }
        bool shouldDestroy = owned != info.hCursor;

        try
        {
            return ReadFromIcon(owned);
        }
        finally
        {
            if (shouldDestroy && owned != IntPtr.Zero)
            {
                User32.DestroyIcon(owned);
            }
        }
    }

    private static unsafe SystemCursorReadResult ReadFromIcon(IntPtr hIcon)
    {
        if (!User32.GetIconInfo(hIcon, out ICONINFO icon))
        {
            return new SystemCursorReadResult(null, string.Empty,
                $"GetIconInfo failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        try
        {
            var notes = new List<string>();

            int colorWidth = 0, colorHeight = 0, maskWidth = 0, maskHeight = 0;
            if (icon.hbmColor != IntPtr.Zero)
            {
                Describe(icon.hbmColor, out colorWidth, out colorHeight);
            }
            if (icon.hbmMask != IntPtr.Zero)
            {
                Describe(icon.hbmMask, out maskWidth, out maskHeight);
            }

            if (colorWidth <= 0 || colorHeight <= 0)
            {
                // Monochrome cursor: no colour bitmap at all, just the double-height mask.
                if (maskWidth <= 0 || maskHeight <= 0)
                {
                    return new SystemCursorReadResult(null, string.Empty,
                        "The cursor has neither a colour bitmap nor a usable mask bitmap.");
                }

                int height = maskHeight / 2;
                byte[] mask = ReadMaskBits(icon.hbmMask, maskWidth, maskHeight);
                if (mask.Length == 0)
                {
                    return new SystemCursorReadResult(null, string.Empty, "Reading the cursor mask failed.");
                }

                int stride = BitMask1Bpp.RowBytes(maskWidth);
                var and = new byte[stride * height];
                var xor = new byte[stride * height];
                Array.Copy(mask, 0, and, 0, and.Length);
                Array.Copy(mask, stride * height, xor, 0, xor.Length);

                var image = new ArgbBitmap(maskWidth, height);
                int inverted = 0;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < maskWidth; x++)
                    {
                        bool andBit = BitMask1Bpp.GetBit(and, stride, x, y);
                        bool xorBit = BitMask1Bpp.GetBit(xor, stride, x, y);
                        if (!andBit && !xorBit) image.SetPixel(x, y, ArgbPixel.OpaqueBlack);
                        else if (!andBit && xorBit) image.SetPixel(x, y, ArgbPixel.OpaqueWhite);
                        else if (andBit && !xorBit) { inverted++; image.SetPixel(x, y, new ArgbPixel(0x80, 0x80, 0x80, 255)); }
                        else image.SetPixel(x, y, ArgbPixel.Transparent);
                    }
                }

                notes.Add($"Monochrome cursor: {maskWidth}x{height} from a double-height mask, " +
                          $"{inverted} background-inverting pixels. The .cur output stores the raw 1bpp masks and is exact.");
                return new SystemCursorReadResult(
                    new CursorImage(image)
                    {
                        HotspotX = icon.xHotspot,
                        HotspotY = icon.yHotspot,
                        PreferredEncoding = CursorEncoding.Monochrome1,
                        AndMask1Bpp = and,
                        XorMask1Bpp = xor,
                        Provenance = CursorProvenance.SystemCursorApi,
                        Note = notes[0],
                    },
                    string.Join(" ", notes), null);
            }

            byte[] colorBits = ReadColorBits(icon.hbmColor, colorWidth, colorHeight, out bool isBottomUp);
            if (colorBits.Length == 0)
            {
                return new SystemCursorReadResult(null, string.Empty,
                    "Reading the cursor colour bitmap failed (GetDIBits returned no lines).");
            }

            var bitmap = new ArgbBitmap(colorWidth, colorHeight);
            colorBits.AsSpan().CopyTo(bitmap.Pixels);
            if (isBottomUp)
            {
                FlipVertically(bitmap);
            }

            // Decide whether the alpha channel can be trusted.
            int alphaNonZero = 0, alphaFull = 0;
            for (int i = 3; i < bitmap.Pixels.Length; i += 4)
            {
                byte a = bitmap.Pixels[i];
                if (a != 0) alphaNonZero++;
                if (a == 255) alphaFull++;
            }

            int total = colorWidth * colorHeight;
            bool alphaLooksLikeAMask = alphaFull > 0 && alphaNonZero < total;

            if (!alphaLooksLikeAMask)
            {
                // Derive alpha from the AND mask: set bit means "leave the background alone".
                byte[] mask = maskWidth == colorWidth && maskHeight == colorHeight
                    ? ReadMaskBits(icon.hbmMask, maskWidth, maskHeight)
                    : [];
                if (mask.Length != 0)
                {
                    int stride = BitMask1Bpp.RowBytes(colorWidth);
                    for (int y = 0; y < colorHeight; y++)
                    {
                        for (int x = 0; x < colorWidth; x++)
                        {
                            bool clear = !BitMask1Bpp.GetBit(mask, stride, x, y);
                            bitmap.Pixels[(((y * colorWidth) + x) * 4) + 3] = clear ? (byte)255 : (byte)0;
                        }
                    }
                    notes.Add($"The colour bitmap's alpha channel was not a usable mask " +
                              $"(fully-opaque pixels: {alphaFull}, non-zero alpha: {alphaNonZero} of {total}), " +
                              "so alpha was derived from the AND mask instead.");
                }
                else
                {
                    for (int i = 3; i < bitmap.Pixels.Length; i += 4)
                    {
                        bitmap.Pixels[i] = 255;
                    }
                    notes.Add("The colour bitmap had no usable alpha and no AND mask was available, so the " +
                              "cursor was treated as fully opaque. The compositor path (DXGI pointer shape) " +
                              "does not have this problem.");
                }
            }
            else
            {
                notes.Add($"The colour bitmap's alpha channel looks like a real mask " +
                          $"({alphaFull} fully opaque, {alphaNonZero} non-zero of {total} pixels).");
            }

            notes.Add($"Native size {colorWidth}x{colorHeight}, which is the cursor's artwork size and not " +
                      "necessarily the size it is drawn at; the accessibility pointer-size setting scales it. " +
                      "The compositor path returns the drawn size.");

            return new SystemCursorReadResult(
                new CursorImage(bitmap)
                {
                    HotspotX = icon.xHotspot,
                    HotspotY = icon.yHotspot,
                    PreferredEncoding = CursorEncoding.AlphaBlend32,
                    Provenance = CursorProvenance.SystemCursorApi,
                    Note = string.Join(" ", notes),
                },
                string.Join(" ", notes), null);
        }
        finally
        {
            if (icon.hbmColor != IntPtr.Zero) Gdi32.DeleteObject(icon.hbmColor);
            if (icon.hbmMask != IntPtr.Zero) Gdi32.DeleteObject(icon.hbmMask);
        }
    }

    private static void Describe(IntPtr bitmap, out int width, out int height)
    {
        width = height = 0;
        if (bitmap == IntPtr.Zero)
        {
            return;
        }
        var bm = default(BITMAP);
        if (Gdi32.GetObjectW(bitmap, Marshal.SizeOf<BITMAP>(), ref bm) == 0)
        {
            return;
        }
        width = bm.bmWidth;
        height = bm.bmHeight;
    }

    /// <summary>Reads an arbitrary bitmap into top-down 32bpp BGRA.</summary>
    private static unsafe byte[] ReadColorBits(IntPtr bitmap, int width, int height, out bool isBottomUp)
    {
        isBottomUp = false;
        if (bitmap == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return [];
        }

        IntPtr dc = Gdi32.CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            int byteCount = width * height * 4;
            IntPtr buffer = Marshal.AllocHGlobal(byteCount);
            try
            {
                // A positive biHeight asks for a bottom-up DIB, which is the only form some drivers
                // honour for a DDB source; the caller flips it if this is what came back.
                var info = new BITMAPINFO { bmiHeader = BITMAPINFOHEADER.Create(width, height, 32) };
                int lines = Gdi32.GetDIBits(dc, bitmap, 0, (uint)height, buffer, ref info, Gdi32.DIB_RGB_COLORS);
                isBottomUp = true;
                if (lines == 0)
                {
                    return [];
                }

                var result = new byte[byteCount];
                Marshal.Copy(buffer, result, 0, byteCount);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Gdi32.DeleteDC(dc);
        }
    }

    /// <summary>Reads a 1bpp mask bitmap into top-down, DWORD-aligned rows.</summary>
    private static unsafe byte[] ReadMaskBits(IntPtr bitmap, int width, int height)
    {
        if (bitmap == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return [];
        }

        IntPtr dc = Gdi32.CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            int stride = BitMask1Bpp.RowBytes(width);
            int byteCount = stride * height;
            IntPtr buffer = Marshal.AllocHGlobal(byteCount);
            try
            {
                var info = new BITMAPINFO { bmiHeader = BITMAPINFOHEADER.Create(width, height, 1) };
                int lines = Gdi32.GetDIBits(dc, bitmap, 0, (uint)height, buffer, ref info, Gdi32.DIB_RGB_COLORS);
                if (lines == 0)
                {
                    return [];
                }

                var result = new byte[byteCount];
                Marshal.Copy(buffer, result, 0, byteCount);

                // Bottom-up rows: flip so the caller always sees top-down.
                var flipped = new byte[byteCount];
                for (int y = 0; y < height; y++)
                {
                    Array.Copy(result, y * stride, flipped, (height - 1 - y) * stride, stride);
                }
                return flipped;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Gdi32.DeleteDC(dc);
        }
    }

    private static void FlipVertically(ArgbBitmap bitmap)
    {
        int stride = bitmap.Stride;
        var scratch = new byte[stride];
        for (int y = 0; y < bitmap.Height / 2; y++)
        {
            int top = y * stride;
            int bottom = (bitmap.Height - 1 - y) * stride;
            Array.Copy(bitmap.Pixels, top, scratch, 0, stride);
            Array.Copy(bitmap.Pixels, bottom, bitmap.Pixels, top, stride);
            Array.Copy(scratch, 0, bitmap.Pixels, bottom, stride);
        }
    }
}
