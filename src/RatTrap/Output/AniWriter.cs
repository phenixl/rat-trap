using System.Buffers.Binary;
using RatTrap.Imaging;

namespace RatTrap.Output;

/// <summary>
/// Writes Windows animated cursor (.ani) files: <c>RIFF</c>/<c>ACON</c> with an
/// <c>anih</c> header, optional <c>rate </c> and <c>seq </c> chunks, and a
/// <c>LIST</c>/<c>fram</c> holding one <c>icon</c> chunk per frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>What an <c>icon</c> frame chunk contains.</b> Parsing
/// <c>C:\Windows\Cursors\aero_busy.ani</c> shows each 30894-byte <c>icon</c> payload starts
/// with <c>00 00 02 00 03 00</c> — <c>idReserved=0, idType=2 (CUR), idCount=3</c> — followed
/// by three <c>ICONDIRENTRY</c> records and their DIBs, and 6 + 3*16 + 16936 + 9640 + 4264
/// equals the chunk length exactly. So a frame is a <b>complete .cur file image</b>, not a
/// bare DIB. That is what this writer emits (matching <c>AF_ICON</c>), and the self-test
/// loads the result back with <c>LoadCursorFromFileW</c> to prove Windows accepts it.
/// </para>
/// <para>
/// RIFF chunks are word-aligned: a pad byte follows an odd-length payload, and a parent
/// chunk's size includes that padding. (Every payload produced here is even, so the pad
/// path never fires in practice — the arithmetic is kept correct regardless.)
/// </para>
/// </remarks>
internal static class AniWriter
{
    /// <summary><c>aniheader.flags</c>: frames are complete icon/cursor file images.</summary>
    private const uint AfIcon = 0x0001;

    /// <summary><c>aniheader.flags</c>: a <c>seq </c> chunk remaps steps onto frames.</summary>
    private const uint AfSequence = 0x0002;

    /// <summary>Size of the <c>aniheader</c> structure itself; also its <c>cbSize</c> field.</summary>
    private const int AniHeaderSize = 36;

    /// <summary>Single-frame .ani wrapping one cursor.</summary>
    public static byte[] Encode(CursorImage frame, int frameRateJiffies = 3)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Build(new[] { frame }, null, null, frameRateJiffies);
    }

    /// <summary>
    /// Multi-frame .ani. <paramref name="jiffiesPerFrame"/> is in 1/60 s units and must have
    /// one entry per frame (i.e. per step) when supplied; <c>null</c> means every step keeps
    /// the <paramref name="defaultJiffies"/> rate recorded in <c>anih.jifRate</c>.
    /// </summary>
    public static byte[] Encode(IReadOnlyList<CursorImage> frames, int[]? jiffiesPerFrame = null, int defaultJiffies = 3)
        => Build(frames, jiffiesPerFrame, null, defaultJiffies);

    public static void WriteFile(string path, IReadOnlyList<CursorImage> frames, int[]? jiffiesPerFrame = null, int defaultJiffies = 3)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.WriteAllBytes(path, Encode(frames, jiffiesPerFrame, defaultJiffies));
    }

    /// <summary>
    /// Explicit step -> frame mapping, which is the only way to make the writer emit a
    /// <c>seq </c> chunk (the public overloads always use an identity mapping).
    /// </summary>
    internal static byte[] EncodeWithSteps(IReadOnlyList<CursorImage> frames, int[] jiffiesPerStep, int[] stepToFrame, int defaultJiffies = 3)
        => Build(frames, jiffiesPerStep, stepToFrame, defaultJiffies);

    private static byte[] Build(IReadOnlyList<CursorImage>? frames, int[]? jiffiesPerStep, int[]? stepToFrame, int defaultJiffies)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            throw new ArgumentException("An .ani needs at least one frame.", nameof(frames));
        }
        if (defaultJiffies <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultJiffies), defaultJiffies,
                "The default frame duration must be positive (units of 1/60 s).");
        }
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i] is null)
            {
                throw new ArgumentException($"Frame {i} is null.", nameof(frames));
            }
        }

        // cSteps is the playback length; without an explicit mapping it is one step per frame.
        int steps = stepToFrame?.Length ?? frames.Count;
        if (steps == 0)
        {
            throw new ArgumentException("stepToFrame must contain at least one step.", nameof(stepToFrame));
        }

        var rates = new int[steps];
        if (jiffiesPerStep is null)
        {
            Array.Fill(rates, defaultJiffies);
        }
        else
        {
            if (jiffiesPerStep.Length != steps)
            {
                throw new ArgumentException(
                    $"jiffiesPerFrame must have one entry per step ({steps}) but had {jiffiesPerStep.Length}.", nameof(jiffiesPerStep));
            }
            for (int i = 0; i < steps; i++)
            {
                if (jiffiesPerStep[i] <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(jiffiesPerStep), jiffiesPerStep[i],
                        $"Step {i} duration must be positive (units of 1/60 s).");
                }
                rates[i] = jiffiesPerStep[i];
            }
        }

        // "rate " is redundant when every step already uses anih.jifRate.
        bool emitRate = false;
        for (int i = 0; i < steps; i++)
        {
            if (rates[i] != defaultJiffies)
            {
                emitRate = true;
                break;
            }
        }

        bool emitSeq = false;
        if (stepToFrame is not null && !IsIdentity(stepToFrame, frames.Count))
        {
            for (int i = 0; i < stepToFrame.Length; i++)
            {
                if (stepToFrame[i] < 0 || stepToFrame[i] >= frames.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(stepToFrame), stepToFrame[i],
                        $"Step {i} maps to frame {stepToFrame[i]}, outside 0..{frames.Count - 1}.");
                }
            }
            emitSeq = true;
        }

        uint flags = AfIcon | (emitSeq ? AfSequence : 0u);
        int bitCount = CursorFileWriter.BitCountFor(frames[0]);

        using var buffer = new MemoryStream();
        buffer.Write("RIFF"u8);
        long riffSizePosition = buffer.Position;
        WriteU32(buffer, 0);            // patched once the whole file is written
        buffer.Write("ACON"u8);

        BeginChunk(buffer, "anih"u8, out long anihPosition);
        WriteU32(buffer, AniHeaderSize);            // cbSize
        WriteU32(buffer, (uint)frames.Count);       // cFrames: number of distinct images
        WriteU32(buffer, (uint)steps);              // cSteps: number of playback steps
        WriteU32(buffer, 0);                        // cx (0 = use the frame's own width)
        WriteU32(buffer, 0);                        // cy (0 = use the frame's own height)
        WriteU32(buffer, (uint)bitCount);           // cBitCount
        WriteU32(buffer, 1);                        // cPlanes
        WriteU32(buffer, (uint)defaultJiffies);     // jifRate: duration of a step with no "rate " entry
        WriteU32(buffer, flags);                    // flags
        EndChunk(buffer, anihPosition);

        if (emitRate)
        {
            BeginChunk(buffer, "rate"u8, out long ratePosition);
            for (int i = 0; i < steps; i++)
            {
                WriteU32(buffer, (uint)rates[i]);
            }
            EndChunk(buffer, ratePosition);
        }

        if (emitSeq)
        {
            // FourCC is literally "seq " with a trailing space.
            BeginChunk(buffer, "seq "u8, out long seqPosition);
            for (int i = 0; i < steps; i++)
            {
                WriteU32(buffer, (uint)stepToFrame![i]);
            }
            EndChunk(buffer, seqPosition);
        }

        BeginChunk(buffer, "LIST"u8, out long listPosition);
        buffer.Write("fram"u8);                     // list type, part of the LIST payload
        for (int i = 0; i < frames.Count; i++)
        {
            byte[] cursorFile = CursorFileWriter.EncodeCur(frames[i]);
            BeginChunk(buffer, "icon"u8, out long iconPosition);
            buffer.Write(cursorFile, 0, cursorFile.Length);
            EndChunk(buffer, iconPosition);
        }
        EndChunk(buffer, listPosition);

        // RIFF's size field counts everything after the size field itself (i.e. file length - 8).
        long end = buffer.Position;
        buffer.Position = riffSizePosition;
        WriteU32(buffer, (uint)(end - riffSizePosition - 4));
        buffer.Position = end;
        return buffer.ToArray();
    }

    private static bool IsIdentity(int[] stepToFrame, int frameCount)
    {
        if (stepToFrame.Length != frameCount)
        {
            return false;
        }
        for (int i = 0; i < stepToFrame.Length; i++)
        {
            if (stepToFrame[i] != i)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Writes a chunk's FourCC and a placeholder size; pair with <see cref="EndChunk"/>.</summary>
    private static void BeginChunk(Stream stream, ReadOnlySpan<byte> fourCc, out long sizePosition)
    {
        stream.Write(fourCc);
        sizePosition = stream.Position;
        WriteU32(stream, 0);
    }

    /// <summary>
    /// Pads an odd-length payload to a word boundary and back-patches the chunk size. The
    /// size field itself excludes the pad byte, but the pad is physically inside the parent
    /// chunk, so the parent's size (measured later) naturally includes it.
    /// </summary>
    private static void EndChunk(Stream stream, long sizePosition)
    {
        long payloadEnd = stream.Position;
        long payloadLength = payloadEnd - sizePosition - 4;
        if ((payloadLength & 1) != 0)
        {
            stream.WriteByte(0);
        }

        long afterPad = stream.Position;
        stream.Position = sizePosition;
        WriteU32(stream, (uint)payloadLength);
        stream.Position = afterPad;
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> scratch = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(scratch, value);
        stream.Write(scratch);
    }
}
