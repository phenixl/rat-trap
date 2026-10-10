using System.Buffers.Binary;
using System.IO.Compression;
using RatTrap.Imaging;

namespace RatTrap.Output;

/// <summary>
/// Hand-rolled PNG encoder for <see cref="ArgbBitmap"/>: colour type 6 (truecolour with
/// alpha), 8 bits per channel, no interlace, one IDAT.
/// </summary>
/// <remarks>
/// <para>
/// The build is zero-package and Native AOT, so there is no imaging library to lean on:
/// the container, the CRC-32, and the adaptive row filter all live here. Everything the
/// spec requires is implemented directly rather than approximated.
/// </para>
/// <para>
/// The source bitmap is <b>straight (non-premultiplied) BGRA</b> and PNG wants straight
/// RGBA, so the only per-pixel work is a B/R swap — alpha is copied verbatim.
/// </para>
/// <para>
/// Row filtering uses the canonical libpng heuristic: for every row, all five filters are
/// computed, each byte is read as a <i>signed</i> value, the absolute values are summed,
/// and the cheapest filter wins (ties go to the lower filter number). Cursor bitmaps are
/// mostly flat colour, which means <c>Up</c> usually wins and the file shrinks a lot.
/// </para>
/// </remarks>
internal static class PngWriter
{
    /// <summary>8-byte PNG signature: 0x89 'P' 'N' 'G' CR LF 0x1A LF.</summary>
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Filter distance: 4 bytes per pixel for RGBA8.</summary>
    private const int BytesPerPixel = 4;

    private const byte ColorTypeRgba = 6;
    private const byte BitDepth8 = 8;

    /// <summary>Encodes <paramref name="image"/> as straight-alpha RGBA PNG bytes.</summary>
    public static byte[] Encode(ArgbBitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);

        byte[] filteredScanlines = BuildFilteredScanlines(image);
        byte[] compressed = ZlibCompress(filteredScanlines);

        using var ms = new MemoryStream(compressed.Length + 128);
        ms.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)image.Height);
        ihdr[8] = BitDepth8;      // bit depth: 8 bits per channel
        ihdr[9] = ColorTypeRgba;  // colour type 6: truecolour with alpha
        ihdr[10] = 0;             // compression method: deflate (only legal value)
        ihdr[11] = 0;             // filter method: adaptive (only legal value)
        ihdr[12] = 0;             // interlace: none

        WriteChunk(ms, "IHDR"u8, ihdr);
        WriteChunk(ms, "IDAT"u8, compressed);
        WriteChunk(ms, "IEND"u8, default);
        return ms.ToArray();
    }

    /// <summary>Encodes and writes <paramref name="image"/> to <paramref name="path"/>.</summary>
    public static void WriteFile(string path, ArgbBitmap image)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(image);
        File.WriteAllBytes(path, Encode(image));
    }

    /// <summary>CRC-32/ISO-HDLC (the PNG polynomial), as spec: reflected, init/final 0xFFFFFFFF.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data) => Crc32Update(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;

    /// <summary>
    /// Streaming CRC-32 update so a chunk's CRC can cover the type bytes and the data
    /// bytes without copying them into one contiguous buffer.
    /// </summary>
    public static uint Crc32Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                // Reflected polynomial 0xEDB88320; shift right and xor when the low bit was set.
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return crc;
    }

    /// <summary>
    /// Builds the raw zlib datastream PNG wants in IDAT: a 2-byte zlib header, deflate
    /// data, and an Adler-32 trailer. <see cref="ZLibStream"/> produces exactly that
    /// (a <see cref="DeflateStream"/> would not — it has no zlib wrapper).
    /// </summary>
    private static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream(raw.Length / 2 + 64);
        using (var zlib = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Produces <c>height</c> scanlines, each prefixed by its chosen filter byte, in
    /// BGRA-to-RGBA-swapped order.
    /// </summary>
    private static byte[] BuildFilteredScanlines(ArgbBitmap image)
    {
        int width = image.Width;
        int height = image.Height;
        int stride = checked(width * BytesPerPixel);

        var output = new byte[checked(height * (stride + 1))];
        var previous = new byte[stride];  // reconstructed row above; all zero above row 0
        var current = new byte[stride];   // raw (unfiltered) bytes of the row being filtered

        // One scratch buffer per filter so every candidate can be scored before choosing.
        var candidates = new byte[5][];
        for (int f = 0; f < 5; f++)
        {
            candidates[f] = new byte[stride];
        }

        for (int y = 0; y < height; y++)
        {
            // Filtering is defined on the *unfiltered* bytes, so copy and swap B/R first.
            ConvertBgraRowToRgba(image.GetRowSpan(y), current);

            for (int i = 0; i < stride; i++)
            {
                byte raw = current[i];
                byte left = i >= BytesPerPixel ? current[i - BytesPerPixel] : (byte)0;
                byte up = previous[i];
                byte upLeft = i >= BytesPerPixel ? previous[i - BytesPerPixel] : (byte)0;

                candidates[0][i] = raw;                                                          // None
                candidates[1][i] = (byte)(raw - left);                                            // Sub
                candidates[2][i] = (byte)(raw - up);                                              // Up
                candidates[3][i] = (byte)(raw - (byte)((left + up) >> 1));                        // Average
                candidates[4][i] = (byte)(raw - Paeth(left, up, upLeft));                         // Paeth
            }

            int best = 0;
            long bestCost = long.MaxValue;
            for (int f = 0; f < 5; f++)
            {
                long cost = 0;
                byte[] row = candidates[f];
                for (int i = 0; i < stride; i++)
                {
                    int v = row[i];
                    cost += v < 128 ? v : 256 - v;   // |signed byte|, the libpng metric
                }
                if (cost < bestCost)                    // strict '<' keeps the lowest filter on a tie
                {
                    bestCost = cost;
                    best = f;
                }
            }

            int rowStart = y * (stride + 1);
            output[rowStart] = (byte)best;
            candidates[best].CopyTo(output.AsSpan(rowStart + 1, stride));

            // The row just filtered becomes the "up" row for the next iteration.
            (previous, current) = (current, previous);
        }

        return output;
    }

    /// <summary>BGRA -> RGBA (a channel-order swap only; alpha stays straight).</summary>
    private static void ConvertBgraRowToRgba(ReadOnlySpan<byte> bgra, Span<byte> rgba)
    {
        for (int i = 0; i < rgba.Length; i += BytesPerPixel)
        {
            rgba[i] = bgra[i + 2];              // R
            rgba[i + 1] = bgra[i + 1];          // G
            rgba[i + 2] = bgra[i];              // B
            rgba[i + 3] = bgra[i + 3];          // A
        }
    }

    /// <summary>PNG Paeth predictor (spec 9.4): pick the neighbour closest to a + b - c.</summary>
    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }
        return pb <= pc ? b : c;
    }

    /// <summary>length (big-endian) + type + data + CRC-32(type + data).</summary>
    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> chunkType, ReadOnlySpan<byte> data)
    {
        Span<byte> scratch = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(scratch, (uint)data.Length);
        stream.Write(scratch);
        stream.Write(chunkType);
        stream.Write(data);

        uint crc = Crc32Update(0xFFFFFFFFu, chunkType);
        crc = Crc32Update(crc, data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(scratch, crc);
        stream.Write(scratch);
    }
}
