using System.Buffers.Binary;

namespace Sff;

/// <summary>
/// Reads animated GIF files into whole-canvas frames. Each returned frame is
/// the picture as it appears when the GIF is played, so a frame that only
/// redraws a small area still comes back at the logical screen size.
/// </summary>
public static class GifDecoder
{
    private static ReadOnlySpan<byte> Header87 => "GIF87a"u8;
    private static ReadOnlySpan<byte> Header89 => "GIF89a"u8;

    /// <summary>Whether the data starts with a GIF87a/GIF89a signature.</summary>
    public static bool LooksLikeGif(ReadOnlySpan<byte> data) =>
        data.Length >= 6 && (data[..6].SequenceEqual(Header87) || data[..6].SequenceEqual(Header89));

    /// <summary>
    /// Decodes every frame as BGRA pixels over the logical screen, in file
    /// order, with the frame's delay in hundredths of a second. Every frame is
    /// Width x Height, whatever rectangle the GIF stored it in.
    /// </summary>
    public static List<GifFrame> Decode(byte[] data, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!LooksLikeGif(data)) throw new InvalidDataException("gif: bad signature");

        int p = 6;
        if (data.Length < p + 7) throw new InvalidDataException("gif: header is truncated");
        width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p));
        height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 2));
        byte packed = data[p + 4];
        p += 7;
        if (width < 1 || height < 1) throw new InvalidDataException("gif: zero-sized screen");

        var globalTable = (packed & 0x80) != 0 ? ReadTable(data, ref p, 2 << (packed & 0x07)) : null;

        // The logical screen bounds the frames: anything drawn outside it is
        // clipped away rather than growing the canvas.
        int stride = checked(width * 4);
        var canvas = new byte[stride * height];
        // Holds the canvas as it was before a RestorePrevious frame drew, so
        // that frame can be undone once the next one is composited.
        var before = new byte[canvas.Length];

        var frames = new List<GifFrame>();
        var transparent = -1;      // index skipped while drawing, -1 for none
        // Disposal and rectangle of the frame already on the canvas, which is
        // the one this frame's own GCE does not describe. A GCE precedes the
        // image it applies to, so these lag by one frame on purpose.
        var prevDisposal = 0;
        var prevX = 0; var prevY = 0; var prevW = 0; var prevH = 0;
        var disposal = 0;          // disposal of the frame about to be drawn
        var delayCs = 0;

        while (p < data.Length)
        {
            byte block = data[p++];

            if (block == 0x3B) break; // trailer

            if (block == 0x21) // extension
            {
                if (p >= data.Length) throw new InvalidDataException("gif: truncated extension");
                byte label = data[p++];
                if (label == 0xF9)
                {
                    // Graphic control: disposal, delay, then the transparent
                    // index. The index is only honored if the frame has a
                    // color table to point into, checked once that is read.
                    if (p + 6 > data.Length) throw new InvalidDataException("gif: truncated graphic control");
                    byte control = data[p + 1];
                    var pendingTransparent = (control & 0x01) != 0 ? data[p + 4] : -1;
                    delayCs = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 2));
                    disposal = (control >> 2) & 0x07;
                    p = SkipSubBlocks(data, p);
                    transparent = pendingTransparent;
                    continue;
                }
                p = SkipSubBlocks(data, p);
                continue;
            }

            if (block != 0x2C) throw new InvalidDataException($"gif: unknown block 0x{block:X2}");

            // Image descriptor.
            if (p + 9 > data.Length) throw new InvalidDataException("gif: truncated image descriptor");
            int fx = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p));
            int fy = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 2));
            int fw = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 4));
            int fh = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 6));
            byte flags = data[p + 8];
            p += 9;
            if (fw < 1 || fh < 1) throw new InvalidDataException("gif: zero-sized frame");
            // An interlaced frame stores its rows out of order, in four passes,
            // so each stored row has to be put back on the row it belongs to.
            var rowOf = (flags & 0x40) != 0 ? Deinterlace(fh) : null;

            var table = ((flags & 0x80) != 0 ? ReadTable(data, ref p, 2 << (flags & 0x07)) : null)
                ?? globalTable
                ?? throw new InvalidDataException("gif: a frame has no color table and the file declares no global one");

            if (transparent >= table.Length) transparent = -1; // no such color to be transparent

            if (p >= data.Length) throw new InvalidDataException("gif: truncated image data");
            int minCodeSize = data[p++];
            var indices = LzwDecode(ReadSubBlocks(data, ref p), minCodeSize, fw * fh);

            // Undo whatever the frame already on the canvas asked for. A
            // RestorePrevious frame's saved canvas is the one from before it
            // drew, so the swap happens now, and is swapped back after drawing.
            if (prevDisposal == 3)
            {
                (before, canvas) = (canvas, before);
            }
            else if (prevDisposal == 2)
            {
                // restore to background: that frame's own rect goes transparent
                Clear(canvas, stride, prevX, prevY, prevW, prevH, width, height);
            }

            for (int y = 0; y < fh; y++)
            {
                int dy = fy + (rowOf?[y] ?? y);
                if (dy < 0 || dy >= height) continue;
                int src = y * fw;
                int dst = dy * stride + fx * 4;
                for (int x = 0; x < fw; x++)
                {
                    int dx = fx + x;
                    if (dx < 0 || dx >= width) continue;
                    int index = indices[src + x];
                    if (index == transparent) continue;
                    var c = index < table.Length ? table[index] : default;
                    int o = dy * stride + dx * 4;
                    canvas[o] = c.B; canvas[o + 1] = c.G; canvas[o + 2] = c.R; canvas[o + 3] = c.A;
                }
            }

            frames.Add(new GifFrame((byte[])canvas.Clone(), delayCs));

            if (disposal == 3) (canvas, before) = (before, canvas);
            prevDisposal = disposal;
            prevX = fx; prevY = fy; prevW = fw; prevH = fh;
            transparent = -1; // a graphic control applies to one frame only
        }

        if (frames.Count == 0) throw new InvalidDataException("gif: no frames");
        return frames;
    }

    /// <summary>
    /// Maps each stored row to the row it belongs on, for a frame stored
    /// interlaced: the four passes take every 8th row, then every 8th from 4,
    /// then every 4th from 2, then every other row from 1.
    /// </summary>
    private static int[] Deinterlace(int height)
    {
        var row = new int[height];
        int at = 0;
        foreach (var (start, step) in new[] { (0, 8), (4, 8), (2, 4), (1, 2) })
            for (int y = start; y < height; y += step) row[at++] = y;
        return row;
    }

    private static Rgba[] ReadTable(byte[] data, ref int p, int count)
    {
        if (p + count * 3 > data.Length) throw new InvalidDataException("gif: truncated color table");
        var table = new Rgba[count];
        for (int i = 0; i < count; i++)
        {
            table[i] = new Rgba(data[p + i * 3], data[p + i * 3 + 1], data[p + i * 3 + 2], 255);
        }
        p += count * 3;
        return table;
    }

    /// <summary>
    /// Reads a chain of length-prefixed sub-blocks into one buffer. The length
    /// bytes are framing, not payload, so the parts are joined; p ends just
    /// past the chain's terminator.
    /// </summary>
    private static byte[] ReadSubBlocks(byte[] data, ref int p)
    {
        using var ms = new MemoryStream();
        while (p < data.Length)
        {
            int size = data[p++];
            if (size == 0) return ms.ToArray();
            if (p + size > data.Length) throw new InvalidDataException("gif: truncated sub-block");
            ms.Write(data, p, size);
            p += size;
        }
        throw new InvalidDataException("gif: unterminated sub-block chain");
    }

    /// <summary>Skips a chain of length-prefixed sub-blocks, returning the offset past the terminator.</summary>
    private static int SkipSubBlocks(byte[] data, int p)
    {
        while (p < data.Length)
        {
            int size = data[p++];
            if (size == 0) return p;
            p += size;
        }
        throw new InvalidDataException("gif: unterminated sub-block chain");
    }

    private static void Clear(byte[] canvas, int stride, int x, int y, int w, int h, int width, int height)
    {
        for (int row = y; row < y + h; row++)
        {
            if (row < 0 || row >= height) continue;
            int from = Math.Max(0, x);
            int to = Math.Min(width, x + w);
            if (to > from) Array.Clear(canvas, row * stride + from * 4, (to - from) * 4);
        }
    }

    /// <summary>
    /// GIF's variable-width LZW, least-significant bit first. Codes start at
    /// minCodeSize+1 bits, the clear code is 1<<minCodeSize, and the table grows
    /// as pairs are added until a clear resets it.
    /// </summary>
    private static byte[] LzwDecode(ReadOnlySpan<byte> data, int minCodeSize, int pixelCount)
    {
        if (minCodeSize is < 2 or > 8) throw new InvalidDataException($"gif: bad LZW code size {minCodeSize}");

        int clear = 1 << minCodeSize;
        int end = clear + 1;
        var outp = new byte[pixelCount];
        int written = 0;

        // Each code is a prefix followed by one pixel, so an entry's first
        // pixel is always a root index and never needs storing.
        var prefix = new int[4096];
        var added = new byte[4096];
        for (int i = 0; i < 4096; i++) prefix[i] = -1;

        int codeSize = minCodeSize + 1;
        int next = end + 1;
        int bitPos = 0;
        int old = -1;

        while (written < pixelCount)
        {
            if (bitPos + codeSize > data.Length * 8) break; // truncated stream: keep what we got
            int code = ReadCode(data, bitPos, codeSize);
            bitPos += codeSize;

            if (code == clear)
            {
                codeSize = minCodeSize + 1;
                next = end + 1;
                old = -1;
                continue;
            }
            if (code == end) break;

            // A code inside the table walks its own chain. The one special case
            // is the code just past the end of the table: the entry it names is
            // the one about to be created, so its string is the previous
            // code's string plus its first pixel.
            bool special = code == next;
            int walk;
            if (code < next) walk = code;
            else if (special && old != -1) walk = old;
            else if (code == next) throw new InvalidDataException("gif: LZW stream starts with a data code");
            else throw new InvalidDataException($"gif: LZW code {code} is past the end of the table");

            // Walk the prefix chain, which comes out reversed, then append. The
            // special case's extra pixel belongs at the end of the string, so it
            // has to go in front of the reversed chain: the chain is written
            // out back to front.
            var stack = new byte[4096];
            int sp = special ? 1 : 0;
            while (walk >= end)
            {
                if (walk >= 4096) throw new InvalidDataException("gif: corrupt LZW code");
                stack[sp++] = added[walk];
                walk = prefix[walk];
            }
            // The pending entry ends with the same pixel the next one starts with.
            byte lead = (byte)walk;
            if (special) stack[0] = lead;
            stack[sp++] = lead;

            // A short final run means the stream ended early; keep what fits.
            if (sp > pixelCount - written) sp = pixelCount - written;
            for (int i = sp - 1; i >= 0; i--) outp[written++] = stack[i];

            if (old != -1 && next < 4096)
            {
                prefix[next] = old;
                added[next] = lead;
                next++;
                // One entry behind the encoder by design, so the width grows a
                // code earlier than Gif.Encode's own check.
                if (next == 1 << codeSize && codeSize < 12) codeSize++;
            }
            // The next pending entry prefixes this code, whatever it was.
            old = code;
        }

        return outp;
    }

    private static int ReadCode(ReadOnlySpan<byte> data, int bitPos, int codeSize)
    {
        int value = 0;
        for (int i = 0; i < codeSize; i++)
        {
            int bit = bitPos + i;
            int b = (data[bit >> 3] >> (bit & 7)) & 1;
            value |= b << i;
        }
        return value;
    }
}
