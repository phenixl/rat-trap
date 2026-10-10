using System.Buffers.Binary;
using RatTrap.Imaging;

namespace RatTrap.Output;

/// <summary>
/// Writes Windows cursor (.cur), icon (.ico) and raw cursor-DIB payloads.
/// </summary>
/// <remarks>
/// <para>
/// Container shape: <c>ICONDIR</c> (6 bytes) + one <c>ICONDIRENTRY</c> (16 bytes) + a
/// <c>BITMAPINFOHEADER</c> DIB whose <c>biHeight</c> is <b>twice</b> the image height,
/// because the ICO/CUR DIB stacks the XOR bitmap and the AND mask.
/// </para>
/// <para>
/// Everything in a DIB is <b>bottom-up</b>, while <see cref="ArgbBitmap"/> and the
/// <c>CursorImage</c> 1bpp masks are top-down, so every bitmap write here flips rows.
/// </para>
/// <para>
/// The quirk that makes .cur files different from .ico files: in a <b>.cur</b>
/// <c>ICONDIRENTRY</c> the <c>wPlanes</c> field carries the <b>hotspot X</b> and the
/// <c>wBitCount</c> field carries the <b>hotspot Y</b>. That is verified against
/// <c>C:\Windows\Cursors\aero_link.cur</c>, whose 32x32 entry has 6/0 where its hotspot
/// is (6,0), and against a load/GetIconInfo round-trip in the self-test.
/// </para>
/// <para>
/// A DIB below 8bpp also carries a colour table between the header and the pixel bits; it is
/// written whenever <c>biBitCount &lt; 8</c> (so always for
/// <see cref="CursorEncoding.Monochrome1"/>), with <c>biClrUsed</c> set to its entry count.
/// </para>
/// </remarks>
internal static class CursorFileWriter
{
    private const int IconDirSize = 6;
    private const int IconDirEntrySize = 16;
    private const int BitmapInfoHeaderSize = 40;

    private const ushort TypeIcon = 1;
    private const ushort TypeCursor = 2;

    /// <summary>Directory entries store 256 as 0, so this is the largest legal dimension.</summary>
    private const int MaxDimension = 256;

    // ---------------------------------------------------------------------------------
    // Public surface
    // ---------------------------------------------------------------------------------

    /// <summary>Encodes a .cur (idType = 2) containing one image; the hotspot goes in the directory entry.</summary>
    public static byte[] EncodeCur(CursorImage cursor) => EncodeFile(cursor, TypeCursor);

    /// <summary>Encodes a .ico (idType = 1); the hotspot is stored as 0,0.</summary>
    public static byte[] EncodeIco(CursorImage cursor) => EncodeFile(cursor, TypeIcon);

    public static void WriteCur(string path, CursorImage cursor)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.WriteAllBytes(path, EncodeCur(cursor));
    }

    public static void WriteIco(string path, CursorImage cursor)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.WriteAllBytes(path, EncodeIco(cursor));
    }

    /// <summary>
    /// Raw ICO/CUR image payload (BITMAPINFOHEADER + XOR bitmap + AND mask, no ICONDIR).
    /// </summary>
    /// <remarks>
    /// Used for embedding a frame as a bare DIB. Note that an <c>.ani</c> frame with the
    /// <c>AF_ICON</c> flag must instead carry a <b>whole .cur file</b>
    /// (<see cref="EncodeCur"/>) — that is what <c>aero_busy.ani</c> does and what
    /// <see cref="AniWriter"/> emits.
    /// </remarks>
    public static byte[] EncodeImagePayload(CursorImage cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return BuildImagePayload(cursor, out _);
    }

    /// <summary>Bits per pixel the given cursor would be written with: 32, or 1 for monochrome.</summary>
    internal static int BitCountFor(CursorImage cursor) => cursor.PreferredEncoding switch
    {
        CursorEncoding.AlphaBlend32 => 32,
        CursorEncoding.XorMasked32 => 32,
        CursorEncoding.Monochrome1 => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(cursor), cursor.PreferredEncoding, "Unknown cursor encoding."),
    };

    // ---------------------------------------------------------------------------------
    // Container
    // ---------------------------------------------------------------------------------

    private static byte[] EncodeFile(CursorImage cursor, ushort idType)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        int width = cursor.Width;
        int height = cursor.Height;
        ValidateDimension(width, nameof(cursor));
        ValidateDimension(height, nameof(cursor));

        if (idType == TypeCursor)
        {
            if (cursor.HotspotX < 0 || cursor.HotspotX >= width)
            {
                throw new ArgumentOutOfRangeException(nameof(cursor), cursor.HotspotX,
                    $"Hotspot X must be inside the image (0..{width - 1}).");
            }
            if (cursor.HotspotY < 0 || cursor.HotspotY >= height)
            {
                throw new ArgumentOutOfRangeException(nameof(cursor), cursor.HotspotY,
                    $"Hotspot Y must be inside the image (0..{height - 1}).");
            }
        }

        byte[] payload = BuildImagePayload(cursor, out int bitCount);

        var file = new byte[IconDirSize + IconDirEntrySize + payload.Length];

        // ICONDIR starts at 0: idReserved, idType, idCount.
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), idType);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), 1);

        // ICONDIRENTRY starts at 6: width, height, colour count, reserved, planes, bit count,
        // bytes in resource, image offset.
        int entry = IconDirSize;
        file[entry + 0] = SizeByte(width);
        file[entry + 1] = SizeByte(height);
        file[entry + 2] = ColorCountByte(bitCount);
        file[entry + 3] = 0; // bReserved

        if (idType == TypeCursor)
        {
            // .cur only: the planes/bit-count fields are overloaded as hotspot X/Y.
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(entry + 4), (ushort)cursor.HotspotX);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(entry + 6), (ushort)cursor.HotspotY);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(entry + 4), 1);                    // wPlanes
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(entry + 6), (ushort)bitCount);    // wBitCount
        }

        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry + 8), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry + 12), IconDirSize + IconDirEntrySize);
        payload.CopyTo(file, IconDirSize + IconDirEntrySize);
        return file;
    }

    /// <summary>256 is written as 0; every other dimension is written verbatim.</summary>
    private static byte SizeByte(int dimension) => dimension == MaxDimension ? (byte)0 : (byte)dimension;

    /// <summary>0 for 8bpp and above (alpha or palette implied); the real count below that.</summary>
    private static byte ColorCountByte(int bitCount) => bitCount >= 8 ? (byte)0 : (byte)(1 << bitCount);

    private static void ValidateDimension(int dimension, string paramName)
    {
        if (dimension <= 0 || dimension > MaxDimension)
        {
            throw new ArgumentOutOfRangeException(paramName, dimension,
                $"Cursor dimensions must be 1..{MaxDimension} (the directory entry stores 256 as 0).");
        }
    }

    // ---------------------------------------------------------------------------------
    // DIB payload
    // ---------------------------------------------------------------------------------

    private static byte[] BuildImagePayload(CursorImage cursor, out int bitCount)
    {
        bitCount = BitCountFor(cursor);

        int width = cursor.Width;
        int height = cursor.Height;

        // 1bpp DIB rows are whole DWORDs; that is also the AND mask row size for every depth.
        int maskStride = MaskRowStride(width);
        int xorStride = bitCount == 1 ? maskStride : checked(width * 4);
        int xorBytes = checked(xorStride * height);
        int maskBytes = checked(maskStride * height);

        // A DIB below 8bpp carries a colour table between the header and the pixel bits. It is
        // not optional: without it Windows reads the first palette-sized slab of pixel data as
        // the palette, which silently destroys a 1bpp cursor (verified: hbmColor came back as a
        // flat colour and the AND/XOR planes came back empty).
        int colorTableEntries = bitCount < 8 ? 1 << bitCount : 0;
        int colorTableBytes = colorTableEntries * 4;

        var payload = new byte[checked(BitmapInfoHeaderSize + colorTableBytes + xorBytes + maskBytes)];
        WriteBitmapInfoHeader(payload, width, height, (ushort)bitCount,
            (uint)(colorTableBytes + xorBytes + maskBytes), colorTableEntries);
        WriteColorTable(payload.AsSpan(BitmapInfoHeaderSize, colorTableBytes), colorTableEntries);

        int xorOffset = BitmapInfoHeaderSize + colorTableBytes;
        int maskOffset = xorOffset + xorBytes;

        switch (cursor.PreferredEncoding)
        {
            case CursorEncoding.AlphaBlend32:
                // Straight (non-premultiplied) BGRA is what modern 32bpp cursor/icon DIBs hold.
                Write32bppXorFromImage(payload.AsSpan(xorOffset, xorBytes), cursor.Image, width, height);
                WriteAndMaskFromAlpha(payload.AsSpan(maskOffset, maskBytes), cursor.Image, width, height, maskStride);
                break;

            case CursorEncoding.XorMasked32:
            {
                byte[] xor = cursor.XorMaskBgra
                    ?? throw new InvalidOperationException("CursorEncoding.XorMasked32 requires CursorImage.XorMaskBgra.");
                int expected = checked(width * height * 4);
                if (xor.Length != expected)
                {
                    throw new ArgumentException(
                        $"XorMaskBgra must be exactly {expected} bytes (W*H*4) but was {xor.Length}.", nameof(cursor));
                }

                Write32bppXorFromRaw(payload.AsSpan(xorOffset, xorBytes), xor, width * 4, height);
                // The AND mask stays all zero: "take the XOR bitmap everywhere, never punch a hole".
                break;
            }

            case CursorEncoding.Monochrome1:
            {
                byte[] xor1 = cursor.XorMask1Bpp
                    ?? throw new InvalidOperationException("CursorEncoding.Monochrome1 requires CursorImage.XorMask1Bpp.");
                byte[] and1 = cursor.AndMask1Bpp
                    ?? throw new InvalidOperationException("CursorEncoding.Monochrome1 requires CursorImage.AndMask1Bpp.");
                int expected = checked(maskStride * height);
                if (xor1.Length != expected)
                {
                    throw new ArgumentException(
                        $"XorMask1Bpp must be exactly {expected} bytes (H * ((W+31)/32*4)) but was {xor1.Length}.", nameof(cursor));
                }
                if (and1.Length != expected)
                {
                    throw new ArgumentException(
                        $"AndMask1Bpp must be exactly {expected} bytes (H * ((W+31)/32*4)) but was {and1.Length}.", nameof(cursor));
                }

                FlipTopDownRowsToBottomUp(xor1, payload.AsSpan(xorOffset, xorBytes), maskStride, height);
                FlipTopDownRowsToBottomUp(and1, payload.AsSpan(maskOffset, maskBytes), maskStride, height);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(cursor), cursor.PreferredEncoding, "Unknown cursor encoding.");
        }

        return payload;
    }

    /// <summary>1bpp row size: bits rounded up to whole DWORDs.</summary>
    private static int MaskRowStride(int width) => ((width + 31) / 32) * 4;

    private static void WriteBitmapInfoHeader(
        Span<byte> dst, int width, int height, ushort bitCount, uint bitmapBytes, int colorTableEntries)
    {
        // Offsets are fixed by the 40-byte BITMAPINFOHEADER layout.
        BinaryPrimitives.WriteUInt32LittleEndian(dst, BitmapInfoHeaderSize);                 //  0 biSize
        BinaryPrimitives.WriteInt32LittleEndian(dst[4..], width);                            //  4 biWidth
        //  8 biHeight: an ICO/CUR DIB is XOR bitmap over AND mask, so it is twice as tall.
        //  A positive height also means "bottom-up", which is what the file format wants.
        BinaryPrimitives.WriteInt32LittleEndian(dst[8..], checked(height * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(dst[12..], 1);                              // 12 biPlanes
        BinaryPrimitives.WriteUInt16LittleEndian(dst[14..], bitCount);                       // 14 biBitCount
        BinaryPrimitives.WriteUInt32LittleEndian(dst[16..], 0);                              // 16 biCompression = BI_RGB
        BinaryPrimitives.WriteUInt32LittleEndian(dst[20..], bitmapBytes);                    // 20 biSizeImage
        BinaryPrimitives.WriteInt32LittleEndian(dst[24..], 0);                               // 24 biXPelsPerMeter
        BinaryPrimitives.WriteInt32LittleEndian(dst[28..], 0);                               // 28 biYPelsPerMeter
        // 32 biClrUsed: written explicitly so the reader never has to guess 2^bitCount.
        BinaryPrimitives.WriteUInt32LittleEndian(dst[32..], (uint)colorTableEntries);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[36..], 0);                              // 36 biClrImportant
    }

    /// <summary>
    /// Greyscale RGBQUAD table. For the only sub-8bpp depth this writer produces (1bpp) that is
    /// the conventional black/white pair, with index 1 = white.
    /// </summary>
    private static void WriteColorTable(Span<byte> dst, int entries)
    {
        if (entries == 0)
        {
            return;
        }
        for (int i = 0; i < entries; i++)
        {
            byte level = entries == 1 ? (byte)0 : (byte)((i * 255) / (entries - 1));
            int o = i * 4;                 // RGBQUAD = blue, green, red, reserved
            dst[o] = level;
            dst[o + 1] = level;
            dst[o + 2] = level;
            dst[o + 3] = 0;
        }
    }

    private static void Write32bppXorFromImage(Span<byte> dst, ArgbBitmap image, int width, int height)
    {
        int stride = width * 4;
        for (int row = 0; row < height; row++)
        {
            // DIB row `row` (from the bottom) is image row `height - 1 - row`.
            image.GetRowSpan(height - 1 - row).CopyTo(dst.Slice(row * stride, stride));
        }
    }

    private static void Write32bppXorFromRaw(Span<byte> dst, byte[] source, int sourceStride, int height)
    {
        for (int row = 0; row < height; row++)
        {
            source.AsSpan((height - 1 - row) * sourceStride, sourceStride)
                  .CopyTo(dst.Slice(row * sourceStride, sourceStride));
        }
    }

    /// <summary>
    /// Legacy AND mask derived from alpha: 1 = "leave the destination pixel alone", so a
    /// fully transparent source pixel gets its bit set. Bit 7 of byte 0 is the leftmost
    /// pixel; the padding bits at the end of each row stay 0.
    /// </summary>
    private static void WriteAndMaskFromAlpha(Span<byte> dst, ArgbBitmap image, int width, int height, int maskStride)
    {
        for (int row = 0; row < height; row++)
        {
            Span<byte> maskRow = dst.Slice(row * maskStride, maskStride);
            maskRow.Clear();
            ReadOnlySpan<byte> sourceRow = image.GetRowSpan(height - 1 - row);
            for (int x = 0; x < width; x++)
            {
                if (sourceRow[(x * 4) + 3] == 0)
                {
                    maskRow[x >> 3] |= (byte)(0x80 >> (x & 7));
                }
            }
        }
    }

    /// <summary>Copies top-down 1bpp rows into a bottom-up DIB half, row by row.</summary>
    private static void FlipTopDownRowsToBottomUp(byte[] topDown, Span<byte> dst, int stride, int height)
    {
        for (int row = 0; row < height; row++)
        {
            topDown.AsSpan((height - 1 - row) * stride, stride).CopyTo(dst.Slice(row * stride, stride));
        }
    }
}
