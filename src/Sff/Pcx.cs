using System.Buffers.Binary;

namespace Sff;

/// <summary>
/// SFFv1 stores every sprite as a PCX image, so reading v1 means reading PCX.
/// Only what MUGEN actually produces is handled: 8-bit run-length encoded, one
/// plane, with the 256-color palette appended after the pixel data.
/// </summary>
internal static class Pcx
{
    public readonly record struct Image(int Width, int Height, byte[] Indices, Rgba[]? Palette);

    public static Rgba[]? ReadPaletteOnly(ReadOnlySpan<byte> data)
    {
        if (data.Length < 128 || data[0] != 0x0a)
            throw new InvalidDataException("pcx: bad signature");

        int bitsPerPixel = data[3], planes = data[65];
        if (bitsPerPixel == 8 && planes == 1) return ReadPalette(data);
        if ((bitsPerPixel is 1 or 2 or 4) && (planes is >= 1 and <= 4) && bitsPerPixel * planes <= 4)
        {
            int colorCount = 1 << (bitsPerPixel * planes);
            var colors = new Rgba[colorCount];
            for (int i = 0; i < colors.Length; i++)
            {
                int at = 16 + i * 3;
                colors[i] = new Rgba(data[at], data[at + 1], data[at + 2], i == 0 ? (byte)0 : (byte)255);
            }
            return colors;
        }
        throw new NotSupportedException($"pcx: palette extraction does not support {bitsPerPixel}-bit, {planes}-plane images");
    }

    public static Image Decode(ReadOnlySpan<byte> data, bool samePalette = false)
    {
        if (data.Length < 128 || data[0] != 0x0a)
            throw new InvalidDataException("pcx: bad signature");

        int encoding = data[2], bitsPerPixel = data[3];
        int xmin = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        int ymin = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        int xmax = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        int ymax = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        int planes = data[65];
        int bytesPerLine = BinaryPrimitives.ReadUInt16LittleEndian(data[66..]);

        if (bitsPerPixel != 8 || planes != 1)
            throw new NotSupportedException($"pcx: {bitsPerPixel}-bit, {planes}-plane images are not supported");

        int width = xmax - xmin + 1, height = ymax - ymin + 1;
        if (width <= 0 || height <= 0) throw new InvalidDataException("pcx: zero size");
        if (bytesPerLine <= 0) bytesPerLine = width;

        var pixels = new byte[width * height];
        var row = new byte[bytesPerLine];
        int pixelDataEnd = data.Length;
        // A same-palette sprite stores no palette, so a 0x0c 769 bytes from the end is pixel data.
        if (!samePalette)
            pixelDataEnd -= data.Length >= 769 && data[^769] == 0x0c ? 769 : data.Length >= 768 + 128 ? 768 : 0;
        int p = 128;

        for (int y = 0; y < height; y++)
        {
            int x = 0;
            while (x < bytesPerLine)
            {
                if (p >= pixelDataEnd) throw new InvalidDataException("pcx: truncated image data");
                byte v = data[p++];
                if (encoding == 1 && (v & 0xc0) == 0xc0)
                {
                    int count = v & 0x3f;
                    if (p >= pixelDataEnd) throw new InvalidDataException("pcx: truncated image data");
                    byte value = data[p++];
                    for (int k = 0; k < count && x < bytesPerLine; k++) row[x++] = value;
                }
                else row[x++] = v;
            }
            row.AsSpan(0, Math.Min(width, bytesPerLine)).CopyTo(pixels.AsSpan(y * width));
        }

        return new Image(width, height, pixels, ReadPalette(data, samePalette));
    }

    /// <summary>
    /// The 256-color table sits in the last 769 bytes, introduced by 0x0c.
    /// Not every writer emits that marker though, and SFFv1 readers in the wild
    /// simply take the last 768 bytes of the subfile, so that is the fallback.
    /// Sprites reusing the previous sprite's palette have none at all.
    /// </summary>
    private static Rgba[]? ReadPalette(ReadOnlySpan<byte> data, bool samePalette = false)
    {
        ReadOnlySpan<byte> table;
        if (data.Length >= 769 && data[^769] == 0x0c) table = data[^768..];
        else if (!samePalette && data.Length >= 768 + 128) table = data[^768..]; // must not eat the header
        else return null;

        var pal = new Rgba[256];
        for (int i = 0; i < 256; i++)
            pal[i] = new Rgba(table[i * 3], table[i * 3 + 1], table[i * 3 + 2], i == 0 ? (byte)0 : (byte)255);
        return pal;
    }
}
