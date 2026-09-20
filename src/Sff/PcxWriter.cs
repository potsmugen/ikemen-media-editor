using System.Buffers.Binary;

namespace Sff;

/// <summary>Writes the 8-bit run-length PCX images SFFv1 is made of.</summary>
internal static class PcxWriter
{
    /// <summary>
    /// Encodes indices as a 256-color PCX. The palette is appended after the
    /// pixel data, introduced by 0x0c, which is where PCX readers look for it.
    /// </summary>
    public static byte[] Encode(byte[] indices, int width, int height, Rgba[] palette, bool includePalette)
    {
        var outp = new List<byte>(indices.Length / 2 + 1024);

        // PCX keeps an even number of bytes per scanline.
        int bytesPerLine = width + (width & 1);

        var header = new byte[128];
        header[0] = 0x0a;  // manufacturer
        header[1] = 5;     // version 3.0 with palette
        header[2] = 1;     // run-length encoded
        header[3] = 8;     // bits per pixel
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)(width - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)(height - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 72);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), 72);
        header[65] = 1;    // one plane
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(66), (ushort)bytesPerLine);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(68), 1); // color palette
        outp.AddRange(header);

        var row = new byte[bytesPerLine];
        for (int y = 0; y < height; y++)
        {
            Array.Copy(indices, y * width, row, 0, Math.Min(width, indices.Length - y * width));
            if (bytesPerLine > width) row[width] = row[width - 1 >= 0 ? width - 1 : 0];
            EncodeRow(outp, row);
        }

        if (includePalette)
        {
            outp.Add(0x0c);
            for (int i = 0; i < 256; i++)
            {
                var c = i < palette.Length ? palette[i] : default;
                outp.Add(c.R);
                outp.Add(c.G);
                outp.Add(c.B);
            }
        }
        return outp.ToArray();
    }

    /// <summary>Compares the 256 RGB entries PCX writes, ignoring alpha.</summary>
    internal static bool SameRgbPalette(Rgba[] a, Rgba[] b)
    {
        for (int i = 0; i < 256; i++)
        {
            var ca = i < a.Length ? a[i] : default;
            var cb = i < b.Length ? b[i] : default;
            if (ca.R != cb.R || ca.G != cb.G || ca.B != cb.B) return false;
        }
        return true;
    }

    /// <summary>
    /// A byte with its top two bits set is a run count, so any literal in that
    /// range has to be written as a run of one.
    /// </summary>
    private static void EncodeRow(List<byte> outp, byte[] row)
    {
        for (int x = 0; x < row.Length;)
        {
            byte v = row[x];
            int run = 1;
            while (x + run < row.Length && row[x + run] == v && run < 63) run++;

            if (run > 1 || (v & 0xc0) == 0xc0)
            {
                outp.Add((byte)(0xc0 | run));
                outp.Add(v);
            }
            else outp.Add(v);
            x += run;
        }
    }
}

/// <summary>Public entry point for writing PCX sprites — Pcx and PcxWriter stay internal.</summary>
public static class PcxExport
{
    public static int DecodePaletteCount(byte[] data) => Pcx.ReadPaletteOnly(data)?.Length
        ?? throw new InvalidDataException("pcx: no embedded palette");

    public static byte[] ReplacePalette(byte[] data, Palette palette)
    {
        if (data.Length < 128 || data[0] != 0x0a)
            throw new InvalidDataException("pcx: bad signature");
        var current = Pcx.ReadPaletteOnly(data)
            ?? throw new InvalidDataException("pcx: no embedded palette");
        int bitsPerPixel = data[3], planes = data[65];
        if (current.Length == 256 && (bitsPerPixel != 8 || planes != 1))
            throw new InvalidDataException("pcx: invalid 256-color palette layout");
        var output = (byte[])data.Clone();
        int start = current.Length == 256 ? data.Length - 768 : 16;
        for (int i = 0; i < current.Length; i++)
        {
            var color = i < palette.Colors.Length ? palette.Colors[i] : default;
            output[start + i * 3] = color.R;
            output[start + i * 3 + 1] = color.G;
            output[start + i * 3 + 2] = color.B;
        }
        return output;
    }

    /// <summary>
    /// Writes an indexed sprite as a 256-color PCX with its palette appended.
    /// PCX has no transparency, so index 0 simply stays index 0. True-color
    /// sprites cannot be stored in PCX and are refused.
    /// </summary>
    public static byte[] Encode(Sprite sprite, Palette? palette)
    {
        if (sprite.Indexed is not { } indices)
            throw new NotSupportedException("PCX stores 8-bit indexed pixels only, and this sprite is true color.");
        var pal = palette ?? sprite.Palette
            ?? throw new InvalidDataException("the sprite has no palette to write into the PCX.");
        return EncodeIndexed(indices, sprite.Width, sprite.Height, pal);
    }

    /// <summary>Writes indexed pixels of an explicit size as a 256-color PCX.</summary>
    public static byte[] EncodeIndexed(byte[] indices, int width, int height, Palette palette)
    {
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(palette);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height), "PCX dimensions must be positive.");
        if (width > ushort.MaxValue - 1 || height > ushort.MaxValue + 1)
            throw new ArgumentOutOfRangeException(width > ushort.MaxValue - 1 ? nameof(width) : nameof(height), "PCX dimensions exceed the format limits.");
        if (indices.Length != checked(width * height))
            throw new ArgumentException("The index array must contain exactly width × height values.", nameof(indices));
        return PcxWriter.Encode(indices, width, height, palette.Colors, includePalette: true);
    }
}
