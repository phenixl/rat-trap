using System.Runtime.CompilerServices;

namespace RatTrap.Imaging;

/// <summary>
/// A single pixel in <b>straight (non-premultiplied) BGRA</b> order.
/// </summary>
/// <remarks>
/// Channel order matches the byte order of a 32bpp Windows DIB
/// (<c>BI_RGB</c>, <c>biBitCount = 32</c>) and of <c>DXGI_FORMAT_B8G8R8A8_UNORM</c>,
/// which is what every capture backend in this project produces. Keeping that order
/// everywhere means the capture path never has to swizzle.
/// </remarks>
public readonly record struct ArgbPixel(byte B, byte G, byte R, byte A)
{
    public static readonly ArgbPixel Transparent = new(0, 0, 0, 0);
    public static readonly ArgbPixel OpaqueBlack = new(0, 0, 0, 255);
    public static readonly ArgbPixel OpaqueWhite = new(255, 255, 255, 255);

    public bool IsTransparent => A == 0;
    public bool IsOpaque => A == 255;
}

/// <summary>
/// A top-down, tightly packed, 4-bytes-per-pixel straight-BGRA bitmap.
/// </summary>
/// <remarks>
/// <para>
/// This is the single image currency of the whole program: capture backends fill it,
/// the pointer decoders fill it, the reconstruction solver fills it, and the PNG /
/// CUR / ANI writers read it. It deliberately has no dependency on
/// <c>System.Drawing</c> (not AOT-safe here) and no per-pixel virtual dispatch.
/// </para>
/// <para>
/// Layout: <c>Pixels[(y * Width + x) * 4 + {0,1,2,3}] = B, G, R, A</c>. Row 0 is the
/// <b>top</b> row. There is no row padding (<see cref="Stride"/> is always
/// <c>Width * 4</c>).
/// </para>
/// </remarks>
public sealed class ArgbBitmap
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Bytes per row. Always <c>Width * 4</c>.</summary>
    public int Stride => Width * 4;

    /// <summary>Raw BGRA bytes, length <c>Width * Height * 4</c>.</summary>
    public byte[] Pixels { get; }

    public ArgbBitmap(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * 4)];
    }

    public int PixelCount => Width * Height;

    public Span<byte> AsSpan() => Pixels.AsSpan();

    /// <summary>Row <paramref name="y"/> as a span of <c>Width * 4</c> bytes.</summary>
    public Span<byte> GetRowSpan(int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return Pixels.AsSpan(y * Stride, Stride);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ArgbPixel GetPixel(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return new ArgbPixel(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPixel(int x, int y, ArgbPixel p)
    {
        int i = (y * Width + x) * 4;
        Pixels[i] = p.B;
        Pixels[i + 1] = p.G;
        Pixels[i + 2] = p.R;
        Pixels[i + 3] = p.A;
    }

    public void Fill(ArgbPixel p)
    {
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            Pixels[i] = p.B;
            Pixels[i + 1] = p.G;
            Pixels[i + 2] = p.R;
            Pixels[i + 3] = p.A;
        }
    }

    public ArgbBitmap Clone()
    {
        var copy = new ArgbBitmap(Width, Height);
        Pixels.AsSpan().CopyTo(copy.Pixels);
        return copy;
    }

    /// <summary>
    /// Copies a source buffer (arbitrary <paramref name="sourceStride"/>, top-down,
    /// straight BGRA) into a new bitmap.
    /// </summary>
    public static ArgbBitmap FromBgra(ReadOnlySpan<byte> source, int width, int height, int sourceStride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (sourceStride < width * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceStride), sourceStride,
                $"Source stride must be at least {width * 4} bytes.");
        }
        if (source.Length < (long)sourceStride * (height - 1) + (width * 4))
        {
            throw new ArgumentException("Source buffer is too small for the requested dimensions.", nameof(source));
        }

        var bmp = new ArgbBitmap(width, height);
        if (sourceStride == bmp.Stride)
        {
            source[..(width * height * 4)].CopyTo(bmp.Pixels);
        }
        else
        {
            for (int y = 0; y < height; y++)
            {
                source.Slice(y * sourceStride, bmp.Stride).CopyTo(bmp.Pixels.AsSpan(y * bmp.Stride, bmp.Stride));
            }
        }
        return bmp;
    }

    /// <summary>
    /// Tight bounding box of all pixels whose alpha is <b>strictly greater</b> than
    /// <paramref name="alphaThreshold"/>. Returns <c>false</c> when no pixel qualifies.
    /// </summary>
    public bool TryGetAlphaBounds(byte alphaThreshold, out int x, out int y, out int width, out int height)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int ry = 0; ry < Height; ry++)
        {
            int rowBase = ry * Stride;
            for (int rx = 0; rx < Width; rx++)
            {
                if (Pixels[rowBase + (rx * 4) + 3] > alphaThreshold)
                {
                    if (rx < minX) minX = rx;
                    if (rx > maxX) maxX = rx;
                    if (ry < minY) minY = ry;
                    if (ry > maxY) maxY = ry;
                }
            }
        }

        if (maxX < 0)
        {
            x = y = width = height = 0;
            return false;
        }

        x = minX;
        y = minY;
        width = maxX - minX + 1;
        height = maxY - minY + 1;
        return true;
    }

    /// <summary>Extracts a sub-rectangle. The rectangle must be fully inside this bitmap.</summary>
    public ArgbBitmap Crop(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > Width || y + height > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x),
                $"Crop rect ({x},{y},{width},{height}) is not inside {Width}x{Height}.");
        }

        var outBmp = new ArgbBitmap(width, height);
        for (int row = 0; row < height; row++)
        {
            Pixels.AsSpan((y + row) * Stride + (x * 4), width * 4)
                  .CopyTo(outBmp.Pixels.AsSpan(row * outBmp.Stride, width * 4));
        }
        return outBmp;
    }

    /// <summary>
    /// Returns a new bitmap shrunk to the alpha bounding box. When the bitmap is fully
    /// transparent the original image is returned unchanged and the offsets are zero.
    /// </summary>
    public ArgbBitmap TrimByAlpha(byte alphaThreshold, out int offsetX, out int offsetY)
    {
        if (!TryGetAlphaBounds(alphaThreshold, out int x, out int y, out int w, out int h))
        {
            offsetX = 0;
            offsetY = 0;
            return this;
        }

        offsetX = x;
        offsetY = y;
        if (x == 0 && y == 0 && w == Width && h == Height)
        {
            return this;
        }
        return Crop(x, y, w, h);
    }

    /// <summary>
    /// Composites this bitmap (as a source-over layer) onto an opaque background and
    /// returns the resulting opaque bitmap. Used to render a flat preview.
    /// </summary>
    public ArgbBitmap CompositeOver(ArgbPixel background)
    {
        var outBmp = new ArgbBitmap(Width, Height);
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            int a = Pixels[i + 3];
            int inv = 255 - a;
            outBmp.Pixels[i] = (byte)((Pixels[i] * a + background.B * inv + 127) / 255);
            outBmp.Pixels[i + 1] = (byte)((Pixels[i + 1] * a + background.G * inv + 127) / 255);
            outBmp.Pixels[i + 2] = (byte)((Pixels[i + 2] * a + background.R * inv + 127) / 255);
            outBmp.Pixels[i + 3] = 255;
        }
        return outBmp;
    }

    /// <summary>
    /// Builds a checkerboard background of <paramref name="cell"/>-pixel squares, so that
    /// transparency in a recovered cursor is visible in previews and documentation.
    /// </summary>
    public static ArgbBitmap CreateCheckerboard(int width, int height, int cell, ArgbPixel light, ArgbPixel dark)
    {
        var bmp = new ArgbBitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool lightSquare = (((x / cell) + (y / cell)) & 1) == 0;
                bmp.SetPixel(x, y, lightSquare ? light : dark);
            }
        }
        return bmp;
    }
}
