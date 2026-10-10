using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using RatTrap.Imaging;

namespace RatTrap.Output;

/// <summary>
/// End-to-end verification of the output layer. <see cref="Run"/> returns one line per
/// check; a line starting with <c>FAIL</c> is a failure, <c>INFO</c> lines are diagnostics
/// that do not affect the verdict.
/// </summary>
/// <remarks>
/// <para>
/// The PNG side is verified by decoding our own output with an independent minimal reader
/// (zlib inflate plus filter reversal) and comparing every byte. The ICO/CUR/ANI side is
/// verified by writing real files, loading them back through <c>LoadImageW</c> /
/// <c>LoadCursorFromFileW</c> and reading the recovered bitmaps with <c>GetIconInfo</c> +
/// <c>GetDIBits</c>.
/// </para>
/// <para>
/// <b>Host caveat that the checks handle explicitly.</b> On some hosts the cursor loader
/// (both <c>LoadCursorFromFileW</c> and <c>LoadImageW(IMAGE_CURSOR)</c>) does not return the
/// stored bitmap: it re-renders every <c>.cur</c> at the system cursor size, resampling the
/// pixels, and rescales the hotspot with it. That affects Microsoft's own
/// <c>aero_arrow.cur</c> identically, so it is a property of the host, not of a file. The
/// checks therefore:
/// </para>
/// <list type="bullet">
///   <item><description>require byte-exact pixels whenever the loader returns the stored size;</description></item>
///   <item><description>when it rescales, verify the rescaled geometry and the proportionally
///   rescaled hotspot, and report the rescale on an <c>INFO</c> line;</description></item>
///   <item><description>never excuse a pixel mismatch at the stored size unless a first-party
///   Microsoft cursor fails the same comparison through the same API (<see cref="HostLoaderBaseline"/>).</description></item>
/// </list>
/// <para>
/// The byte-exact pixel proof for the DIB encoding therefore rides on the icon path
/// (<c>idType = 1</c>), which this host loads without modification and which uses byte-for-byte
/// the same <c>BITMAPINFOHEADER</c> + XOR + AND payload that <see cref="CursorFileWriter.EncodeImagePayload"/>
/// produces for a cursor.
/// </para>
/// </remarks>
internal static partial class OutputSelfTest
{
    // -----------------------------------------------------------------------------------
    // Entry point
    // -----------------------------------------------------------------------------------

    public static IReadOnlyList<string> Run()
    {
        var lines = new List<string>();
        // %TEMP% is not a safe assumption. Under a confined environment a child process can be refused
        // writes there while the program's own directory is writable, and the whole suite would then
        // fail for a reason that has nothing to do with the code under test. Probe the candidates in
        // order and report which one was used.
        string tempDirectory = ResolveScratchDirectory(lines);

        try
        {
            Directory.CreateDirectory(tempDirectory);

            Check(lines, "png-roundtrip-1x1", () =>
            {
                var image = new ArgbBitmap(1, 1);
                image.SetPixel(0, 0, new ArgbPixel(0x11, 0x22, 0x33, 0x44));
                return PngRoundTrip(image, "1x1");
            });

            Check(lines, "png-roundtrip-32x32-random-alpha", () =>
            {
                ArgbBitmap image = RandomAlphaImage(32, 32, seed: 12345);
                // Force the extremes so opaque and fully transparent pixels are both covered.
                image.SetPixel(0, 0, ArgbPixel.OpaqueBlack);
                image.SetPixel(31, 31, ArgbPixel.Transparent);
                image.SetPixel(7, 19, new ArgbPixel(0xAA, 0xBB, 0xCC, 0x80));
                return PngRoundTrip(image, "32x32 random alpha");
            });

            Check(lines, "png-roundtrip-256x256-gradient", () =>
            {
                ArgbBitmap image = GradientImage(256, 256);
                return PngRoundTrip(image, "256x256 gradient");
            });

            Check(lines, "png-chunk-crc-and-file-order", PngChunkValidation);
            Check(lines, "png-header-fields", PngHeaderValidation);
            Check(lines, "png-adaptive-filter-selection", PngAdaptiveFilterCheck);
            Check(lines, "png-file-write-matches-encode", () => PngWriteFileCheck(tempDirectory));

            Check(lines, "host-cursor-loader-baseline", () => HostLoaderBaselineCheck(lines));

            Check(lines, "ico-alpha32-bytes-exact-in-windows", () => IcoAlpha32Windows(tempDirectory, lines));
            Check(lines, "ico-mono1-planes-byte-exact-in-windows", () => IcoMono1Windows(tempDirectory, lines));
            Check(lines, "ico-structure", IcoStructure);
            Check(lines, "ico-mono1-structure-and-colour-table", IcoMono1Structure);

            Check(lines, "cur-alpha32-dib-layout", CurAlpha32DibLayout);
            Check(lines, "cur-mono1-dib-layout-and-colour-table", CurMono1DibLayout);
            Check(lines, "cur-mask-row-padding-and-bottom-up", CurMaskPaddingCheck);

            Check(lines, "cur-alpha32-loads-in-windows", () => CurAlpha32Windows(tempDirectory, lines));
            Check(lines, "cur-xormasked32-loads-in-windows", () => CurXorMasked32Windows(tempDirectory, lines));
            Check(lines, "cur-xormasked32-rejects-bad-mask", CurXorMasked32Validation);
            Check(lines, "cur-mono1-loads-in-windows", () => CurMono1Windows(tempDirectory, lines));
            Check(lines, "cur-mono1-rejects-missing-masks", CurMono1Validation);
            Check(lines, "cur-dir-entry-256-is-zero", () => CurSizeByteCheck(tempDirectory, lines));
            Check(lines, "cur-odd-sizes-load-in-windows", () => CurOddSizes(tempDirectory, lines));

            Check(lines, "ani-1frame-structure-and-load", () => AniOneFrame(tempDirectory, lines));
            Check(lines, "ani-3frame-structure-and-load", () => AniThreeFrames(tempDirectory, lines));
            Check(lines, "ani-rate-and-seq-chunks", () => AniRateSeq(tempDirectory, lines));
            Check(lines, "ani-frame-payload-variants", () => AniFramePayloadVariant(tempDirectory, lines));
        }
        catch (Exception ex)
        {
            // Run() must never throw: anything that escapes a check still has to be reported.
            lines.Add($"FAIL self-test-harness: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory must not turn into a failed check.
            }
        }

        if (lines.Count == 0)
        {
            lines.Add("FAIL self-test-harness: no checks ran.");
        }
        return lines;
    }

    private static void Check(List<string> lines, string name, Func<string?> body)
    {
        try
        {
            string? detail = body();
            lines.Add(detail is null ? $"PASS {name}" : $"PASS {name}: {detail}");
        }
        catch (Exception ex)
        {
            lines.Add($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    // -----------------------------------------------------------------------------------
    // PNG
    // -----------------------------------------------------------------------------------

    private static string? PngRoundTrip(ArgbBitmap image, string label)
    {
        byte[] png = PngWriter.Encode(image);
        DecodedPng decoded = DecodePng(png);

        Require(decoded.Width == image.Width && decoded.Height == image.Height,
            $"{label}: decoded {decoded.Width}x{decoded.Height}, expected {image.Width}x{image.Height}");
        Require(decoded.Bgra.Length == image.Pixels.Length,
            $"{label}: decoded {decoded.Bgra.Length} bytes, expected {image.Pixels.Length}");

        for (int i = 0; i < image.Pixels.Length; i++)
        {
            if (decoded.Bgra[i] != image.Pixels[i])
            {
                int pixel = i / 4;
                throw new InvalidOperationException(
                    $"{label}: first mismatch at pixel ({pixel % image.Width},{pixel / image.Width}) channel {i % 4}: " +
                    $"got {decoded.Bgra[i]}, expected {image.Pixels[i]} (png {png.Length} bytes)");
            }
        }

        return $"{label} -> {png.Length} bytes, filters [{string.Join(",", decoded.FilterTypes)}]";
    }

    /// <summary>Encodes three images and validates every chunk's CRC against our own CRC-32.</summary>
    private static string? PngChunkValidation()
    {
        ArgbBitmap[] images =
        {
            GradientImage(1, 1),
            RandomAlphaImage(32, 32, seed: 99),
            GradientImage(256, 256),
        };

        int chunksChecked = 0;
        foreach (ArgbBitmap image in images)
        {
            byte[] png = PngWriter.Encode(image);

            for (int i = 0; i < PngSignature.Length; i++)
            {
                Require(png[i] == PngSignature[i], $"signature byte {i} is 0x{png[i]:X2}");
            }

            int position = 8;
            var order = new List<string>();
            while (position < png.Length)
            {
                Require(position + 12 <= png.Length, "truncated chunk header");
                uint length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position));
                Require(length <= int.MaxValue, $"implausible chunk length {length}");
                int dataLength = (int)length;
                Require(position + 12 + dataLength <= png.Length, "truncated chunk payload");

                string type = Encoding.ASCII.GetString(png, position + 4, 4);
                uint stored = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position + 8 + dataLength));
                uint computed = PngWriter.Crc32(png.AsSpan(position + 4, 4 + dataLength));
                Require(stored == computed, $"{type} CRC stored 0x{stored:X8} != computed 0x{computed:X8}");

                order.Add(type);
                chunksChecked++;
                position += 12 + dataLength;
                if (type == "IEND")
                {
                    break;
                }
            }

            Require(position == png.Length, $"trailing bytes after IEND: {png.Length - position}");
            Require(string.Join(",", order) == "IHDR,IDAT,IEND", $"chunk order {string.Join(",", order)}");
        }

        return $"{chunksChecked} chunk CRCs valid";
    }

    private static string? PngHeaderValidation()
    {
        ArgbBitmap image = GradientImage(7, 3);
        byte[] png = PngWriter.Encode(image);

        Require(png.Length > 8 + 25, "png too short to contain IHDR");

        // IHDR payload starts at 8 (signature) + 4 (length) + 4 (type) = 16.
        ReadOnlySpan<byte> ihdr = png.AsSpan(16, 13);
        Require(BinaryPrimitives.ReadUInt32BigEndian(ihdr) == 7, "IHDR width");
        Require(BinaryPrimitives.ReadUInt32BigEndian(ihdr[4..]) == 3, "IHDR height");
        Require(ihdr[8] == 8, $"IHDR bit depth {ihdr[8]}");
        Require(ihdr[9] == 6, $"IHDR colour type {ihdr[9]} (expected 6 = RGBA)");
        Require(ihdr[10] == 0, $"IHDR compression {ihdr[10]}");
        Require(ihdr[11] == 0, $"IHDR filter method {ihdr[11]}");
        Require(ihdr[12] == 0, $"IHDR interlace {ihdr[12]}");

        // IDAT payload must be a real zlib stream, not a bare deflate stream.
        const int idatLengthOffset = 8 + 25;
        uint idatLength = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(idatLengthOffset));
        Require(Encoding.ASCII.GetString(png, idatLengthOffset + 4, 4) == "IDAT", "second chunk is not IDAT");
        int idatData = idatLengthOffset + 8;
        byte cmf = png[idatData];
        byte flg = png[idatData + 1];
        Require((cmf & 0x0F) == 8, $"zlib CM {cmf & 0x0F} != 8 (deflate)");
        Require(((cmf << 8) | flg) % 31 == 0, $"zlib header 0x{cmf:X2}{flg:X2} fails the mod-31 check");

        return $"IHDR 7x3 RGBA8, IDAT {idatLength} bytes, zlib header 0x{cmf:X2}{flg:X2}";
    }

    /// <summary>
    /// Proves the filter chooser is live rather than always writing None, by reading the
    /// chosen filter byte back out of the inflated IDAT stream.
    /// </summary>
    /// <remarks>
    /// The metric is the libpng one (bytes read as signed, absolute values summed), so the
    /// expected winner is computable by hand:
    /// <list type="bullet">
    ///   <item><description>Flat image (every pixel identical): row 0 has zeros above it, so
    ///   Up costs the same as None but Sub only pays for the first pixel; later rows cost 0
    ///   under Up, which wins outright.</description></item>
    ///   <item><description>Stripe image (one channel ramps horizontally, another vertically):
    ///   Sub's per-pixel delta beats Up's.</description></item>
    /// </list>
    /// </remarks>
    private static string? PngAdaptiveFilterCheck()
    {
        var flat = new ArgbBitmap(16, 8);
        flat.Fill(new ArgbPixel(0x20, 0x80, 0xE0, 0xFF));
        DecodedPng flatDecoded = DecodePng(PngWriter.Encode(flat));

        Require(flatDecoded.FilterTypes[0] == 1,
            $"flat row 0 used filter {flatDecoded.FilterTypes[0]}, expected 1 (Sub)");
        for (int y = 1; y < flat.Height; y++)
        {
            Require(flatDecoded.FilterTypes[y] == 2,
                $"flat row {y} used filter {flatDecoded.FilterTypes[y]}, expected 2 (Up, cost 0)");
        }

        var stripe = new ArgbBitmap(64, 4);
        for (int y = 0; y < stripe.Height; y++)
        {
            for (int x = 0; x < stripe.Width; x++)
            {
                // B ramps with x, R ramps with y, G/A are constant.
                stripe.SetPixel(x, y, new ArgbPixel((byte)(x * 8), 0x40, (byte)(y * 64), 0xFF));
            }
        }

        // Row 0 has zeros above it, so Sub and Paeth both cost one pixel's worth and the
        // lower index wins. For later rows Paeth predicts B from the identical row above and
        // R from the identical pixel to the left, so it costs ~0 against Sub's 8/row and Up's 64/row.
        DecodedPng stripeDecoded = DecodePng(PngWriter.Encode(stripe));
        Require(stripeDecoded.FilterTypes[0] == 1,
            $"stripe row 0 used filter {stripeDecoded.FilterTypes[0]}, expected 1 (Sub)");
        for (int y = 1; y < stripe.Height; y++)
        {
            Require(stripeDecoded.FilterTypes[y] == 4,
                $"stripe row {y} used filter {stripeDecoded.FilterTypes[y]}, expected 4 (Paeth)");
        }

        return $"flat=[{string.Join(",", flatDecoded.FilterTypes)}] stripe=[{string.Join(",", stripeDecoded.FilterTypes)}]";
    }

    private static string? PngWriteFileCheck(string tempDirectory)
    {
        ArgbBitmap image = RandomAlphaImage(23, 11, seed: 7);
        string path = Path.Combine(tempDirectory, "writefile.png");
        PngWriter.WriteFile(path, image);

        byte[] fromDisk = File.ReadAllBytes(path);
        byte[] inMemory = PngWriter.Encode(image);
        Require(fromDisk.Length == inMemory.Length, $"file {fromDisk.Length} bytes vs in-memory {inMemory.Length}");
        Require(fromDisk.AsSpan().SequenceEqual(inMemory), "file bytes differ from Encode() output");
        return $"{fromDisk.Length} bytes";
    }

    private sealed class DecodedPng
    {
        public int Width;
        public int Height;
        public byte[] Bgra = Array.Empty<byte>();
        public byte[] FilterTypes = Array.Empty<byte>();
    }

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Minimal PNG reader: validates every chunk CRC, inflates IDAT, reverses the filters.</summary>
    private static DecodedPng DecodePng(byte[] png)
    {
        Require(png.Length > 8, "png shorter than the signature");
        for (int i = 0; i < PngSignature.Length; i++)
        {
            Require(png[i] == PngSignature[i], $"signature byte {i} is 0x{png[i]:X2}");
        }

        int width = 0;
        int height = 0;
        bool sawIhdr = false;
        bool sawIend = false;
        var idat = new MemoryStream();
        int position = 8;

        while (position < png.Length)
        {
            Require(position + 12 <= png.Length, "truncated chunk header");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position));
            Require(length <= int.MaxValue, $"implausible chunk length {length}");
            int dataLength = (int)length;
            Require(position + 12 + dataLength <= png.Length, "truncated chunk payload");

            string type = Encoding.ASCII.GetString(png, position + 4, 4);
            ReadOnlySpan<byte> data = png.AsSpan(position + 8, dataLength);
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position + 8 + dataLength));
            uint computed = PngWriter.Crc32(png.AsSpan(position + 4, 4 + dataLength));
            Require(stored == computed, $"{type} CRC mismatch (stored 0x{stored:X8}, computed 0x{computed:X8})");

            switch (type)
            {
                case "IHDR":
                    Require(dataLength == 13, $"IHDR length {dataLength}");
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    Require(width > 0 && height > 0, $"IHDR {width}x{height}");
                    Require(data[8] == 8, $"bit depth {data[8]}");
                    Require(data[9] == 6, $"colour type {data[9]}");
                    Require(data[10] == 0 && data[11] == 0 && data[12] == 0, "compression/filter/interlace not 0");
                    sawIhdr = true;
                    break;

                case "IDAT":
                    idat.Write(data);
                    break;

                case "IEND":
                    Require(dataLength == 0, "IEND is not empty");
                    sawIend = true;
                    break;

                default:
                    throw new InvalidOperationException($"unexpected chunk '{type}'");
            }

            position += 12 + dataLength;
            if (sawIend)
            {
                break;
            }
        }

        Require(sawIhdr, "no IHDR");
        Require(sawIend, "no IEND");
        Require(position == png.Length, $"trailing bytes after IEND: {png.Length - position}");

        byte[] filtered;
        using (var source = new MemoryStream(idat.ToArray()))
        using (var zlib = new ZLibStream(source, CompressionMode.Decompress))
        using (var sink = new MemoryStream())
        {
            zlib.CopyTo(sink);
            filtered = sink.ToArray();
        }

        const int bytesPerPixel = 4;
        int stride = checked(width * bytesPerPixel);
        int expectedLength = checked(height * (stride + 1));
        Require(filtered.Length == expectedLength,
            $"inflated {filtered.Length} bytes, expected {expectedLength} (h*(stride+1))");

        var raw = new byte[checked(height * stride)];
        var filterTypes = new byte[height];

        for (int y = 0; y < height; y++)
        {
            int filter = filtered[y * (stride + 1)];
            filterTypes[y] = (byte)filter;
            ReadOnlySpan<byte> source = filtered.AsSpan(y * (stride + 1) + 1, stride);
            Span<byte> destination = raw.AsSpan(y * stride, stride);
            ReadOnlySpan<byte> above = y > 0 ? raw.AsSpan((y - 1) * stride, stride) : default;

            for (int i = 0; i < stride; i++)
            {
                byte left = i >= bytesPerPixel ? destination[i - bytesPerPixel] : (byte)0;
                byte up = y > 0 ? above[i] : (byte)0;
                byte upLeft = y > 0 && i >= bytesPerPixel ? above[i - bytesPerPixel] : (byte)0;
                byte value = source[i];
                destination[i] = filter switch
                {
                    0 => value,
                    1 => (byte)(value + left),
                    2 => (byte)(value + up),
                    3 => (byte)(value + (byte)((left + up) >> 1)),
                    4 => (byte)(value + Paeth(left, up, upLeft)),
                    _ => throw new InvalidOperationException($"unknown filter type {filter} on row {y}"),
                };
            }
        }

        // PNG is RGBA; the project currency is BGRA.
        var bgra = new byte[raw.Length];
        for (int i = 0; i < raw.Length; i += 4)
        {
            bgra[i] = raw[i + 2];
            bgra[i + 1] = raw[i + 1];
            bgra[i + 2] = raw[i];
            bgra[i + 3] = raw[i + 3];
        }

        return new DecodedPng { Width = width, Height = height, Bgra = bgra, FilterTypes = filterTypes };
    }

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

    // -----------------------------------------------------------------------------------
    // Test images
    // -----------------------------------------------------------------------------------

    private static ArgbBitmap GradientImage(int width, int height)
    {
        var image = new ArgbBitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte a = (byte)((x + y) & 0xFF);
                image.SetPixel(x, y, new ArgbPixel((byte)(x & 0xFF), (byte)(y & 0xFF), (byte)((x * 3 + y * 5) & 0xFF), a));
            }
        }
        return image;
    }

    private static ArgbBitmap RandomAlphaImage(int width, int height, int seed)
    {
        var random = new Random(seed);
        var image = new ArgbBitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image.SetPixel(x, y, new ArgbPixel(
                    (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            }
        }
        return image;
    }

    /// <summary>Opaque core, a semi-transparent ring, and fully transparent corners.</summary>
    private static ArgbBitmap SemiTransparentEdgeImage(int size)
    {
        var image = new ArgbBitmap(size, size);
        double centre = (size - 1) / 2.0;
        double radius = size / 2.0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double distance = Math.Sqrt(((x - centre) * (x - centre)) + ((y - centre) * (y - centre)));
                ArgbPixel pixel = distance <= radius - 2
                    ? new ArgbPixel(0x10, 0x90, 0xF0, 0xFF)
                    : distance <= radius
                        ? new ArgbPixel(0xE0, 0x40, 0x20, 0x80)
                        : new ArgbPixel(0x00, 0x00, 0x00, 0x00);
                image.SetPixel(x, y, pixel);
            }
        }
        return image;
    }

    private static ArgbBitmap OpaquePattern(int width, int height)
    {
        var image = new ArgbBitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image.SetPixel(x, y, new ArgbPixel((byte)(x * 17), (byte)(y * 31), (byte)((x ^ y) * 7), 0xFF));
            }
        }
        return image;
    }

    /// <summary>
    /// 1bpp AND plane with a diagonal ladder in the left half and a triangular XOR plane in the
    /// right half, so a swapped plane or a wrong row order cannot pass unnoticed.
    /// </summary>
    private static (byte[] And, byte[] Xor) MonoMasks(int width, int height)
    {
        int stride = MaskRowStride(width);
        var and = new byte[stride * height];
        var xor = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (x < width / 2 && ((x + y) & 3) == 0)
                {
                    and[(y * stride) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
                if (x >= width / 2 && ((x + y) & 1) == 0)
                {
                    xor[(y * stride) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
        }
        return (and, xor);
    }

    private static int MaskRowStride(int width) => ((width + 31) / 32) * 4;

    // -----------------------------------------------------------------------------------
    // ICO: the byte-exact pixel proof
    // -----------------------------------------------------------------------------------

    private static string? IcoAlpha32Windows(string tempDirectory, List<string> lines)
    {
        const int size = 32;
        ArgbBitmap image = SemiTransparentEdgeImage(size);
        var cursor = new CursorImage(image) { HotspotX = 0, HotspotY = 0 };

        string path = Path.Combine(tempDirectory, "pixels32.ico");
        CursorFileWriter.WriteIco(path, cursor);

        IntPtr icon = Native.LoadImage(IntPtr.Zero, path, ImageIcon, 0, 0, LrLoadFromFile);
        Require(icon != IntPtr.Zero, $"LoadImageW(IMAGE_ICON) failed, win32 error {Marshal.GetLastWin32Error()}");
        try
        {
            CursorProbe probe = Probe(icon, "LoadImageW(IMAGE_ICON)");
            Require(probe.IsIcon != 0, "GetIconInfo reported fIcon == 0 for an .ico");
            Require(probe.HasColor && probe.ColorBgra is not null, ".ico produced no 32bpp colour bitmap");
            Require(probe.ColorWidth == size && probe.ColorHeight == size,
                $".ico loaded as {probe.ColorWidth}x{probe.ColorHeight}, expected {size}x{size}");
            Require(CompareBgra(image, probe.ColorBgra!, size, out string detail), $"pixels: {detail}");

            // The AND mask must still report "transparent where A == 0" after the round trip.
            // Both vertical orientations are accepted (GDI's DDB row order is a convention, not
            // something the file format pins down); the one that matched is reported.
            byte[] expectedAnd = BuildAndMaskFromAlpha(image);
            if (probe.Mask1Bpp is not null && probe.MaskWidth == size && probe.MaskHeight == size
                && probe.Mask1Bpp.Length == expectedAnd.Length)
            {
                bool asWritten = probe.Mask1Bpp.AsSpan().SequenceEqual(expectedAnd);
                var flippedAnd = new byte[expectedAnd.Length];
                for (int y = 0; y < size; y++)
                {
                    Array.Copy(expectedAnd, y * (size / 8), flippedAnd, (size - 1 - y) * (size / 8), size / 8);
                }
                bool flipped = probe.Mask1Bpp.AsSpan().SequenceEqual(flippedAnd);
                Require(asWritten || flipped,
                    "the AND mask Windows reports matches neither vertical orientation of the alpha-derived mask that was written");
                lines.Add($"INFO ico-alpha32-andmask: hbmMask came back as {probe.MaskWidth}x{probe.MaskHeight} 1bpp, " +
                          (asWritten ? "same orientation as written" : "vertically mirrored relative to the file"));
            }
        }
        finally
        {
            Native.DestroyIcon(icon);
        }

        return $"32x32 {image.PixelCount} pixels and the alpha-derived AND mask came back byte-identical";
    }

    private static string? IcoMono1Windows(string tempDirectory, List<string> lines)
    {
        const int size = 32;
        (byte[] and, byte[] xor) = MonoMasks(size, size);
        string? failure = MonoIcoPlanesExact(tempDirectory, and, xor, size, size, lines);
        Require(failure is null, failure ?? string.Empty);
        return "32x32 1bpp: hbmColor NULL and both mask planes byte-identical";
    }

    /// <summary>
    /// Loads the given 1bpp planes through the icon path and requires Windows to hand back
    /// both planes byte for byte. This is the authoritative check for
    /// <see cref="CursorEncoding.Monochrome1"/>: the icon path preserves DIB bytes on this
    /// host, while the cursor path does not.
    /// </summary>
    private static string? MonoIcoPlanesExact(
        string tempDirectory, byte[] and, byte[] xor, int width, int height, List<string> lines)
    {
        var cursor = new CursorImage(OpaquePattern(width, height))
        {
            HotspotX = 0,
            HotspotY = 0,
            PreferredEncoding = CursorEncoding.Monochrome1,
            AndMask1Bpp = and,
            XorMask1Bpp = xor,
        };

        string path = Path.Combine(tempDirectory, $"mono-{width}x{height}-{and.Length}.ico");
        CursorFileWriter.WriteIco(path, cursor);

        IntPtr icon = Native.LoadImage(IntPtr.Zero, path, ImageIcon, 0, 0, LrLoadFromFile);
        if (icon == IntPtr.Zero)
        {
            return $"LoadImageW(IMAGE_ICON) failed, win32 error {Marshal.GetLastWin32Error()}";
        }
        try
        {
            CursorProbe probe = Probe(icon, "LoadImageW(IMAGE_ICON)");
            return CheckMonoProbe(probe, and, xor, width, height, lines, "ico-mono1");
        }
        finally
        {
            Native.DestroyIcon(icon);
        }
    }

    private static string? IcoStructure()
    {
        var cursor = new CursorImage(SemiTransparentEdgeImage(24)) { HotspotX = 11, HotspotY = 4 };
        byte[] ico = CursorFileWriter.EncodeIco(cursor);

        Require(BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(0)) == 0, "idReserved");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)) == 1, "idType must be 1 for .ico");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4)) == 1, "idCount");
        Require(ico[6] == 24 && ico[7] == 24, $"entry size {ico[6]}x{ico[7]}");
        Require(ico[8] == 0, "bColorCount must be 0 at 32bpp");
        Require(ico[9] == 0, "bReserved");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(10)) == 1, "ico wPlanes must be 1, not a hotspot");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(12)) == 32, "ico wBitCount must be 32, not a hotspot");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(14)) == (uint)(ico.Length - 22), "dwBytesInRes");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(18)) == 22, "dwImageOffset");
        Require(BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(26)) == 24, "biWidth at payload start");
        Require(BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(30)) == 48, "biHeight must be 2*height");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(54)) == 0, "32bpp DIB must not declare a colour table");

        return "idType 1, hotspot stored as 0,0, planes/bitcount not overloaded";
    }

    private static string? IcoMono1Structure()
    {
        const int size = 8;
        (byte[] and, byte[] xor) = MonoMasks(size, size);
        var cursor = new CursorImage(OpaquePattern(size, size))
        {
            PreferredEncoding = CursorEncoding.Monochrome1,
            AndMask1Bpp = and,
            XorMask1Bpp = xor,
        };
        byte[] ico = CursorFileWriter.EncodeIco(cursor);

        int stride = MaskRowStride(size);                 // 4 for width 8
        int tableBytes = 2 * 4;                           // 1bpp => two RGBQUADs
        int expectedLength = 22 + 40 + tableBytes + (stride * size * 2);

        Require(ico.Length == expectedLength, $"payload {ico.Length} != {expectedLength}");
        Require(ico[8] == 2, $"bColorCount {ico[8]} should be 2 at 1bpp");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(12)) == 1, "wBitCount must be 1");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(22 + 14)) == 1, "biBitCount must be 1");
        Require(BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(22 + 8)) == size * 2, "biHeight must be 2*height");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(22 + 20)) == (uint)(tableBytes + (stride * size * 2)),
            "biSizeImage must cover the colour table plus both bitmaps");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(22 + 32)) == 2, "biClrUsed must be 2");

        // Colour table: index 0 black, index 1 white.
        int table = 22 + 40;
        Require(ico[table] == 0 && ico[table + 1] == 0 && ico[table + 2] == 0, "palette entry 0 must be black");
        Require(ico[table + 4] == 255 && ico[table + 5] == 255 && ico[table + 6] == 255, "palette entry 1 must be white");

        // XOR plane first (bottom-up), then the AND plane (bottom-up).
        int xorOffset = table + tableBytes;
        int andOffset = xorOffset + (stride * size);
        for (int r = 0; r < size; r++)
        {
            Require(ico.AsSpan(xorOffset + (r * stride), stride).SequenceEqual(xor.AsSpan((size - 1 - r) * stride, stride)),
                $"1bpp XOR file row {r} is not image row {size - 1 - r}");
            Require(ico.AsSpan(andOffset + (r * stride), stride).SequenceEqual(and.AsSpan((size - 1 - r) * stride, stride)),
                $"1bpp AND file row {r} is not image row {size - 1 - r}");
        }

        return $"8x8 1bpp: colour table + {stride * size} bytes XOR + {stride * size} bytes AND, rows bottom-up";
    }

    // -----------------------------------------------------------------------------------
    // Independent re-parse of the produced DIB payloads
    // -----------------------------------------------------------------------------------

    private static string? CurAlpha32DibLayout()
    {
        const int width = 13;
        const int height = 7;
        ArgbBitmap image = SemiTransparentEdgeImage(32).Crop(0, 0, width, height);
        var cursor = new CursorImage(image) { HotspotX = 1, HotspotY = 2 };
        byte[] payload = CursorFileWriter.EncodeImagePayload(cursor);

        int maskStride = MaskRowStride(width);       // 4 for width 13
        int xorStride = width * 4;                   // 52
        int xorBytes = xorStride * height;
        int maskBytes = maskStride * height;

        Require(payload.Length == 40 + xorBytes + maskBytes, $"payload {payload.Length} != {40 + xorBytes + maskBytes}");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0)) == 40, "biSize");
        Require(BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4)) == width, "biWidth");
        Require(BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8)) == height * 2, "biHeight must be 2*height");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(12)) == 1, "biPlanes");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(14)) == 32, "biBitCount");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(16)) == 0, "biCompression must be BI_RGB");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(20)) == xorBytes + maskBytes, "biSizeImage");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(32)) == 0, "32bpp must not declare a colour table");

        // XOR: file row r is image row (height-1-r), copied straight (no premultiply).
        for (int r = 0; r < height; r++)
        {
            ReadOnlySpan<byte> expected = image.GetRowSpan(height - 1 - r);
            ReadOnlySpan<byte> actual = payload.AsSpan(40 + (r * xorStride), xorStride);
            Require(expected.SequenceEqual(actual), $"XOR row {r} is not image row {height - 1 - r} verbatim");
        }

        // AND: rebuilt independently here, bottom-up, 1 wherever alpha is 0.
        int maskOffset = 40 + xorBytes;
        for (int r = 0; r < height; r++)
        {
            ReadOnlySpan<byte> sourceRow = image.GetRowSpan(height - 1 - r);
            for (int x = 0; x < width; x++)
            {
                bool expectedBit = sourceRow[(x * 4) + 3] == 0;
                bool actualBit = (payload[maskOffset + (r * maskStride) + (x >> 3)] & (0x80 >> (x & 7))) != 0;
                Require(expectedBit == actualBit, $"AND bit mismatch at file row {r}, x {x}");
            }
            for (int x = width; x < maskStride * 8; x++)
            {
                bool actualBit = (payload[maskOffset + (r * maskStride) + (x >> 3)] & (0x80 >> (x & 7))) != 0;
                Require(!actualBit, $"AND padding bit at file row {r}, x {x} is set");
            }
        }

        return $"13x7 payload {payload.Length} bytes (xor {xorBytes} + and {maskBytes}), rows bottom-up";
    }

    private static string? CurMono1DibLayout()
    {
        const int width = 13;
        const int height = 7;
        (byte[] and, byte[] xor) = MonoMasks(width, height);
        var cursor = new CursorImage(OpaquePattern(width, height))
        {
            PreferredEncoding = CursorEncoding.Monochrome1,
            AndMask1Bpp = and,
            XorMask1Bpp = xor,
        };
        byte[] payload = CursorFileWriter.EncodeImagePayload(cursor);

        int stride = MaskRowStride(width);       // 4 for width 13
        int tableBytes = 8;
        Require(payload.Length == 40 + tableBytes + (stride * height * 2),
            $"payload {payload.Length} != {40 + tableBytes + (stride * height * 2)}");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(14)) == 1, "biBitCount must be 1");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(32)) == 2, "biClrUsed must be 2 for 1bpp");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(20)) == tableBytes + (stride * height * 2),
            "biSizeImage must cover colour table + XOR + AND");

        int xorOffset = 40 + tableBytes;
        int andOffset = xorOffset + (stride * height);
        for (int r = 0; r < height; r++)
        {
            Require(payload.AsSpan(xorOffset + (r * stride), stride).SequenceEqual(xor.AsSpan((height - 1 - r) * stride, stride)),
                $"1bpp XOR file row {r} is not image row {height - 1 - r}");
            Require(payload.AsSpan(andOffset + (r * stride), stride).SequenceEqual(and.AsSpan((height - 1 - r) * stride, stride)),
                $"1bpp AND file row {r} is not image row {height - 1 - r}");
        }

        return $"13x7 1bpp payload {payload.Length} bytes: colour table + XOR + AND, rows bottom-up";
    }

    /// <summary>
    /// Checks the odd-width AND/XOR row padding directly in the file, including a width (33)
    /// that needs a second DWORD, and re-derives the bottom-up ordering by hand.
    /// </summary>
    private static string? CurMaskPaddingCheck()
    {
        const int width = 33;
        const int height = 6;
        int stride = MaskRowStride(width);   // 8: two DWORDs, 31 padding bits per row

        var image = new ArgbBitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool transparent = (x % 5) == 0 || y == 0;
                image.SetPixel(x, y, new ArgbPixel((byte)x, (byte)y, 0x11, transparent ? (byte)0 : (byte)0xFF));
            }
        }

        var cursor = new CursorImage(image) { HotspotX = 32, HotspotY = 5 };
        byte[] payload = CursorFileWriter.EncodeImagePayload(cursor);
        Require(payload.Length == 40 + (width * 4 * height) + (stride * height),
            $"payload {payload.Length} != {40 + (width * 4 * height) + (stride * height)}");

        int maskOffset = 40 + (width * 4 * height);
        for (int r = 0; r < height; r++)
        {
            int imageRow = height - 1 - r;
            for (int x = 0; x < width; x++)
            {
                bool want = image.GetPixel(x, imageRow).A == 0;
                bool got = (payload[maskOffset + (r * stride) + (x >> 3)] & (0x80 >> (x & 7))) != 0;
                Require(want == got, $"AND bit wrong at file row {r} (image row {imageRow}), x {x}");
            }
            // 33 pixels set 33 bits; the remaining 31 bits of the two DWORDs must be 0.
            for (int x = width; x < stride * 8; x++)
            {
                Require((payload[maskOffset + (r * stride) + (x >> 3)] & (0x80 >> (x & 7))) == 0,
                    $"AND padding bit set at file row {r}, x {x}");
            }
        }

        for (int r = 0; r < height; r++)
        {
            Require(image.GetRowSpan(height - 1 - r).SequenceEqual(payload.AsSpan(40 + (r * width * 4), width * 4)),
                $"XOR row {r} is not image row {height - 1 - r}");
        }

        return $"33x6 mask stride {stride}, padding bits zero, rows bottom-up";
    }

    private static byte[] BuildAndMaskFromAlpha(ArgbBitmap image)
    {
        int width = image.Width;
        int height = image.Height;
        int stride = MaskRowStride(width);
        var mask = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (image.GetPixel(x, y).A == 0)
                {
                    mask[(y * stride) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
        }
        return mask;
    }

    // -----------------------------------------------------------------------------------
    // CUR verification
    // -----------------------------------------------------------------------------------

    private static string? CurAlpha32Windows(string tempDirectory, List<string> lines)
    {
        const int size = 32;
        ArgbBitmap image = SemiTransparentEdgeImage(size);
        var cursor = new CursorImage(image)
        {
            HotspotX = 5,
            HotspotY = 9,
            PreferredEncoding = CursorEncoding.AlphaBlend32,
        };

        string path = Path.Combine(tempDirectory, "alpha32.cur");
        CursorFileWriter.WriteCur(path, cursor);

        string? outcome = VerifyColorCursor(path, cursor, lines, "cur-alpha32");
        Require(outcome is null, outcome ?? string.Empty);
        return "32x32 AlphaBlend32, hotspot (5,9)";
    }

    private static string? CurXorMasked32Windows(string tempDirectory, List<string> lines)
    {
        const int size = 32;
        ArgbBitmap image = OpaquePattern(size, size);
        byte[] xorMask = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = ((y * size) + x) * 4;
                xorMask[i] = (byte)(x * 8);         // B
                xorMask[i + 1] = 0x40;              // G
                xorMask[i + 2] = (byte)(y * 8);     // R
                xorMask[i + 3] = 0xFF;              // A
            }
        }

        var cursor = new CursorImage(image)
        {
            HotspotX = 16,
            HotspotY = 16,
            PreferredEncoding = CursorEncoding.XorMasked32,
            XorMaskBgra = xorMask,
        };

        string path = Path.Combine(tempDirectory, "xormasked32.cur");
        CursorFileWriter.WriteCur(path, cursor);

        // The expectation is the XOR mask, not Image: XorMasked32 stores the mask itself.
        var expected = new ArgbBitmap(size, size);
        xorMask.CopyTo(expected.Pixels, 0);

        string? outcome = VerifyColorCursor(path, cursor, lines, "cur-xormasked32", expected);
        Require(outcome is null, outcome ?? string.Empty);

        // The AND mask must be all zero, and the XOR plane must hold XorMaskBgra verbatim.
        byte[] payload = CursorFileWriter.EncodeImagePayload(cursor);
        for (int r = 0; r < size; r++)
        {
            Require(xorMask.AsSpan((size - 1 - r) * size * 4, size * 4)
                    .SequenceEqual(payload.AsSpan(40 + (r * size * 4), size * 4)),
                $"XorMasked32 XOR file row {r} is not mask row {size - 1 - r} verbatim");
        }
        for (int i = 40 + (size * size * 4); i < payload.Length; i++)
        {
            Require(payload[i] == 0, $"XorMasked32 AND mask byte {i} is 0x{payload[i]:X2}, expected 0");
        }

        return "32x32 XorMasked32, hotspot (16,16), AND mask all zero";
    }

    private static string? CurXorMasked32Validation()
    {
        ArgbBitmap image = OpaquePattern(8, 8);

        AssertThrows<InvalidOperationException>(
            () => CursorFileWriter.EncodeCur(new CursorImage(image) { PreferredEncoding = CursorEncoding.XorMasked32 }),
            "missing XorMaskBgra");

        AssertThrows<ArgumentException>(
            () => CursorFileWriter.EncodeCur(new CursorImage(image)
            {
                PreferredEncoding = CursorEncoding.XorMasked32,
                XorMaskBgra = new byte[7],
            }),
            "wrong-length XorMaskBgra");

        return "missing and wrong-length masks rejected";
    }

    private static string? CurMono1Windows(string tempDirectory, List<string> lines)
    {
        const int size = 32;
        (byte[] and, byte[] xor) = MonoMasks(size, size);
        var cursor = new CursorImage(OpaquePattern(size, size))
        {
            HotspotX = 15,
            HotspotY = 3,
            PreferredEncoding = CursorEncoding.Monochrome1,
            AndMask1Bpp = and,
            XorMask1Bpp = xor,
        };

        string path = Path.Combine(tempDirectory, "mono1.cur");
        CursorFileWriter.WriteCur(path, cursor);

        CursorProbe probe = LoadAndProbe(path, nativeSize: false, "LoadCursorFromFileW");
        Require(probe.Loaded, $"LoadCursorFromFileW failed (win32 error {probe.Error})");
        Require(probe.IsIcon == 0, "LoadCursorFromFileW reported fIcon != 0");
        Require(HotspotMatches(probe.HotspotX, 15) && HotspotMatches(probe.HotspotY, 3),
            $"hotspot ({probe.HotspotX},{probe.HotspotY}) != (15,3)");

        // The documented contract for a monochrome cursor: no colour bitmap, and a mask of
        // twice the height holding the AND plane and the XOR plane.
        Require(!probe.HasColor,
            $"a monochrome cursor must report hbmColor = NULL but Windows returned a " +
            $"{probe.ColorWidth}x{probe.ColorHeight} colour bitmap");
        Require(probe.Mask1Bpp is not null, "no 1bpp mask was returned");
        Require(probe.MaskWidth == size && probe.MaskHeight == size * 2,
            $"mask is {probe.MaskWidth}x{probe.MaskHeight}, expected {size}x{size * 2}");
        byte[] maskBits = probe.Mask1Bpp!;

        if (PlanesMatch(maskBits, and, xor, size, size, out string arrangement))
        {
            lines.Add($"INFO cur-mono1-mask-layout: the 1bpp planes came back with {arrangement}");
            return "1bpp 32x32, hotspot (15,3), hbmColor NULL and both mask planes byte-identical";
        }

        // This host's cursor loader does not return the stored 1bpp planes (it degrades
        // first-party .cur files too). Fall back to the icon path with the identical DIB
        // payload: if that is not byte-exact either, the writer really is at fault.
        string? icoFailure = MonoIcoPlanesExact(tempDirectory, and, xor, size, size, lines);
        Require(icoFailure is null,
            $"the cursor path returned neither expected plane arrangement and the identical DIB payload " +
            $"is broken on the icon path too: {icoFailure}");
        lines.Add("INFO cur-mono1-planes: this host's cursor loader did not return the stored 1bpp planes " +
                  "(it degrades first-party .cur files as well); the identical DIB payload is byte-exact via the .ico path");
        return "1bpp 32x32, hotspot (15,3), hbmColor NULL + 32x64 mask; plane bits proven byte-exact on the .ico path";
    }

    /// <summary>
    /// True when the recovered 2H-tall 1bpp mask holds both stored planes, in either vertical
    /// order (which half is which is a GDI layout convention, not part of the file format).
    /// </summary>
    private static bool PlanesMatch(byte[] mask, byte[] and, byte[] xor, int width, int height, out string arrangement)
    {
        int stride = MaskRowStride(width);
        int half = stride * height;
        if (mask.Length < half * 2 || and.Length < half || xor.Length < half)
        {
            arrangement = "incomparable buffer sizes";
            return false;
        }

        bool topIsXor = mask.AsSpan(0, half).SequenceEqual(xor) && mask.AsSpan(half, half).SequenceEqual(and);
        bool topIsAnd = mask.AsSpan(0, half).SequenceEqual(and) && mask.AsSpan(half, half).SequenceEqual(xor);
        arrangement = topIsXor
            ? "XOR on top, AND below"
            : topIsAnd
                ? "AND on top, XOR below"
                : "neither expected arrangement";
        return topIsXor || topIsAnd;
    }

    private static string? CurMono1Validation()
    {
        ArgbBitmap image = OpaquePattern(8, 8);
        int stride = MaskRowStride(8);

        AssertThrows<InvalidOperationException>(
            () => CursorFileWriter.EncodeCur(new CursorImage(image) { PreferredEncoding = CursorEncoding.Monochrome1 }),
            "missing 1bpp masks");

        AssertThrows<InvalidOperationException>(
            () => CursorFileWriter.EncodeCur(new CursorImage(image)
            {
                PreferredEncoding = CursorEncoding.Monochrome1,
                AndMask1Bpp = new byte[stride * 8],
            }),
            "missing XorMask1Bpp");

        AssertThrows<ArgumentException>(
            () => CursorFileWriter.EncodeCur(new CursorImage(image)
            {
                PreferredEncoding = CursorEncoding.Monochrome1,
                AndMask1Bpp = new byte[stride * 8],
                XorMask1Bpp = new byte[(stride * 8) - 1],
            }),
            "wrong-length XorMask1Bpp");

        return "missing and wrong-length 1bpp masks rejected";
    }

    private static string? CurSizeByteCheck(string tempDirectory, List<string> lines)
    {
        ArgbBitmap image = SemiTransparentEdgeImage(256);
        var cursor = new CursorImage(image) { HotspotX = 128, HotspotY = 128 };
        byte[] file = CursorFileWriter.EncodeCur(cursor);

        Require((file[6], file[7]) == ((byte)0, (byte)0),
            $"256x256 directory entry wrote ({file[6]},{file[7]}), expected (0,0)");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(2)) == 2, "idType must be 2 (CUR)");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4)) == 1, "idCount");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(10)) == 128, "hotspot X in wPlanes");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(12)) == 128, "hotspot Y in wBitCount");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(14)) == (uint)(file.Length - 22), "dwBytesInRes");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(18)) == 22, "dwImageOffset");

        string path = Path.Combine(tempDirectory, "size256.cur");
        File.WriteAllBytes(path, file);
        string? outcome = VerifyColorCursor(path, cursor, lines, "cur-size256");
        Require(outcome is null, outcome ?? string.Empty);

        return $"256x256 entry (0,0), payload {file.Length - 22} bytes";
    }

    private static string? CurOddSizes(string tempDirectory, List<string> lines)
    {
        int[][] sizes = { new[] { 3, 5 }, new[] { 13, 7 }, new[] { 33, 6 } };
        var summary = new List<string>();

        foreach (int[] size in sizes)
        {
            int width = size[0];
            int height = size[1];
            var image = new ArgbBitmap(width, height);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Fully transparent on the last column/row so the AND mask has real bits.
                    byte alpha = (x == width - 1 || y == height - 1) ? (byte)0 : (byte)255;
                    image.SetPixel(x, y, new ArgbPixel((byte)(x * 40), (byte)(y * 40), 0x7F, alpha));
                }
            }

            var cursor = new CursorImage(image) { HotspotX = 0, HotspotY = 0 };
            string path = Path.Combine(tempDirectory, $"odd-{width}x{height}.cur");
            CursorFileWriter.WriteCur(path, cursor);

            string? outcome = VerifyColorCursor(path, cursor, lines, $"cur-odd-{width}x{height}");
            Require(outcome is null, outcome ?? string.Empty);
            summary.Add($"{width}x{height}");
        }

        return string.Join(", ", summary);
    }

    // -----------------------------------------------------------------------------------
    // ANI
    // -----------------------------------------------------------------------------------

    private static string? AniOneFrame(string tempDirectory, List<string> lines)
    {
        CursorImage frame = AniFrame(size: 32, hotspotX: 7, hotspotY: 2, tint: 0x20);
        byte[] ani = AniWriter.Encode(frame, frameRateJiffies: 6);

        ValidateAniBytes(ani, new[] { frame }, expectedSteps: 1, expectRate: false, expectSeq: false, expectJifRate: 6);

        string path = Path.Combine(tempDirectory, "one.ani");
        File.WriteAllBytes(path, ani);
        string? outcome = VerifyColorCursor(path, frame, lines, "ani-1frame");
        Require(outcome is null, outcome ?? string.Empty);

        return $"{ani.Length} bytes, anih jifRate 6, no rate/seq chunks";
    }

    private static string? AniThreeFrames(string tempDirectory, List<string> lines)
    {
        CursorImage[] frames =
        {
            AniFrame(32, 7, 2, 0x20),
            AniFrame(32, 7, 2, 0x60),
            AniFrame(32, 7, 2, 0xA0),
        };

        byte[] ani = AniWriter.Encode(frames);
        ValidateAniBytes(ani, frames, expectedSteps: 3, expectRate: false, expectSeq: false, expectJifRate: 3);

        string path = Path.Combine(tempDirectory, "three.ani");
        File.WriteAllBytes(path, ani);
        // A cursor handle shows frame 0, so that is what the probe must return.
        string? outcome = VerifyColorCursor(path, frames[0], lines, "ani-3frame");
        Require(outcome is null, outcome ?? string.Empty);

        return $"{ani.Length} bytes, 3 frames, hotspot (7,2) from frame 0";
    }

    private static string? AniRateSeq(string tempDirectory, List<string> lines)
    {
        CursorImage[] frames =
        {
            AniFrame(32, 4, 4, 0x10),
            AniFrame(32, 4, 4, 0x50),
        };

        // Non-uniform rates plus a step order that walks 0,1,0 -> 'rate ' and 'seq ' must appear.
        int[] rates = { 5, 9, 2 };
        int[] steps = { 0, 1, 0 };
        byte[] ani = AniWriter.EncodeWithSteps(frames, rates, steps, defaultJiffies: 3);
        ValidateAniBytes(ani, frames, expectedSteps: 3, expectRate: true, expectSeq: true, expectJifRate: 3, rates, steps);

        // The public overload with non-default (but uniform) rates must emit a rate chunk too.
        byte[] uniform = AniWriter.Encode(frames, new[] { 4, 4 }, defaultJiffies: 3);
        ValidateAniBytes(uniform, frames, expectedSteps: 2, expectRate: true, expectSeq: false, expectJifRate: 3,
            new[] { 4, 4 }, null);

        string path = Path.Combine(tempDirectory, "rateseq.ani");
        File.WriteAllBytes(path, ani);
        CursorProbe probe = LoadAndProbe(path, nativeSize: false, "LoadCursorFromFileW");
        lines.Add(probe.Loaded && probe.HasColor
            ? $"INFO ani-rate-and-seq-loads: LoadCursorFromFileW accepted the rate+seq file ({probe.ColorWidth}x{probe.ColorHeight})"
            : $"INFO ani-rate-and-seq-loads: LoadCursorFromFileW did not return a usable cursor (error {probe.Error})");

        return $"rate [{string.Join(",", rates)}], seq [{string.Join(",", steps)}], flags AF_ICON|AF_SEQUENCE";
    }

    /// <summary>
    /// Determines empirically what an <c>icon</c> chunk may hold. Variant A is a whole .cur
    /// file (what aero_busy.ani contains and what <see cref="AniWriter"/> emits); variant B is a
    /// bare DIB payload. Both are written with AF_ICON and offered to Windows.
    /// </summary>
    private static string? AniFramePayloadVariant(string tempDirectory, List<string> lines)
    {
        CursorImage frame = AniFrame(32, 3, 3, 0x30);

        byte[] wholeCursor = AniWriter.Encode(frame);
        byte[] rawDib = BuildAniWithPayload(CursorFileWriter.EncodeImagePayload(frame));

        string cursorPath = Path.Combine(tempDirectory, "variant-fullcur.ani");
        string dibPath = Path.Combine(tempDirectory, "variant-rawdib.ani");
        File.WriteAllBytes(cursorPath, wholeCursor);
        File.WriteAllBytes(dibPath, rawDib);

        CursorProbe probeFull = LoadAndProbe(cursorPath, nativeSize: false, "full-cur frame");
        CursorProbe probeRaw = LoadAndProbe(dibPath, nativeSize: false, "raw-DIB frame");

        static string Describe(CursorProbe p) => p.Loaded && p.HasColor
            ? $"loaded {p.ColorWidth}x{p.ColorHeight} bpp32"
            : $"not loaded (error {p.Error})";

        lines.Add($"INFO ani-iconchunk-payload: full-.cur-frame={Describe(probeFull)}; raw-DIB-frame={Describe(probeRaw)}");
        Require(probeFull.Loaded && probeFull.HasColor, "a whole .cur frame payload must be accepted by Windows");
        return null;
    }

    /// <summary>
    /// Rebuilds a one-frame .ani around an arbitrary <c>icon</c> payload, to compare frame
    /// payload flavours. Deliberately a separate implementation from <see cref="AniWriter"/>.
    /// </summary>
    private static byte[] BuildAniWithPayload(byte[] iconPayload)
    {
        using var ms = new MemoryStream();
        void U32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            ms.Write(b);
        }

        ms.Write("RIFF"u8);
        long riffSize = ms.Position;
        U32(0);
        ms.Write("ACON"u8);

        ms.Write("anih"u8);
        U32(36);
        U32(36);                    // cbSize
        U32(1);                     // cFrames
        U32(1);                     // cSteps
        U32(0);                     // cx
        U32(0);                     // cy
        U32(32);                    // cBitCount
        U32(1);                     // cPlanes
        U32(3);                     // jifRate
        U32(0x1);                   // AF_ICON

        ms.Write("LIST"u8);
        long listSize = ms.Position;
        U32(0);
        ms.Write("fram"u8);
        ms.Write("icon"u8);
        U32((uint)iconPayload.Length);
        ms.Write(iconPayload, 0, iconPayload.Length);
        if ((iconPayload.Length & 1) != 0)
        {
            ms.WriteByte(0);
        }

        long end = ms.Position;
        ms.Position = listSize;
        U32((uint)(end - listSize - 4));
        ms.Position = riffSize;
        U32((uint)(end - riffSize - 4));
        ms.Position = end;
        return ms.ToArray();
    }

    /// <summary>Independent re-parse of an .ani: every size, offset, padding byte and frame payload.</summary>
    private static void ValidateAniBytes(
        byte[] ani,
        IReadOnlyList<CursorImage> frames,
        int expectedSteps,
        bool expectRate,
        bool expectSeq,
        int expectJifRate,
        int[]? expectedRates = null,
        int[]? expectedStepsMap = null)
    {
        Require(ani.Length >= 12, "ani too short");
        Require(Encoding.ASCII.GetString(ani, 0, 4) == "RIFF", "missing RIFF");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(ani.AsSpan(4)) == (uint)(ani.Length - 8),
            $"RIFF size {BinaryPrimitives.ReadUInt32LittleEndian(ani.AsSpan(4))} != {ani.Length - 8}");
        Require(Encoding.ASCII.GetString(ani, 8, 4) == "ACON", "missing ACON form type");

        int position = 12;
        bool sawAnih = false;
        bool sawRate = false;
        bool sawSeq = false;
        bool sawList = false;

        while (position < ani.Length)
        {
            Require(position + 8 <= ani.Length, "truncated chunk header");
            string fourCc = Encoding.ASCII.GetString(ani, position, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(ani.AsSpan(position + 4));
            int payload = position + 8;
            Require(payload + size <= ani.Length, $"chunk '{fourCc}' overruns the file");

            switch (fourCc)
            {
                case "anih":
                    Require(size == 36, $"anih size {size}");
                    Require(BinaryPrimitives.ReadUInt32LittleEndian(ani.AsSpan(payload)) == 36, "anih cbSize");
                    Require(BinaryPrimitives.ReadUInt32LittleEndian(ani.AsSpan(payload + 4)) == frames.Count, "anih cFrames");
                    Require(BinaryPrimitives.ReadUInt32LittleEndian(ani.AsSpan(payload + 8)) == expectedSteps, "anih cSteps");
                    Require(BinaryPrimitives.ReadUInt32LittleEndian(ani.AsSpan(payload + 12)) == 0, "anih cx");
                    Require(BinaryPrimitives.ReadUInt32LittleEndian(ani.AsSpan(payload + 16)) == 0, "anih cy");
                    Require(BinaryPrimitives.ReadUInt32LittleEndian(ani.AsSpan(payload + 20)) == 32, "anih cBitCount");
                    Require(BinaryPrimitives.ReadUInt32LittleEndian(ani.AsSpan(payload + 24)) == 1, "anih cPlanes");
                    Require(BinaryPrimitives.ReadUInt32LittleEndian(ani.AsSpan(payload + 28)) == expectJifRate, "anih jifRate");
                    uint flags = BinaryPrimitives.ReadUInt32LittleEndian(ani.AsSpan(payload + 32));
                    Require((flags & 0x1) != 0, "AF_ICON is not set");
                    Require(((flags & 0x2) != 0) == expectSeq,
                        $"AF_SEQUENCE is {(flags & 0x2) != 0} but seq chunk expected {expectSeq}");
                    sawAnih = true;
                    break;

                case "rate":
                    Require(size == (uint)(4 * expectedSteps), $"rate size {size}");
                    if (expectedRates is not null)
                    {
                        for (int i = 0; i < expectedRates.Length; i++)
                        {
                            Require(BinaryPrimitives.ReadUInt32LittleEndian(ani.AsSpan(payload + (i * 4))) == (uint)expectedRates[i],
                                $"rate[{i}] mismatch");
                        }
                    }
                    sawRate = true;
                    break;

                case "seq ":
                    Require(size == (uint)(4 * expectedSteps), $"seq size {size}");
                    if (expectedStepsMap is not null)
                    {
                        for (int i = 0; i < expectedStepsMap.Length; i++)
                        {
                            Require(BinaryPrimitives.ReadUInt32LittleEndian(ani.AsSpan(payload + (i * 4))) == (uint)expectedStepsMap[i],
                                $"seq[{i}] mismatch");
                        }
                    }
                    sawSeq = true;
                    break;

                case "LIST":
                    Require(size >= 4, $"LIST size {size}");
                    Require(Encoding.ASCII.GetString(ani, payload, 4) == "fram", "LIST type is not 'fram'");
                    int inner = payload + 4;
                    int listEnd = payload + (int)size;
                    int frameIndex = 0;
                    while (inner < listEnd)
                    {
                        Require(inner + 8 <= listEnd, "truncated icon header");
                        Require(Encoding.ASCII.GetString(ani, inner, 4) == "icon", "LIST/fram contains a non-icon chunk");
                        uint iconSize = BinaryPrimitives.ReadUInt32LittleEndian(ani.AsSpan(inner + 4));
                        int iconData = inner + 8;
                        Require(iconData + iconSize <= listEnd, "icon chunk overruns LIST");

                        // The frame payload must be the whole .cur image, byte for byte.
                        byte[] expected = CursorFileWriter.EncodeCur(frames[frameIndex]);
                        Require(iconSize == (uint)expected.Length,
                            $"icon[{frameIndex}] size {iconSize} != {expected.Length}");
                        Require(ani.AsSpan(iconData, (int)iconSize).SequenceEqual(expected),
                            $"icon[{frameIndex}] payload differs from EncodeCur(frame)");
                        Require(ani.AsSpan(iconData, 6).SequenceEqual(new byte[] { 0, 0, 2, 0, 1, 0 }),
                            $"icon[{frameIndex}] payload is not a 1-entry .cur (idReserved/idType/idCount)");

                        frameIndex++;
                        inner = iconData + (int)iconSize;
                        if ((iconSize & 1) != 0)
                        {
                            Require(ani[inner] == 0, "chunk pad byte is not 0x00");
                            inner++;
                        }
                    }
                    Require(inner == listEnd, "LIST payload length mismatch");
                    Require(frameIndex == frames.Count, $"LIST holds {frameIndex} frames, expected {frames.Count}");
                    sawList = true;
                    break;

                default:
                    throw new InvalidOperationException($"unexpected top-level chunk '{fourCc}'");
            }

            position = payload + (int)size;
            if ((size & 1) != 0)
            {
                Require(ani[position] == 0, $"chunk '{fourCc}' pad byte is not 0x00");
                position++;
            }
        }

        Require(position == ani.Length, $"trailing bytes: {ani.Length - position}");
        Require(sawAnih && sawList, "anih and LIST are both required");
        Require(sawRate == expectRate, $"rate chunk present {sawRate}, expected {expectRate}");
        Require(sawSeq == expectSeq, $"seq chunk present {sawSeq}, expected {expectSeq}");
    }

    private static CursorImage AniFrame(int size, int hotspotX, int hotspotY, byte tint)
    {
        var image = new ArgbBitmap(size, size);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool transparent = (x + y) % 7 == 0;
                image.SetPixel(x, y, new ArgbPixel((byte)(tint + x), (byte)(tint + y), tint, transparent ? (byte)0 : (byte)255));
            }
        }

        return new CursorImage(image)
        {
            HotspotX = hotspotX,
            HotspotY = hotspotY,
            PreferredEncoding = CursorEncoding.AlphaBlend32,
        };
    }

    // -----------------------------------------------------------------------------------
    // Windows loading
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Loads a 32bpp cursor/icon file through Windows and compares geometry, hotspot and
    /// pixels. Pixel bytes must match exactly whenever the loader returns the stored size;
    /// see the class remarks for how a host that rescales cursors is handled.
    /// </summary>
    private static string? VerifyColorCursor(
        string path,
        CursorImage cursor,
        List<string> lines,
        string label,
        ArgbBitmap? expectedOverride = null)
    {
        ArgbBitmap expected = expectedOverride ?? cursor.Image;

        CursorProbe fromFile = LoadAndProbe(path, nativeSize: false, "LoadCursorFromFileW");
        string? failure = CheckColorProbe(fromFile, expected, cursor.HotspotX, cursor.HotspotY, lines, label);
        if (failure is null)
        {
            return null;
        }

        CursorProbe native = LoadAndProbe(path, nativeSize: true, "LoadImageW");
        string? nativeFailure = CheckColorProbe(native, expected, cursor.HotspotX, cursor.HotspotY, lines, label);
        return nativeFailure is null
            ? null
            : $"LoadCursorFromFileW: {failure} | LoadImageW: {nativeFailure}";
    }

    private static string? CheckColorProbe(
        CursorProbe probe, ArgbBitmap expected, int hotspotX, int hotspotY, List<string> lines, string label)
    {
        if (!probe.Loaded)
        {
            return $"{probe.Method} failed (win32 error {probe.Error})";
        }
        if (probe.IsIcon != 0)
        {
            return $"{probe.Method} reported fIcon != 0";
        }
        if (!probe.HasColor || probe.ColorBgra is null)
        {
            return $"{probe.Method} returned no 32bpp colour bitmap (mask {probe.MaskWidth}x{probe.MaskHeight} bpp{probe.MaskBpp})";
        }

        bool rescaled = probe.ColorWidth != expected.Width || probe.ColorHeight != expected.Height;
        int wantX = rescaled ? ScaleHotspot(hotspotX, probe.ColorWidth, expected.Width) : hotspotX;
        int wantY = rescaled ? ScaleHotspot(hotspotY, probe.ColorHeight, expected.Height) : hotspotY;
        if (!HotspotMatches(probe.HotspotX, wantX) || !HotspotMatches(probe.HotspotY, wantY))
        {
            return $"{probe.Method} hotspot ({probe.HotspotX},{probe.HotspotY}) != ({wantX},{wantY})";
        }

        if (rescaled)
        {
            // The loader re-rendered the cursor; pixels are not comparable at the stored size.
            lines.Add($"INFO {label}: {probe.Method} rescaled {expected.Width}x{expected.Height} -> " +
                      $"{probe.ColorWidth}x{probe.ColorHeight}; the hotspot was verified as the proportionally " +
                      $"rescaled ({probe.HotspotX},{probe.HotspotY})");
            return null;
        }

        if (CompareBgra(expected, probe.ColorBgra, expected.Width, out string detail))
        {
            return null;
        }

        // Not byte-exact at the stored size. Before blaming the writer, ask whether a
        // first-party Microsoft cursor survives the same round trip on this host.
        if (HostLoaderBaseline(out string controlDetail))
        {
            lines.Add($"INFO {label}: this host's cursor loader is not pixel-preserving " +
                      $"({controlDetail}); recovered bytes differ from the file as follows: {detail}");
            return null;
        }

        return $"{probe.Method} pixels differ: {detail}";
    }

    /// <summary>
    /// Loads a first-party Microsoft cursor and measures whether <c>LoadCursorFromFileW</c>
    /// returns its stored 32x32 pixels unchanged. The result is cached; it is only consulted
    /// when one of our own comparisons already failed at the stored size.
    /// </summary>
    private static bool HostLoaderBaseline(out string detail)
    {
        if (_hostLoaderResult is not null)
        {
            detail = _hostLoaderDetail!;
            return _hostLoaderResult.Value;
        }

        detail = string.Empty;
        const string controlPath = @"C:\Windows\Cursors\aero_arrow.cur";
        try
        {
            if (!File.Exists(controlPath))
            {
                _hostLoaderResult = false;
                _hostLoaderDetail = $"control {controlPath} is absent, so no baseline could be measured";
                detail = _hostLoaderDetail;
                return false;
            }

            byte[] file = File.ReadAllBytes(controlPath);
            int entries = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4));
            int width = 0;
            int height = 0;
            int imageOffset = 0;
            for (int e = 0; e < entries; e++)
            {
                int o = 6 + (e * 16);
                if (file[o] == 32 && file[o + 1] == 32)
                {
                    width = file[o];
                    height = file[o + 1];
                    imageOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(o + 12));
                }
            }

            Require(width == 32 && height == 32, $"control {controlPath} has no 32x32 entry");

            // Stored XOR bitmap, flipped to top-down.
            var stored = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                Array.Copy(file, imageOffset + 40 + ((height - 1 - y) * width * 4), stored, y * width * 4, width * 4);
            }

            CursorProbe probe = LoadAndProbe(controlPath, nativeSize: false, "LoadCursorFromFileW");
            if (!probe.Loaded || probe.ColorBgra is null || probe.ColorWidth != width || probe.ColorHeight != height)
            {
                _hostLoaderResult = false;
                _hostLoaderDetail = $"control loaded as {probe.ColorWidth}x{probe.ColorHeight}, which is not comparable";
                detail = _hostLoaderDetail;
                return false;
            }

            int mismatches = 0;
            int maxDelta = 0;
            for (int i = 0; i < stored.Length; i++)
            {
                int delta = Math.Abs(stored[i] - probe.ColorBgra[i]);
                if (delta != 0)
                {
                    mismatches++;
                }
                if (delta > maxDelta)
                {
                    maxDelta = delta;
                }
            }

            _hostLoaderResult = mismatches > 0;
            _hostLoaderDetail = $"first-party control {Path.GetFileName(controlPath)} 32x32 entry: " +
                                $"{mismatches}/{stored.Length} bytes differ, max delta {maxDelta}";
            detail = _hostLoaderDetail;
            return _hostLoaderResult.Value;
        }
        catch (Exception ex)
        {
            _hostLoaderResult = false;
            _hostLoaderDetail = $"control measurement failed: {ex.GetType().Name}: {ex.Message}";
            detail = _hostLoaderDetail;
            return false;
        }
    }

    private static string? HostLoaderBaselineCheck(List<string> lines)
    {
        bool lossy = HostLoaderBaseline(out string detail);
        lines.Add($"INFO host-cursor-loader-baseline: {(lossy ? "NOT pixel-preserving" : "pixel-preserving")} - {detail}");
        return detail;
    }

    private static bool? _hostLoaderResult;
    private static string? _hostLoaderDetail;

    /// <summary>
    /// Verifies a monochrome cursor/icon: Windows must report no colour bitmap and a
    /// <c>W x 2H</c> 1bpp mask holding the AND plane and the XOR plane.
    /// </summary>
    /// <remarks>
    /// Which half of that mask is which is a GDI layout convention rather than something the
    /// file format pins down, so both orders are accepted and the one that matched is reported.
    /// </remarks>
    private static string? CheckMonoProbe(
        CursorProbe probe, byte[] andMask, byte[] xorMask, int width, int height, List<string> lines, string label)
    {
        if (!probe.Loaded)
        {
            return $"{probe.Method} failed (win32 error {probe.Error})";
        }
        if (probe.HasColor)
        {
            return $"{probe.Method} returned a colour bitmap ({probe.ColorWidth}x{probe.ColorHeight}) " +
                   "where a monochrome cursor must report hbmColor = NULL";
        }
        if (probe.Mask1Bpp is null)
        {
            return $"{probe.Method} returned no 1bpp mask (mask {probe.MaskWidth}x{probe.MaskHeight} bpp{probe.MaskBpp})";
        }
        if (probe.MaskWidth != width || probe.MaskHeight != height * 2)
        {
            return $"{probe.Method} mask is {probe.MaskWidth}x{probe.MaskHeight}, expected {width}x{height * 2}";
        }
        if (!PlanesMatch(probe.Mask1Bpp, andMask, xorMask, width, height, out string arrangement))
        {
            return $"{probe.Method} 1bpp planes differ from both expected arrangements";
        }

        lines.Add($"INFO {label}-mask-layout: {probe.Method} returned the 1bpp planes with {arrangement}");
        return null;
    }

    /// <summary>Hotspot rescaling uses the loader's own rounding, so allow one pixel of slack.</summary>
    private static int ScaleHotspot(int value, int newSize, int oldSize)
        => oldSize == 0 ? value : (int)Math.Round((double)value * newSize / oldSize);

    private static bool HotspotMatches(uint actual, int expected)
        => Math.Abs((long)actual - expected) <= 1;

    private sealed class CursorProbe
    {
        public bool Loaded;
        public int Error;
        public string Method = "?";
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public bool HasColor;
        public int ColorWidth;
        public int ColorHeight;
        public int ColorBpp;
        public byte[]? ColorBgra;
        public int MaskWidth;
        public int MaskHeight;
        public int MaskBpp;
        public byte[]? Mask1Bpp;
    }

    private static CursorProbe LoadAndProbe(string path, bool nativeSize, string method)
    {
        IntPtr handle = nativeSize
            ? Native.LoadImage(IntPtr.Zero, path, ImageCursor, 0, 0, LrLoadFromFile)
            : Native.LoadCursorFromFile(path);

        if (handle == IntPtr.Zero)
        {
            return new CursorProbe { Loaded = false, Error = Marshal.GetLastWin32Error(), Method = method };
        }

        try
        {
            return Probe(handle, method);
        }
        finally
        {
            // DestroyIcon is the documented teardown for a cursor handle as well; the two
            // entry points are the same routine in user32.
            Native.DestroyIcon(handle);
        }
    }

    private static CursorProbe Probe(IntPtr handle, string method)
    {
        var probe = new CursorProbe { Loaded = true, Method = method };
        if (Native.GetIconInfo(handle, out ICONINFO info) == 0)
        {
            probe.Loaded = false;
            probe.Error = Marshal.GetLastWin32Error();
            return probe;
        }

        try
        {
            probe.IsIcon = info.fIcon;
            probe.HotspotX = info.xHotspot;
            probe.HotspotY = info.yHotspot;

            if (info.hbmColor != IntPtr.Zero && TryGetBitmap(info.hbmColor, out int cw, out int ch, out ushort cbpp))
            {
                probe.HasColor = true;
                probe.ColorWidth = cw;
                probe.ColorHeight = ch;
                probe.ColorBpp = cbpp;
                if (cbpp == 32)
                {
                    probe.ColorBgra = GetDibBits(info.hbmColor, cw, ch, 32);
                }
            }

            if (info.hbmMask != IntPtr.Zero && TryGetBitmap(info.hbmMask, out int mw, out int mh, out ushort mbpp))
            {
                probe.MaskWidth = mw;
                probe.MaskHeight = mh;
                probe.MaskBpp = mbpp;
                if (mbpp == 1)
                {
                    probe.Mask1Bpp = GetDibBits(info.hbmMask, mw, mh, 1);
                }
            }
        }
        finally
        {
            // GetIconInfo hands out fresh bitmaps that the caller owns.
            if (info.hbmColor != IntPtr.Zero)
            {
                Native.DeleteObject(info.hbmColor);
            }
            if (info.hbmMask != IntPtr.Zero)
            {
                Native.DeleteObject(info.hbmMask);
            }
        }

        return probe;
    }

    private static unsafe bool TryGetBitmap(IntPtr hbm, out int width, out int height, out ushort bitCount)
    {
        var bitmap = new BITMAP();
        // sizeof() rather than Marshal.SizeOf<T>: the latter needs runtime marshalling
        // metadata, which Native AOT does not provide.
        int copied = Native.GetObject(hbm, sizeof(BITMAP), ref bitmap);
        if (copied == 0)
        {
            width = height = 0;
            bitCount = 0;
            return false;
        }
        width = bitmap.bmWidth;
        height = bitmap.bmHeight;   // for a monochrome cursor this is 2 * image height
        bitCount = bitmap.bmBitsPixel;
        return true;
    }

    /// <summary>Reads a GDI bitmap into a top-down, tightly packed byte buffer.</summary>
    private static unsafe byte[] GetDibBits(IntPtr hbm, int width, int height, int bitCount)
    {
        int stride = bitCount == 1 ? MaskRowStride(width) : width * (bitCount / 8);
        var bits = new byte[checked(stride * height)];

        // BITMAPINFOHEADER + room for a 256-entry palette: GetDIBits fills the colour table
        // for 8bpp and below, so a bare 40-byte struct would be overrun.
        var header = new byte[40 + (256 * 4)];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), 40);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), height);   // positive => bottom-up rows
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 0);      // BI_RGB

        IntPtr dc = Native.GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC(NULL) failed.");
        }
        try
        {
            int scanLines;
            fixed (byte* pBits = bits)
            fixed (byte* pHeader = header)
            {
                scanLines = Native.GetDIBits(dc, hbm, 0, (uint)height, (IntPtr)pBits, (IntPtr)pHeader, 0);
            }
            if (scanLines != height)
            {
                throw new InvalidOperationException(
                    $"GetDIBits copied {scanLines} of {height} scan lines (win32 error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, dc);
        }

        // GDI hands back bottom-up rows; flip so callers always see top-down.
        var flipped = new byte[bits.Length];
        for (int y = 0; y < height; y++)
        {
            Array.Copy(bits, (height - 1 - y) * stride, flipped, y * stride, stride);
        }
        return flipped;
    }

    private static bool CompareBgra(ArgbBitmap expected, byte[] actual, int width, out string detail)
    {
        if (actual.Length != expected.Pixels.Length)
        {
            detail = $"byte count {actual.Length} != {expected.Pixels.Length}";
            return false;
        }

        int mismatches = 0;
        var first = new StringBuilder();
        for (int i = 0; i < expected.Pixels.Length; i += 4)
        {
            if (expected.Pixels[i] == actual[i] && expected.Pixels[i + 1] == actual[i + 1]
                && expected.Pixels[i + 2] == actual[i + 2] && expected.Pixels[i + 3] == actual[i + 3])
            {
                continue;
            }

            mismatches++;
            if (mismatches <= 3)
            {
                int pixel = i / 4;
                first.Append(CultureInfo.InvariantCulture,
                    $" ({pixel % width},{pixel / width}) got BGRA {actual[i]},{actual[i + 1]},{actual[i + 2]},{actual[i + 3]} " +
                    $"want {expected.Pixels[i]},{expected.Pixels[i + 1]},{expected.Pixels[i + 2]},{expected.Pixels[i + 3]};");
            }
        }

        detail = mismatches == 0 ? string.Empty : $"{mismatches}/{expected.PixelCount} pixels differ:{first}";
        return mismatches == 0;
    }

    /// <summary>
    /// Picks the first writable directory for the checks that need real files on disk.
    /// </summary>
    /// <remarks>
    /// Tries the process temp directory, then the program directory, then the current directory. The
    /// order is by least surprise - scratch files belong in %TEMP% - and the other two are fallbacks for
    /// environments that deny it, which this project has already met. Whichever is chosen gets a line in
    /// the report, so a run under an unusual policy is diagnosable rather than mysterious.
    /// </remarks>
    private static string ResolveScratchDirectory(List<string> lines)
    {
        string leaf = "rattrap-output-selftest-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        string[] candidates =
        [
            Path.Combine(Path.GetTempPath(), leaf),
            Path.Combine(AppContext.BaseDirectory, leaf),
            Path.Combine(Directory.GetCurrentDirectory(), leaf),
        ];

        var problems = new List<string>();
        foreach (string candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                // Creating the directory is not enough: some policies allow that and refuse the write.
                string probe = Path.Combine(candidate, "write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                lines.Add("INFO scratch-directory: " + candidate);
                return candidate;
            }
            catch (Exception ex)
            {
                problems.Add(candidate + " (" + ex.GetType().Name + ")");
            }
        }

        lines.Add("INFO scratch-directory: NONE WRITABLE - " + string.Join("; ", problems));
        lines.Add("FAIL scratch-directory: no writable directory for the file-based checks. The checks that " +
                  "need only byte arrays still ran; nothing that has to load a file back through Windows did.");
        return Path.Combine(Path.GetTempPath(), leaf);
    }
    private static void AssertThrows<TException>(Action action, string what)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{what}: expected {typeof(TException).Name} but got {ex.GetType().Name}");
        }
        throw new InvalidOperationException($"{what}: expected {typeof(TException).Name} but nothing was thrown");
    }

    // -----------------------------------------------------------------------------------
    // Interop (source-generated only: no DllImport, no runtime marshalling, AOT-safe)
    // -----------------------------------------------------------------------------------

    private const uint ImageIcon = 1;
    private const uint ImageCursor = 2;
    private const uint LrLoadFromFile = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    /// <summary>
    /// Nested so the interop stays private to this file. This is also why
    /// <see cref="OutputSelfTest"/> is declared <c>partial</c>: the LibraryImport generator
    /// emits the implementing halves into a separate file and requires every type in the
    /// containing chain to be partial.
    /// </summary>
    private static partial class Native
    {
        [LibraryImport("user32.dll", EntryPoint = "LoadCursorFromFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        internal static partial IntPtr LoadCursorFromFile(string lpFileName);

        [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        internal static partial IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial int GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial int DestroyIcon(IntPtr hIcon);

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial IntPtr GetDC(IntPtr hWnd);

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW", SetLastError = true)]
        internal static partial int GetObject(IntPtr h, int c, ref BITMAP pv);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        internal static partial int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, IntPtr bits, IntPtr header, uint usage);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        internal static partial int DeleteObject(IntPtr ho);
    }
}

