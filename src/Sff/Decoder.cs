using System.Buffers.Binary;

namespace Sff;

/// <summary>Turns one sprite's on-disk bytes into pixels.</summary>
internal static class Decoder
{
    private static ReadOnlySpan<byte> PngMagic => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a];

    /// <summary>
    /// Fills the sprite's pixel fields. Every compressed format prefixes the
    /// payload with a 4-byte decompressed length.
    /// </summary>
    public static void DecodePixels(Sprite s, ReadOnlySpan<byte> data)
    {
        if (s.Width <= 0 || s.Height <= 0)
            throw new InvalidDataException($"sprite has zero size {s.Width}x{s.Height}");
        int npix = s.Width * s.Height;

        switch (s.SrcFormat)
        {
            case SpriteFormat.Raw:
                switch (s.SrcDepth)
                {
                    case <= 8:
                        if (data.Length < npix) throw new InvalidDataException("truncated data");
                        s.Indexed = data[..npix].ToArray();
                        return;
                    case 24:
                    case 32:
                        s.Direct = FromRaw(data, npix, s.SrcDepth);
                        return;
                    default:
                        throw new NotSupportedException($"raw {s.SrcDepth}-bit sprites are not supported");
                }

            case SpriteFormat.Rle8:
            case SpriteFormat.Rle5:
            case SpriteFormat.Lz5:
            {
                if (data.Length < 4) throw new InvalidDataException("truncated data");
                uint decompressedLength = BinaryPrimitives.ReadUInt32LittleEndian(data);
                if (decompressedLength != npix)
                    throw new InvalidDataException($"compressed sprite length {decompressedLength} does not match dimensions ({npix})");
                var payload = data[4..];
                var outp = new byte[npix];
                switch (s.SrcFormat)
                {
                    case SpriteFormat.Rle8: Rle8.Decode(payload, outp); break;
                    case SpriteFormat.Rle5: Rle5.Decode(payload, outp); break;
                    default: Lz5.Decode(payload, outp); break;
                }
                s.Indexed = outp;
                return;
            }

            case SpriteFormat.Png8:
            case SpriteFormat.Png24:
            case SpriteFormat.Png32:
            {
                var png = StripToPng(data);
                var img = Png.Decode(png);
                if (s.SrcFormat == SpriteFormat.Png8)
                {
                    // A PNG8's embedded PLTE is not authoritative; the SFF palette wins.
                    if (img.Indices is null)
                        throw new InvalidDataException("png8: expected an indexed image");
                    s.Indexed = Fit(img.Indices, img.Width, img.Height, s.Width, s.Height, 1);
                }
                else
                {
                    if (img.Bgra is null) throw new InvalidDataException("png: expected a color image");
                    var bgra = Fit(img.Bgra, img.Width, img.Height, s.Width, s.Height, 4);
                    for (int i = 0; i + 3 < bgra.Length; i += 4)
                        if (bgra[i + 3] == 0) bgra[i] = bgra[i + 1] = bgra[i + 2] = 0;
                    s.Direct = bgra;
                }
                return;
            }
        }
        throw new InvalidDataException($"unknown sprite format {(byte)s.SrcFormat}");
    }

    /// <summary>
    /// Converts raw true-color rows to BGRA. A fully transparent pixel has its
    /// color zeroed, because otherwise its RGB bleeds into neighboring pixels
    /// once the texture is filtered or scaled.
    /// </summary>
    private static byte[] FromRaw(ReadOnlySpan<byte> data, int npix, int depth)
    {
        int step = depth / 8;
        if (data.Length < npix * step) throw new InvalidDataException("truncated data");
        var dst = new byte[npix * 4];
        for (int i = 0; i < npix && (i + 1) * step <= data.Length; i++)
        {
            byte r = data[i * step], g = data[i * step + 1], b = data[i * step + 2];
            byte a = step == 4 ? data[i * step + 3] : (byte)255;
            if (a == 0) r = g = b = 0;
            dst[i * 4] = b;
            dst[i * 4 + 1] = g;
            dst[i * 4 + 2] = r;
            dst[i * 4 + 3] = a;
        }
        return dst;
    }

    /// <summary>Skips the length prefix when present; some writers omit it.</summary>
    private static ReadOnlySpan<byte> StripToPng(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(PngMagic)) return data;
        if (data.Length > 4 && data[4..].StartsWith(PngMagic)) return data[4..];
        return data;
    }

    /// <summary>Copies a decoded bitmap into the node's declared dimensions.</summary>
    private static byte[] Fit(byte[] src, int sw, int sh, int dw, int dh, int bpp)
    {
        if (sw == dw && sh == dh && src.Length == dw * dh * bpp) return src; // already the right size
        var dst = new byte[dw * dh * bpp];
        int w = Math.Min(sw, dw), h = Math.Min(sh, dh);
        for (int y = 0; y < h; y++)
            Array.Copy(src, y * sw * bpp, dst, y * dw * bpp, w * bpp);
        return dst;
    }
}

/// <summary>
/// RLE8: a byte with bits 7,6 set to 0,1 is a run control packet; every other
/// byte is a literal pixel.
/// </summary>
internal static class Rle8
{
    public static void Decode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int j = 0;
        for (int i = 0; i < src.Length && j < dst.Length;)
        {
            byte b = src[i++];
            if ((b & 0xc0) != 0x40) { dst[j++] = b; continue; }
            if (i >= src.Length) throw new InvalidDataException("rle8: truncated data");
            byte c = src[i++];
            for (int n = b & 0x3f; n > 0 && j < dst.Length; n--) dst[j++] = c;
        }
        if (j < dst.Length) throw new InvalidDataException("rle8: truncated data");
    }

    /// <summary>
    /// Packs pixels back into RLE8. Runs of three or more become control
    /// packets; shorter ones stay literal. Values in 0x40-0x7f would be read
    /// back as control bytes, so they are always escaped as runs.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> pix)
    {
        var outp = new List<byte>(pix.Length / 2);
        for (int i = 0; i < pix.Length;)
        {
            byte v = pix[i];
            int run = 1;
            while (i + run < pix.Length && pix[i + run] == v && run < 63) run++;

            if (run >= 3 || v is >= 0x40 and <= 0x7f)
            {
                outp.Add((byte)(0x40 | run));
                outp.Add(v);
            }
            else for (int k = 0; k < run; k++) outp.Add(v);
            i += run;
        }
        return outp.ToArray();
    }
}

/// <summary>
/// RLE5: each packet is a run of one color (color 0 unless the color bit is
/// set) followed by data_length bytes holding a 3-bit run of a 5-bit color.
/// A packet whose color bit is set stores a full 128 sub-packets as a zero
/// data_length; the tool relies on the decoder stopping at the sprite's pixel
/// count, so a zero here means 128 only while output remains.
/// </summary>
internal static class Rle5
{
    public static void Decode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int j = 0;
        for (int i = 0; i + 1 < src.Length && j < dst.Length;)
        {
            int runLen = src[i];
            bool hasColor = (src[i + 1] & 0x80) != 0;
            int dataLen = src[i + 1] & 0x7f;
            i += 2;
            if (hasColor && dataLen == 0) dataLen = 128;

            byte c = 0;
            if (hasColor)
            {
                if (i >= src.Length) throw new InvalidDataException("rle5: truncated data");
                c = src[i++];
            }
            for (int n = 0; n <= runLen && j < dst.Length; n++) dst[j++] = c;

            for (int k = 0; k < dataLen && j < dst.Length; k++)
            {
                if (i >= src.Length) throw new InvalidDataException("rle5: truncated data");
                byte b = src[i++];
                for (int n = 0; n <= (b >> 5) && j < dst.Length; n++) dst[j++] = (byte)(b & 0x1f);
            }
        }
        if (j < dst.Length) throw new InvalidDataException("rle5: truncated data");
    }

    /// <summary>Packs 5-bit pixels into RLE5.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> pix)
    {
        var outp = new List<byte>(pix.Length / 2);
        int i = 0;
        byte runColor = 0;
        int runLeft = 0; // pixels of the current run not yet written
        while (i < pix.Length)
        {
            if (runLeft == 0)
            {
                runColor = pix[i];
                runLeft = RunLength(pix, i);
            }
            int lead = Math.Min(runLeft, 256);
            bool hasColor = runColor != 0;

            outp.Add((byte)(lead - 1));
            int countPos = outp.Count;
            outp.Add(hasColor ? (byte)0x80 : (byte)0);
            if (hasColor) outp.Add(runColor);
            i += lead;
            runLeft -= lead;

            // Fill the packet with sub-packets of up to eight pixels. A run
            // longer than seventeen is left as the next packet's leading run,
            // as the tool does; a color-marked packet must carry at least one
            // sub-packet (a zero count would be read back as 128).
            int subs = 0;
            while (i < pix.Length && subs < 127)
            {
                if (runLeft == 0)
                {
                    runColor = pix[i];
                    runLeft = RunLength(pix, i);
                    if (runLeft > 17 && (subs > 0 || !hasColor)) break;
                }
                int take = Math.Min(runLeft, 8);
                outp.Add((byte)(((take - 1) << 5) | runColor));
                subs++;
                i += take;
                runLeft -= take;
            }
            outp[countPos] |= (byte)subs;
        }
        return outp.ToArray();
    }

    private static int RunLength(ReadOnlySpan<byte> pix, int start)
    {
        byte v = pix[start];
        int n = 1;
        while (start + n < pix.Length && pix[start + n] == v) n++;
        return n;
    }
}

/// <summary>
/// LZ5: a control byte gives the type of each of the next eight packets, LSB
/// first. A set bit means an LZ back-reference, a clear bit an RLE run. Short LZ
/// packets take their distance byte from a pair of bits recycled across four
/// consecutive packets; long packets carry a 10-bit distance.
///
/// Both distances are stored one less than their true value. That detail is not
/// in any spec I could reach; it was established by histogramming back-reference
/// distances against sprite width across a real file, where every path peaks at
/// exactly the width, meaning "copy the pixel directly above".
/// </summary>
internal static class Lz5
{
    /// <summary>
    /// Expands LZ5 data. When distances is given, each back-reference distance
    /// is recorded, which is what the decoder's correctness test checks.
    /// </summary>
    public static void Decode(ReadOnlySpan<byte> src, Span<byte> dst, IList<int>? distances = null)
    {
        if (src.Length == 0) throw new InvalidDataException("lz5: truncated data");

        int i = 1, j = 0;
        byte ctrl = src[0];
        int ctrlBit = 0;
        byte recycle = 0;
        int recycleBits = 0;

        while (j < dst.Length)
        {
            if (i >= src.Length) throw new InvalidDataException("lz5: truncated data");
            int d = src[i++];

            if ((ctrl & (1 << ctrlBit)) != 0)
            {
                int n;
                if ((d & 0x3f) == 0) // long packet: 10-bit distance, length in a third byte
                {
                    if (i + 1 >= src.Length) throw new InvalidDataException("lz5: truncated data");
                    d = (d << 2) | src[i];
                    d++; // stored one less, like the short-packet distance
                    n = src[i + 1] + 2;
                    i += 2;
                }
                else // short packet: 6-bit length, distance from recycled bits
                {
                    n = d & 0x3f;
                    recycle |= (byte)((d & 0xc0) >> recycleBits);
                    recycleBits += 2;
                    if (recycleBits < 8)
                    {
                        if (i >= src.Length) throw new InvalidDataException("lz5: truncated data");
                        d = src[i++] + 1;
                    }
                    else
                    {
                        d = recycle + 1;
                        recycle = 0;
                        recycleBits = 0;
                    }
                }
                if (d <= 0 || d > j)
                    throw new InvalidDataException($"lz5: back-reference distance {d} at output offset {j}");
                distances?.Add(d);
                for (; n >= 0 && j < dst.Length; n--) { dst[j] = dst[j - d]; j++; }
            }
            else // RLE packet: 3-bit run of a 5-bit color, or an extended run
            {
                int n = d >> 5;
                if ((d & 0xe0) == 0)
                {
                    if (i >= src.Length) throw new InvalidDataException("lz5: truncated data");
                    n = src[i++] + 8;
                }
                for (; n > 0 && j < dst.Length; n--) dst[j++] = (byte)(d & 0x1f);
            }

            if (++ctrlBit >= 8)
            {
                if (i >= src.Length) break;
                ctrl = src[i++];
                ctrlBit = 0;
            }
        }
        if (j < dst.Length) throw new InvalidDataException("lz5: truncated data");
    }

    /// <summary>True when the data can be expressed in LZ5, which is 5-bit only.</summary>
    public static bool CanEncode(ReadOnlySpan<byte> pix)
    {
        foreach (var v in pix) if (v > 31) return false;
        return true;
    }

    /// <summary>
    /// Packs pixels into LZ5. Short back-reference packets take their distance
    /// from two bits contributed by each of four consecutive short packets, so
    /// the fourth packet's distance is settled before the first is written. The
    /// way round it is to write each packet's length now and patch the two
    /// distance bits of all four once the group completes; the two fields sit in
    /// different bits of the same byte, so patching afterwards is safe.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> pix, int window = 1024)
    {
        var outp = new List<byte>(pix.Length / 2);
        int ctrlPos = -1, ctrl = 0, ctrlBit = 8;
        var group = new List<int>(4);

        void Packet(bool isLz)
        {
            if (ctrlPos < 0 || ctrlBit == 8)
            {
                outp.Add(0);
                ctrlPos = outp.Count - 1;
                ctrl = 0;
                ctrlBit = 0;
            }
            if (isLz) ctrl |= 1 << ctrlBit;
            outp[ctrlPos] = (byte)ctrl;
            ctrlBit++;
        }

        void EmitShort(int length, int distance)
        {
            Packet(true);
            int pos = outp.Count;
            outp.Add((byte)(length - 1)); // the top two bits are patched below
            group.Add(pos);

            if (group.Count < 4)
            {
                outp.Add((byte)(distance - 1));
                return;
            }
            int recycled = distance - 1;
            for (int k = 0; k < 4; k++)
                outp[group[k]] |= (byte)(((recycled >> (6 - k * 2)) & 0x03) << 6);
            group.Clear();
        }

        void EmitLong(int length, int distance)
        {
            Packet(true);
            int raw = distance - 1;
            outp.Add((byte)((raw >> 8) << 6));
            outp.Add((byte)(raw & 0xff));
            outp.Add((byte)(length - 3));
        }

        void EmitRun(byte color, int run)
        {
            Packet(false);
            if (run <= 7) outp.Add((byte)((run << 5) | color));
            else { outp.Add(color); outp.Add((byte)(run - 8)); }
        }

        for (int i = 0; i < pix.Length;)
        {
            int bestLen = 0, bestDist = 0;
            int limit = Math.Min(window, i);
            for (int d = 1; d <= limit; d++)
            {
                if (pix[i - d] != pix[i]) continue;
                int l = 0;
                while (l < 258 && i + l < pix.Length && pix[i - d + l] == pix[i + l]) l++;
                if (l <= bestLen) continue;
                bestLen = l;
                bestDist = d;
                if (l == 258) break;
            }

            int run = 1;
            while (i + run < pix.Length && pix[i + run] == pix[i] && run < 263) run++;

            bool shortOk = bestDist <= 256 && bestLen is >= 2 and <= 64;
            int lzCost = shortOk ? 2 : 3;
            int runCost = run <= 7 ? 1 : 2;

            if (bestLen >= 2 && bestLen - lzCost > run - runCost)
            {
                if (shortOk) EmitShort(bestLen, bestDist);
                else EmitLong(bestLen, bestDist);
                i += bestLen;
            }
            else
            {
                EmitRun((byte)(pix[i] & 0x1f), run);
                i += run;
            }
        }
        return outp.ToArray();
    }
}
