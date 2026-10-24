namespace Sff;

/// <summary>One animated-WebP frame: Width*Height BGRA pixels, shown for DurationMs milliseconds.</summary>
public readonly record struct WebPFrame(byte[] Bgra, int DurationMs);

/// <summary>
/// Writes animated WebP files from still frames encoded by the caller (lossless, e.g. libwebp via Skia).
/// Later frames store only the rectangle that changed, replacing it without blending.
/// Fully transparent pixels are handed over as transparent black.
/// </summary>
public static class WebP
{
    /// <summary>Largest frame side a WebP image can store (14-bit width and height).</summary>
    public const int MaxSide = 16384;

    /// <param name="encodeStill">Encodes BGRA pixels (width, height) as a still .webp file; its image chunks are kept.</param>
    public static byte[] Encode(int width, int height, IReadOnlyList<WebPFrame> frames, Func<byte[], int, int, byte[]> encodeStill, bool loop = true)
    {
        if (width is < 1 or > MaxSide || height is < 1 or > MaxSide)
            throw new ArgumentOutOfRangeException(nameof(width), $"A WebP can't be {width}x{height}.");
        if (frames.Count == 0) throw new ArgumentException("A WebP animation needs at least one frame.", nameof(frames));

        int count = width * height;
        using var body = new MemoryStream();
        bool anyAlpha = false;
        uint[]? prev = null;
        foreach (var f in frames)
        {
            if (f.Bgra.Length < count * 4) throw new ArgumentException("A frame has fewer pixels than the WebP.", nameof(frames));
            var argb = ToArgb(f.Bgra, count);
            foreach (uint p in argb) anyAlpha |= p >> 24 != 255;
            var (x0, y0, x1, y1) = prev is null ? (0, 0, width, height) : Changed(prev, argb, width, height);
            int w = x1 - x0, h = y1 - y0;
            var sub = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    uint p = argb[(y0 + y) * width + x0 + x];
                    int i = (y * w + x) * 4;
                    (sub[i], sub[i + 1], sub[i + 2], sub[i + 3]) = ((byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24));
                }

            using var frame = new MemoryStream();
            Put24(frame, x0 / 2); Put24(frame, y0 / 2);
            Put24(frame, w - 1); Put24(frame, h - 1);
            Put24(frame, Math.Clamp(f.DurationMs, 0, 0xFFFFFF));
            frame.WriteByte(0b10); // don't blend, don't dispose
            frame.Write(ImageChunks(encodeStill(sub, w, h)));
            WriteChunk(body, "ANMF", frame.ToArray());
            prev = argb;
        }

        using var vp8x = new MemoryStream();
        vp8x.WriteByte((byte)(0x02 | (anyAlpha ? 0x10 : 0))); // animation, alpha
        Put24(vp8x, 0);
        Put24(vp8x, width - 1); Put24(vp8x, height - 1);
        byte[] anim = [0, 0, 0, 0, 0, 0]; // transparent background, loop count below
        if (!loop) anim[4] = 1;

        using var payload = new MemoryStream();
        payload.Write("WEBP"u8);
        WriteChunk(payload, "VP8X", vp8x.ToArray());
        WriteChunk(payload, "ANIM", anim);
        body.WriteTo(payload);

        using var file = new MemoryStream();
        file.Write("RIFF"u8);
        file.Write(BitConverter.GetBytes((uint)payload.Length));
        payload.WriteTo(file);
        return file.ToArray();
    }

    /// <summary>The ALPH, VP8 and VP8L chunks of a still .webp, as stored in it; VP8X, ICCP and metadata are dropped.</summary>
    internal static byte[] ImageChunks(byte[] still)
    {
        if (still.Length < 12 || !still.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !still.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            throw new InvalidDataException("The frame encoder didn't return a WebP file.");
        using var ms = new MemoryStream();
        for (int i = 12; i + 8 <= still.Length;)
        {
            string tag = System.Text.Encoding.ASCII.GetString(still, i, 4);
            long size = BitConverter.ToUInt32(still, i + 4);
            if (i + 8 + size > still.Length) throw new InvalidDataException($"The frame encoder's {tag} chunk is cut off.");
            if (tag is "ALPH" or "VP8 " or "VP8L") WriteChunk(ms, tag, still.AsSpan(i + 8, (int)size).ToArray());
            i += (int)(8 + size + (size & 1));
        }
        if (ms.Length == 0) throw new InvalidDataException("The frame encoder's WebP has no image data.");
        return ms.ToArray();
    }

    private static uint[] ToArgb(byte[] bgra, int count)
    {
        var argb = new uint[count];
        for (int i = 0; i < count; i++)
        {
            uint a = bgra[i * 4 + 3];
            argb[i] = a == 0 ? 0 : a << 24 | (uint)bgra[i * 4 + 2] << 16 | (uint)bgra[i * 4 + 1] << 8 | bgra[i * 4];
        }
        return argb;
    }

    /// <summary>Bounding box of pixels that differ from the previous frame, its corner rounded down to even
    /// (frame offsets are stored halved). An unchanged frame keeps a 1x1 rectangle at the corner.</summary>
    private static (int X0, int Y0, int X1, int Y1) Changed(uint[] prev, uint[] cur, int width, int height)
    {
        int x0 = width, y0 = height, x1 = -1, y1 = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (prev[y * width + x] == cur[y * width + x]) continue;
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
            }
        return x1 < 0 ? (0, 0, 1, 1) : (x0 & ~1, y0 & ~1, x1 + 1, y1 + 1);
    }

    private static void Put24(Stream s, int v)
    {
        s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)(v >> 16));
    }

    private static void WriteChunk(Stream s, string tag, byte[] data)
    {
        foreach (char ch in tag) s.WriteByte((byte)ch);
        s.Write(BitConverter.GetBytes((uint)data.Length));
        s.Write(data);
        if ((data.Length & 1) != 0) s.WriteByte(0);
    }
}
