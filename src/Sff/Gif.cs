namespace Sff;

/// <summary>One animated-GIF frame: Width*Height BGRA pixels, shown for DelayCs hundredths of a second.</summary>
public readonly record struct GifFrame(byte[] Bgra, int DelayCs);

/// <summary>
/// Writes animated GIF89a files. Each frame gets its own color table: exact up to 255
/// colors, a fixed 6×7×6 color cube beyond that. Alpha under 128 is transparent.
/// </summary>
public static class Gif
{
    public static byte[] Encode(int width, int height, IReadOnlyList<GifFrame> frames, bool loop = true)
    {
        if (width is < 1 or > 65535 || height is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(width), $"A GIF can't be {width}x{height}.");
        if (frames.Count == 0) throw new ArgumentException("A GIF needs at least one frame.", nameof(frames));

        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("GIF89a"u8);
        w.Write((ushort)width);
        w.Write((ushort)height);
        w.Write((byte)0x70); // no global color table, 8-bit color resolution
        w.Write((byte)0);    // background color index
        w.Write((byte)0);    // pixel aspect ratio
        if (loop)
        {
            w.Write((byte)0x21); w.Write((byte)0xFF); w.Write((byte)11);
            w.Write("NETSCAPE2.0"u8);
            w.Write((byte)3); w.Write((byte)1); w.Write((ushort)0); w.Write((byte)0); // loop forever
        }

        int count = width * height;
        foreach (var f in frames)
        {
            if (f.Bgra.Length < count * 4) throw new ArgumentException("A frame has fewer pixels than the GIF.", nameof(frames));
            var (indices, colors) = Quantize(f.Bgra, count);
            int transparent = colors.Count;
            int bits = 1;
            while ((1 << bits) < colors.Count + 1) bits++;

            // Graphic control: restore to background (transparent) after each frame, delay, transparent index.
            w.Write((byte)0x21); w.Write((byte)0xF9); w.Write((byte)4);
            w.Write((byte)((2 << 2) | 1));
            w.Write((ushort)Math.Clamp(f.DelayCs, 0, 65535));
            w.Write((byte)transparent);
            w.Write((byte)0);

            // Image descriptor with a local color table.
            w.Write((byte)0x2C);
            w.Write((ushort)0); w.Write((ushort)0);
            w.Write((ushort)width); w.Write((ushort)height);
            w.Write((byte)(0x80 | (bits - 1)));
            for (int i = 0; i < 1 << bits; i++)
            {
                int rgb = i < colors.Count ? colors[i] : 0;
                w.Write((byte)(rgb >> 16)); w.Write((byte)(rgb >> 8)); w.Write((byte)rgb);
            }

            int minCodeSize = Math.Max(2, bits);
            w.Write((byte)minCodeSize);
            WriteLzw(w, indices, minCodeSize);
        }
        w.Write((byte)0x3B);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Maps pixels to palette indices; the transparent index is colors.Count.</summary>
    private static (byte[] Indices, List<int> Colors) Quantize(byte[] bgra, int count)
    {
        var map = new Dictionary<int, int>();
        var colors = new List<int>();
        bool cube = false;
        for (int i = 0; i < count && !cube; i++)
        {
            if (bgra[i * 4 + 3] < 128) continue;
            int rgb = bgra[i * 4 + 2] << 16 | bgra[i * 4 + 1] << 8 | bgra[i * 4];
            if (map.ContainsKey(rgb)) continue;
            if (colors.Count == 255) { cube = true; break; }
            map[rgb] = colors.Count;
            colors.Add(rgb);
        }
        if (cube)
        {
            colors.Clear();
            for (int r = 0; r < 6; r++)
                for (int g = 0; g < 7; g++)
                    for (int b = 0; b < 6; b++)
                        colors.Add(r * 255 / 5 << 16 | g * 255 / 6 << 8 | b * 255 / 5);
        }

        var indices = new byte[count];
        byte transparent = (byte)colors.Count;
        for (int i = 0; i < count; i++)
        {
            byte b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2];
            if (bgra[i * 4 + 3] < 128) indices[i] = transparent;
            else if (!cube) indices[i] = (byte)map[r << 16 | g << 8 | b];
            else indices[i] = (byte)(((r * 5 + 127) / 255 * 7 + (g * 6 + 127) / 255) * 6 + (b * 5 + 127) / 255);
        }
        return (indices, colors);
    }

    /// <summary>Variable-width LZW as GIF wants it, in 255-byte sub-blocks, with a clear code when the table fills.</summary>
    private static void WriteLzw(BinaryWriter w, byte[] indices, int minCodeSize)
    {
        int clear = 1 << minCodeSize, end = clear + 1;
        int codeSize = minCodeSize + 1, next = end + 1;
        var table = new Dictionary<int, int>();
        var block = new byte[255];
        int blockLen = 0, bitBuf = 0, bitCount = 0;

        void Put(byte v)
        {
            block[blockLen++] = v;
            if (blockLen < 255) return;
            w.Write((byte)255); w.Write(block); blockLen = 0;
        }
        void Emit(int code)
        {
            bitBuf |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8) { Put((byte)bitBuf); bitBuf >>= 8; bitCount -= 8; }
        }

        Emit(clear);
        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            int k = indices[i], key = prefix << 8 | k;
            if (table.TryGetValue(key, out int code)) { prefix = code; continue; }
            Emit(prefix);
            if (next < 4096)
            {
                table[key] = next++;
                if (next > 1 << codeSize && codeSize < 12) codeSize++;
            }
            else
            {
                Emit(clear);
                table.Clear();
                codeSize = minCodeSize + 1;
                next = end + 1;
            }
            prefix = k;
        }
        Emit(prefix);
        Emit(end);
        if (bitCount > 0) Put((byte)bitBuf);
        if (blockLen > 0) { w.Write((byte)blockLen); w.Write(block, 0, blockLen); }
        w.Write((byte)0);
    }
}
