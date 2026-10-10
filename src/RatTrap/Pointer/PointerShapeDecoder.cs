using RatTrap.Capture;
using RatTrap.Imaging;
using RatTrap.Interop;

namespace RatTrap.Pointer;

/// <summary>Outcome of decoding a compositor pointer-shape buffer.</summary>
public readonly record struct PointerShapeDecodeResult(CursorImage? Cursor, string Notes, string? Error)
{
    public bool Success => Cursor is not null;
}

/// <summary>
/// Turns the raw <c>GetFramePointerShape</c> payload into a <see cref="CursorImage"/>.
/// </summary>
/// <remarks>
/// <para>
/// Three shape types exist and each is written back out through a different
/// <see cref="CursorEncoding"/>, because they are not interchangeable:
/// </para>
/// <list type="table">
///   <item>
///     <term><c>COLOR</c></term>
///     <description>32bpp BGRA with a real alpha channel. Written as
///     <see cref="CursorEncoding.AlphaBlend32"/>.</description>
///   </item>
///   <item>
///     <term><c>MASKED_COLOR</c></term>
///     <description>32bpp whose alpha is only 0 or 255; where alpha is 255 the colour is
///     <b>XORed into the desktop</b> instead of blended. This cannot be expressed as an
///     alpha image at all, so it is preserved bit-exactly as
///     <see cref="CursorEncoding.XorMasked32"/> (AND mask all zero, XOR bitmap = the mask).
///     The RGBA view handed to previews/PNG shows the mask over black.</description>
///   </item>
///   <item>
///     <term><c>MONOCHROME</c></term>
///     <description>1bpp AND mask followed by a 1bpp XOR mask. "AND set, XOR clear" means
///     <i>invert the background</i>, which no 32bpp encoding can represent, so it is
///     preserved as <see cref="CursorEncoding.Monochrome1"/>.</description>
///   </item>
/// </list>
/// <para>
/// Note the asymmetry this creates: the RGBA view is always available (for preview and PNG)
/// but is only <i>exact</i> for <c>COLOR</c> shapes. When a caller needs fidelity it must
/// write the cursor file, which is why <see cref="CursorImage.PreferredEncoding"/> is set here.
/// </para>
/// </remarks>
public static class PointerShapeDecoder
{
    public static PointerShapeDecodeResult Decode(in PointerShapeSnapshot shape)
    {
        if (shape.Width <= 0 || shape.Height <= 0 || shape.Pitch <= 0)
        {
            return new PointerShapeDecodeResult(null, string.Empty,
                $"Shape has non-positive geometry ({shape.Width}x{shape.Height}, pitch {shape.Pitch}).");
        }

        return shape.ShapeType switch
        {
            Dxgi.POINTER_SHAPE_COLOR => DecodeColor(shape),
            Dxgi.POINTER_SHAPE_MASKED_COLOR => DecodeMaskedColor(shape),
            Dxgi.POINTER_SHAPE_MONOCHROME => DecodeMonochrome(shape),
            _ => new PointerShapeDecodeResult(null, string.Empty,
                $"Unknown DXGI pointer shape type {shape.ShapeType}."),
        };
    }

    private static PointerShapeDecodeResult DecodeColor(in PointerShapeSnapshot shape)
    {
        int required = shape.Pitch * shape.Height;
        if (shape.Buffer.Length < required)
        {
            return new PointerShapeDecodeResult(null, string.Empty,
                $"COLOR shape buffer is {shape.Buffer.Length} bytes but {required} are required.");
        }

        var image = new ArgbBitmap(shape.Width, shape.Height);
        for (int y = 0; y < shape.Height; y++)
        {
            for (int x = 0; x < shape.Width; x++)
            {
                int i = (y * shape.Pitch) + (x * 4);
                image.SetPixel(x, y, new ArgbPixel(shape.Buffer[i], shape.Buffer[i + 1], shape.Buffer[i + 2], shape.Buffer[i + 3]));
            }
        }

        // Some drivers hand over premultiplied alpha. Premultiplied data can never have a
        // colour channel greater than its alpha, so use that as the test; only un-premultiply
        // when the evidence is unambiguous (semi-transparent pixels exist and none violate it).
        bool anySemiTransparent = false;
        bool allWithinAlpha = true;
        for (int i = 0; i < required; i += 4)
        {
            byte a = shape.Buffer[i + 3];
            if (a == 0 || a == 255) continue;
            anySemiTransparent = true;
            if (shape.Buffer[i] > a || shape.Buffer[i + 1] > a || shape.Buffer[i + 2] > a)
            {
                allWithinAlpha = false;
                break;
            }
        }

        string notes = "COLOR shape: 32bpp BGRA with alpha.";
        if (anySemiTransparent && allWithinAlpha)
        {
            byte[] pixels = image.Pixels;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte a = pixels[i + 3];
                if (a is 0 or 255)
                {
                    continue;
                }
                // Straight alpha from premultiplied: c' = min(255, c * 255 / a).
                pixels[i] = (byte)Math.Min(255, (pixels[i] * 255) / a);
                pixels[i + 1] = (byte)Math.Min(255, (pixels[i + 1] * 255) / a);
                pixels[i + 2] = (byte)Math.Min(255, (pixels[i + 2] * 255) / a);
            }
            notes += " Data looked premultiplied and was un-premultiplied.";
        }

        return new PointerShapeDecodeResult(
            new CursorImage(image)
            {
                HotspotX = shape.HotspotX,
                HotspotY = shape.HotspotY,
                PreferredEncoding = CursorEncoding.AlphaBlend32,
                Provenance = CursorProvenance.DxgiPointerShape,
                Note = notes,
            },
            notes,
            null);
    }

    private static PointerShapeDecodeResult DecodeMaskedColor(in PointerShapeSnapshot shape)
    {
        int required = shape.Pitch * shape.Height;
        if (shape.Buffer.Length < required)
        {
            return new PointerShapeDecodeResult(null, string.Empty,
                $"MASKED_COLOR shape buffer is {shape.Buffer.Length} bytes but {required} are required.");
        }

        var image = new ArgbBitmap(shape.Width, shape.Height);
        var xorMask = new byte[shape.Width * shape.Height * 4];
        int opaquePixels = 0;
        int semiTransparent = 0;

        for (int y = 0; y < shape.Height; y++)
        {
            for (int x = 0; x < shape.Width; x++)
            {
                int source = (y * shape.Pitch) + (x * 4);
                byte a = shape.Buffer[source + 3];
                if (a is not (0 or 255)) semiTransparent++;

                // The exact payload is the XOR mask itself; keep it byte for byte.
                int target = ((y * shape.Width) + x) * 4;
                xorMask[target] = shape.Buffer[source];
                xorMask[target + 1] = shape.Buffer[source + 1];
                xorMask[target + 2] = shape.Buffer[source + 2];
                xorMask[target + 3] = a;

                if (a == 255)
                {
                    opaquePixels++;
                    // Rendering the mask over black reproduces exactly what a black
                    // background would show, which is the honest RGBA approximation.
                    image.SetPixel(x, y, new ArgbPixel(shape.Buffer[source], shape.Buffer[source + 1], shape.Buffer[source + 2], 255));
                }
                else
                {
                    image.SetPixel(x, y, ArgbPixel.Transparent);
                }
            }
        }

        string notes = $"MASKED_COLOR shape: {opaquePixels} XOR pixels of {shape.Width * shape.Height}.";
        if (semiTransparent != 0)
        {
            notes += $" WARNING: {semiTransparent} pixels had an alpha outside {{0,255}}, which MASKED_COLOR should not produce.";
        }

        return new PointerShapeDecodeResult(
            new CursorImage(image)
            {
                HotspotX = shape.HotspotX,
                HotspotY = shape.HotspotY,
                PreferredEncoding = CursorEncoding.XorMasked32,
                XorMaskBgra = xorMask,
                Provenance = CursorProvenance.DxgiPointerShape,
                Note = notes + " The RGBA view is the mask over black; the .cur output stores the exact XOR mask.",
            },
            notes,
            null);
    }

    private static PointerShapeDecodeResult DecodeMonochrome(in PointerShapeSnapshot shape)
    {
        // The payload is the AND mask followed by the XOR mask, each Pitch x Height bytes.
        int planeSize = shape.Pitch * shape.Height;
        if (shape.Buffer.Length < planeSize * 2)
        {
            return new PointerShapeDecodeResult(null, string.Empty,
                $"MONOCHROME shape buffer is {shape.Buffer.Length} bytes but {planeSize * 2} are required.");
        }

        var andRaw = shape.Buffer.AsSpan(0, planeSize);
        var xorRaw = shape.Buffer.AsSpan(planeSize, planeSize);

        int packedStride = BitMask1Bpp.RowBytes(shape.Width);
        byte[] andPacked = BitMask1Bpp.Repack(andRaw, shape.Pitch, shape.Width, shape.Height, packedStride);
        byte[] xorPacked = BitMask1Bpp.Repack(xorRaw, shape.Pitch, shape.Width, shape.Height, packedStride);

        var image = new ArgbBitmap(shape.Width, shape.Height);
        int invertedPixels = 0;

        for (int y = 0; y < shape.Height; y++)
        {
            for (int x = 0; x < shape.Width; x++)
            {
                bool andBit = BitMask1Bpp.GetBit(andPacked, packedStride, x, y);
                bool xorBit = BitMask1Bpp.GetBit(xorPacked, packedStride, x, y);

                if (!andBit && !xorBit)
                {
                    image.SetPixel(x, y, ArgbPixel.OpaqueBlack);
                }
                else if (!andBit && xorBit)
                {
                    image.SetPixel(x, y, ArgbPixel.OpaqueWhite);
                }
                else if (andBit && !xorBit)
                {
                    // "Invert the background" - not representable in RGBA. Render a mid grey
                    // so the shape stays visible in previews, and rely on Monochrome1 for fidelity.
                    invertedPixels++;
                    image.SetPixel(x, y, new ArgbPixel(0x80, 0x80, 0x80, 255));
                }
                else
                {
                    image.SetPixel(x, y, ArgbPixel.Transparent);
                }
            }
        }

        string notes = $"MONOCHROME shape: 1bpp AND+XOR masks, {invertedPixels} background-inverting pixels.";
        if (invertedPixels != 0)
        {
            notes += " Those pixels render as mid grey in the RGBA view and cannot be represented in PNG; " +
                     "the .cur output stores the raw 1bpp masks and is exact.";
        }

        return new PointerShapeDecodeResult(
            new CursorImage(image)
            {
                HotspotX = shape.HotspotX,
                HotspotY = shape.HotspotY,
                PreferredEncoding = CursorEncoding.Monochrome1,
                AndMask1Bpp = andPacked,
                XorMask1Bpp = xorPacked,
                Provenance = CursorProvenance.DxgiPointerShape,
                Note = notes,
            },
            notes,
            null);
    }

    private static IEnumerable<(int X, int Y, ArgbPixel Pixel)> EnumeratePixels(ArgbBitmap image)
    {
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                yield return (x, y, image.GetPixel(x, y));
            }
        }
    }
}
