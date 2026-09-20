using System.Buffers.Binary;
using System.IO.Compression;

namespace Sff;

/// <summary>
/// A small PNG codec covering what SFF 2.01 stores: non-interlaced 8-bit
/// indexed, RGB and RGBA. Written against the BCL's ZLibStream so the Sff
/// library needs no image dependency.
/// </summary>
internal static class Png
{
    public readonly record struct Image(int Width, int Height, byte[]? Indices, byte[]? Bgra, Rgba[]? Plte);

    private static ReadOnlySpan<byte> Magic => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a];

    public static Image Decode(ReadOnlySpan<byte> data)
    {
        if (!data.StartsWith(Magic)) throw new InvalidDataException("png: bad signature");

        int width = 0, height = 0, bitDepth = 0, colorType = 0;
        byte[]? plte = null, plteAlpha = null;
        var idat = new MemoryStream(data.Length);

        int p = 8;
        while (p + 8 <= data.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(data[p..]);
            var type = data.Slice(p + 4, 4);
            var body = data.Slice(p + 8, Math.Min(len, data.Length - p - 8));
            p += 12 + len; // length + type + data + crc

            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(body);
                height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                bitDepth = body[8];
                colorType = body[9];
                if (body[12] != 0) throw new NotSupportedException("png: interlaced images are not supported");
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                plte = body.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8) && colorType == 3)
            {
                plteAlpha = body.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(body);
            }
            else if (type.SequenceEqual("IEND"u8)) break;
        }
        if (width <= 0 || height <= 0) throw new InvalidDataException("png: missing IHDR");

        int channels = colorType switch
        {
            0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4,
            _ => throw new NotSupportedException($"png: color type {colorType}"),
        };
        int bitsPerPixel = channels * bitDepth;
        int stride = (width * bitsPerPixel + 7) / 8;
        int filterUnit = Math.Max(1, bitsPerPixel / 8);

        idat.Position = 0;
        using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new byte[(stride + 1) * height];
        ReadExactly(inflate, raw);

        var pixels = Unfilter(raw, stride, height, filterUnit);

        if (colorType == 3)
        {
            var indices = bitDepth == 8 ? pixels : Unpack(pixels, stride, width, height, bitDepth);
            return new Image(width, height, indices, null, BuildPlte(plte, plteAlpha));
        }
        if (bitDepth != 8) throw new NotSupportedException($"png: {bitDepth}-bit color is not supported");

        var bgra = new byte[width * height * 4];
        // One loop per color type keeps the per-pixel work branch-free.
        for (int y = 0; y < height; y++)
        {
            var src = pixels.AsSpan(y * stride, width * channels);
            var dst = bgra.AsSpan(y * width * 4, width * 4);
            switch (colorType)
            {
                case 0:
                    for (int x = 0; x < width; x++) { byte v = src[x]; dst[x * 4] = dst[x * 4 + 1] = dst[x * 4 + 2] = v; dst[x * 4 + 3] = 255; }
                    break;
                case 2:
                    for (int x = 0; x < width; x++) { dst[x * 4] = src[x * 3 + 2]; dst[x * 4 + 1] = src[x * 3 + 1]; dst[x * 4 + 2] = src[x * 3]; dst[x * 4 + 3] = 255; }
                    break;
                case 4:
                    for (int x = 0; x < width; x++) { byte v = src[x * 2]; dst[x * 4] = dst[x * 4 + 1] = dst[x * 4 + 2] = v; dst[x * 4 + 3] = src[x * 2 + 1]; }
                    break;
                default:
                    for (int x = 0; x < width; x++) { dst[x * 4] = src[x * 4 + 2]; dst[x * 4 + 1] = src[x * 4 + 1]; dst[x * 4 + 2] = src[x * 4]; dst[x * 4 + 3] = src[x * 4 + 3]; }
                    break;
            }
        }
        return new Image(width, height, null, bgra, BuildPlte(plte, plteAlpha));
    }

    /// <summary>Combines PLTE and tRNS into a color table.</summary>
    private static Rgba[]? BuildPlte(byte[]? plte, byte[]? alpha)
    {
        if (plte is null) return null;
        var pal = new Rgba[plte.Length / 3];
        for (int i = 0; i < pal.Length; i++)
            pal[i] = new Rgba(plte[i * 3], plte[i * 3 + 1], plte[i * 3 + 2],
                alpha is not null && i < alpha.Length ? alpha[i] : (byte)255);
        return pal;
    }

    /// <summary>Encodes BGRA pixels as an 8-bit RGBA PNG.</summary>
    public static byte[] Encode(byte[] bgra, int width, int height)
    {
        var raw = new byte[(width * 4 + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int dst = y * (width * 4 + 1);
            raw[dst++] = 0; // filter: none
            for (int x = 0; x < width; x++)
            {
                int si = (y * width + x) * 4;
                raw[dst++] = bgra[si + 2];
                raw[dst++] = bgra[si + 1];
                raw[dst++] = bgra[si];
                raw[dst++] = bgra[si + 3];
            }
        }

        var body = new MemoryStream();
        using (var deflate = new ZLibStream(body, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type RGBA

        var outp = new MemoryStream();
        outp.Write(Magic);
        WriteChunk(outp, "IHDR"u8, ihdr);
        WriteChunk(outp, "IDAT"u8, body.ToArray());
        WriteChunk(outp, "IEND"u8, []);
        return outp.ToArray();
    }

    /// <summary>Encodes BGRA pixels as an 8-bit RGB PNG, discarding alpha.</summary>
    public static byte[] EncodeRgb24(byte[] bgra, int width, int height)
    {
        int expected = checked(width * height * 4);
        if (bgra.Length != expected) throw new InvalidDataException("png24: BGRA pixel count does not match dimensions");

        int stride = checked(width * 3);
        var raw = new byte[checked((stride + 1) * height)];
        for (int y = 0; y < height; y++)
        {
            int dst = y * (stride + 1);
            raw[dst++] = 0; // filter: none
            for (int x = 0; x < width; x++)
            {
                int si = (y * width + x) * 4;

                raw[dst++] = bgra[si + 2];
                raw[dst++] = bgra[si + 1];
                raw[dst++] = bgra[si];
            }
        }

        var body = new MemoryStream();
        using (var deflate = new ZLibStream(body, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 2; // color type RGB

        var outp = new MemoryStream();
        outp.Write(Magic);
        WriteChunk(outp, "IHDR"u8, ihdr);
        WriteChunk(outp, "IDAT"u8, body.ToArray());
        WriteChunk(outp, "IEND"u8, []);
        return outp.ToArray();
    }

    /// <summary>
    /// Encodes indices and their palette as an 8-bit indexed PNG. Keeping the
    /// index order intact is the point: a sprite exported, edited elsewhere and
    /// brought back should still line up with the character's alternate palettes.
    /// </summary>
    public static byte[] EncodeIndexed(byte[] indices, int width, int height, Rgba[] palette)
    {
        var raw = new byte[(width + 1) * height];
        for (int y = 0; y < height; y++)
        {
            raw[y * (width + 1)] = 0; // filter: none
            Array.Copy(indices, y * width, raw, y * (width + 1) + 1, Math.Min(width, indices.Length - y * width));
        }

        var body = new MemoryStream();
        using (var deflate = new ZLibStream(body, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 3; // color type: indexed

        int n = Math.Min(palette.Length, 256);
        var plte = new byte[n * 3];
        var trns = new byte[n];
        bool anyTransparent = false;
        for (int i = 0; i < n; i++)
        {
            plte[i * 3] = palette[i].R;
            plte[i * 3 + 1] = palette[i].G;
            plte[i * 3 + 2] = palette[i].B;
            trns[i] = palette[i].A;
            if (palette[i].A != 255) anyTransparent = true;
        }

        var outp = new MemoryStream();
        outp.Write(Magic);
        WriteChunk(outp, "IHDR"u8, ihdr);
        WriteChunk(outp, "PLTE"u8, plte);
        if (anyTransparent) WriteChunk(outp, "tRNS"u8, trns);
        WriteChunk(outp, "IDAT"u8, body.ToArray());
        WriteChunk(outp, "IEND"u8, []);
        return outp.ToArray();
    }

    public static byte[] ReplacePalette(ReadOnlySpan<byte> data, Rgba[] palette)
    {
        var image = Decode(data);
        if (image.Indices is not { } indices || image.Plte is not { } currentPalette)
            throw new NotSupportedException("png: only indexed images have a replaceable palette");
        if (indices.Any(index => index >= currentPalette.Length))
            throw new InvalidDataException("png: an image index is outside its palette");

        var output = new MemoryStream();
        output.Write(data[..8]);
        int p = 8;
        bool wrotePalette = false;
        while (p + 12 <= data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data[p..]);
            if (length < 0 || p + 12L + length > data.Length)
                throw new InvalidDataException("png: invalid chunk length");
            var type = data.Slice(p + 4, 4);
            var body = data.Slice(p + 8, length);
            if (type.SequenceEqual("PLTE"u8))
            {
                int count = length / 3;
                if (length % 3 != 0 || count != currentPalette.Length || count == 0 || count > 256)
                    throw new InvalidDataException("png: invalid palette size");
                int maxEntries = 1 << data[24];
                if (count > maxEntries)
                    throw new InvalidDataException("png: palette exceeds the indexed image bit depth");
                var plte = new byte[count * 3];
                var alpha = new byte[count];
                bool hasAlpha = false;
                for (int i = 0; i < count; i++)
                {
                    var color = i < palette.Length ? palette[i] : default;
                    plte[i * 3] = color.R;
                    plte[i * 3 + 1] = color.G;
                    plte[i * 3 + 2] = color.B;
                    alpha[i] = color.A;
                    if (alpha[i] != 255) hasAlpha = true;
                }
                WriteChunk(output, "PLTE"u8, plte);
                if (hasAlpha) WriteChunk(output, "tRNS"u8, alpha);
                wrotePalette = true;
                p += 12 + length;
                while (p + 12 <= data.Length)
                {
                    int nextLength = BinaryPrimitives.ReadInt32BigEndian(data[p..]);
                    if (nextLength < 0 || p + 12L + nextLength > data.Length)
                        throw new InvalidDataException("png: invalid chunk length");
                    if (data.Slice(p + 4, 4).SequenceEqual("tRNS"u8))
                    {
                        p += 12 + nextLength;
                        continue;
                    }
                    break;
                }
                continue;
            }
            if (type.SequenceEqual("tRNS"u8) && wrotePalette)
            {
                p += 12 + length;
                continue;
            }
            output.Write(data.Slice(p, 12 + length));
            p += 12 + length;
            if (type.SequenceEqual("IEND"u8)) break;
        }
        if (!wrotePalette) throw new InvalidDataException("png: missing PLTE chunk");
        return output.ToArray();
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, body.Length);
        s.Write(len);
        s.Write(type);
        s.Write(body);

        uint crc = Crc32(type, body);
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    /// <summary>Reverses the per-scanline filters PNG applies before compression.</summary>
    private static byte[] Unfilter(byte[] raw, int stride, int height, int unit)
    {
        var outp = new byte[stride * height];
        unit = Math.Min(unit, stride);
        for (int y = 0; y < height; y++)
        {
            int srcOff = y * (stride + 1);
            if (srcOff >= raw.Length) break;
            int filter = raw[srcOff++];
            // A short final row reads as zeros, as before.
            int avail = Math.Clamp(raw.Length - srcOff, 0, stride);
            var cur = outp.AsSpan(y * stride, stride);
            raw.AsSpan(srcOff, avail).CopyTo(cur);
            ReadOnlySpan<byte> up = y > 0 ? outp.AsSpan((y - 1) * stride, stride) : default;

            switch (filter)
            {
                case 0: break;
                case 1:
                    for (int x = unit; x < stride; x++) cur[x] += cur[x - unit];
                    break;
                case 2:
                    if (y > 0) for (int x = 0; x < stride; x++) cur[x] += up[x];
                    break;
                case 3:
                    if (y > 0)
                    {
                        for (int x = 0; x < unit; x++) cur[x] += (byte)(up[x] / 2);
                        for (int x = unit; x < stride; x++) cur[x] += (byte)((cur[x - unit] + up[x]) / 2);
                    }
                    else for (int x = unit; x < stride; x++) cur[x] += (byte)(cur[x - unit] / 2);
                    break;
                case 4:
                    if (y > 0)
                    {
                        for (int x = 0; x < unit; x++) cur[x] += up[x]; // Paeth(0, b, 0) is b
                        for (int x = unit; x < stride; x++) cur[x] += Paeth(cur[x - unit], up[x], up[x - unit]);
                    }
                    else for (int x = unit; x < stride; x++) cur[x] += cur[x - unit]; // Paeth(a, 0, 0) is a
                    break;
                default:
                    throw new InvalidDataException($"png: filter {filter}");
            }
        }
        return outp;
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - 2 * c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Expands sub-byte indexed rows to one byte per pixel.</summary>
    private static byte[] Unpack(byte[] rows, int stride, int width, int height, int bitDepth)
    {
        var outp = new byte[width * height];
        int mask = (1 << bitDepth) - 1, per = 8 / bitDepth;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int shift = 8 - bitDepth * (x % per + 1);
                outp[y * width + x] = (byte)((rows[y * stride + x / per] >> shift) & mask);
            }
        return outp;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0xffffffffu;
        foreach (byte x in a) c = CrcTable[(c ^ x) & 0xff] ^ (c >> 8);
        foreach (byte x in b) c = CrcTable[(c ^ x) & 0xff] ^ (c >> 8);
        return c ^ 0xffffffffu;
    }

    private static void ReadExactly(Stream s, byte[] buf)
    {
        int n = 0;
        while (n < buf.Length)
        {
            int r = s.Read(buf, n, buf.Length - n);
            if (r <= 0) break; // short data is tolerated; the rest stays zero
            n += r;
        }
    }
}

/// <summary>Public entry point for reading and writing PNGs — Png itself, and its Image record, stay internal.</summary>
public static class PngExport
{
    public static int DecodePaletteCount(byte[] data)
    {
        var image = Png.Decode(data);
        return image.Plte?.Length ?? throw new InvalidDataException("png: no palette");
    }

    public static byte[] ReplacePalette(byte[] data, Palette palette) => Png.ReplacePalette(data, palette.Colors);

    /// <summary>Writes straight-alpha BGRA pixels as a 32-bit PNG.</summary>
    public static byte[] Encode(byte[] bgra, int width, int height) => Png.Encode(bgra, width, height);

    /// <summary>Writes palette indices as an 8-bit indexed PNG, order preserved.</summary>
    public static byte[] EncodeIndexed(byte[] indices, int width, int height, Rgba[] palette) =>
        Png.EncodeIndexed(indices, width, height, palette);

    /// <summary>
    /// Decodes any PNG (indexed or direct-color) to plain BGRA bytes, for a
    /// caller that just wants pixels to look at rather than a sprite to
    /// import — a reference image for color-picking, say.
    /// </summary>
    public static (byte[] Bgra, int Width, int Height) DecodeToBgra(byte[] data)
    {
        var img = Png.Decode(data);
        if (img.Bgra is { } bgra) return (bgra, img.Width, img.Height);

        var result = new byte[img.Width * img.Height * 4];
        var plte = img.Plte ?? [];
        var idx = img.Indices ?? [];
        int count = Math.Min(idx.Length, img.Width * img.Height);
        for (int i = 0; i < count; i++)
        {
            var c = idx[i] < plte.Length ? plte[idx[i]] : default;
            result[i * 4] = c.B; result[i * 4 + 1] = c.G; result[i * 4 + 2] = c.R; result[i * 4 + 3] = c.A;
        }
        return (result, img.Width, img.Height);
    }

    /// <summary>
    /// Sniffs the IHDR chunk rather than decoding: true when the PNG's pixels
    /// are palette indexes. Lets a dialog know whether "keep the image's
    /// indexes" means anything before importing. IHDR is always the first
    /// chunk: 8-byte signature, 4-byte length, "IHDR", then its 13 data bytes
    /// with the color type ninth, so byte 25 of the file.
    /// </summary>
    public static bool IsIndexed(byte[] data) =>
        data.Length > 25
        && data[12] == (byte)'I' && data[13] == (byte)'H' && data[14] == (byte)'D' && data[15] == (byte)'R'
        && data[25] == 3;

    /// <summary>
    /// Writes a sprite in whatever form keeps the most information: indexed when
    /// the sprite is indexed, 32-bit otherwise.
    /// </summary>
    public static byte[] Encode(Sprite sprite, Palette? palette, bool showMask)
    {
        if (sprite.Indexed is { } indices && (palette ?? sprite.Palette) is { } pal)
        {
            var colors = (Rgba[])pal.Colors.Clone();
            if (colors.Length > Sprite.TransparentIndex)
                colors[Sprite.TransparentIndex] = colors[Sprite.TransparentIndex] with { A = showMask ? (byte)255 : (byte)0 };
            return EncodeIndexed(indices, sprite.Width, sprite.Height, colors);
        }
        return Encode(sprite.ToBgra(palette, showMask), sprite.Width, sprite.Height);
    }
}
