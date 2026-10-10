namespace RatTrap.Imaging;

/// <summary>
/// Packing and unpacking of 1bpp cursor masks.
/// </summary>
/// <remarks>
/// <para>
/// Convention used everywhere in this project (see <see cref="CursorImage"/>):
/// <b>top-down</b> rows, bit 7 of the first byte is the leftmost pixel, and every row is
/// padded to a <b>4-byte (DWORD)</b> boundary — the layout the ICO/CUR container requires.
/// The file writers are responsible for flipping to bottom-up.
/// </para>
/// <para>
/// The DXGI pointer-shape buffer for monochrome cursors uses the same bit order but a
/// driver-chosen row pitch, so <see cref="Repack"/> exists to move between the two.
/// </para>
/// </remarks>
public static class BitMask1Bpp
{
    /// <summary>Bytes per row for a 1bpp mask of the given width, DWORD-aligned.</summary>
    public static int RowBytes(int width) => ((width + 31) / 32) * 4;

    /// <summary>Total buffer size for a 1bpp mask of the given size.</summary>
    public static int BufferSize(int width, int height) => RowBytes(width) * height;

    public static bool GetBit(ReadOnlySpan<byte> mask, int rowStride, int x, int y)
    {
        byte b = mask[(y * rowStride) + (x >> 3)];
        return (b & (0x80 >> (x & 7))) != 0;
    }

    public static void SetBit(Span<byte> mask, int rowStride, int x, int y, bool value)
    {
        int index = (y * rowStride) + (x >> 3);
        byte bit = (byte)(0x80 >> (x & 7));
        if (value)
        {
            mask[index] |= bit;
        }
        else
        {
            mask[index] &= (byte)~bit;
        }
    }

    /// <summary>
    /// Copies a 1bpp mask that uses an arbitrary row stride into a DWORD-aligned one, or
    /// vice versa when <paramref name="targetRowStride"/> is the source layout.
    /// </summary>
    public static byte[] Repack(ReadOnlySpan<byte> source, int sourceRowStride, int width, int height, int targetRowStride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceRowStride, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetRowStride, 1);

        var result = new byte[targetRowStride * height];
        int copyBytes = Math.Min(sourceRowStride, targetRowStride);
        for (int y = 0; y < height; y++)
        {
            source.Slice(y * sourceRowStride, copyBytes)
                  .CopyTo(result.AsSpan(y * targetRowStride, copyBytes));
        }
        return result;
    }

    /// <summary>Renders a 1bpp mask as a BGRA image with the given colours for 0 and 1.</summary>
    public static ArgbBitmap ToBitmap(ReadOnlySpan<byte> mask, int rowStride, int width, int height, ArgbPixel whenClear, ArgbPixel whenSet)
    {
        var bmp = new ArgbBitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bmp.SetPixel(x, y, GetBit(mask, rowStride, x, y) ? whenSet : whenClear);
            }
        }
        return bmp;
    }

    /// <summary>Builds a 1bpp mask from a predicate over the image.</summary>
    public static byte[] FromBitmap(ArgbBitmap image, Func<ArgbPixel, bool> isSet)
    {
        int rowStride = RowBytes(image.Width);
        var mask = new byte[rowStride * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (isSet(image.GetPixel(x, y)))
                {
                    SetBit(mask, rowStride, x, y, true);
                }
            }
        }
        return mask;
    }
}
