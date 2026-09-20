using System.Globalization;
using System.Text;

namespace Sff;

/// <summary>Reads standalone indexed palette files into the editor's SFF color order.</summary>
public static class PaletteFileImport
{
    public static Palette Read(string path)
    {
        var data = File.ReadAllBytes(path);
        return Read(path, data);
    }

    public static Palette Read(string path, byte[] data)
    {
        bool actReversed = !Path.GetExtension(path).Equals(".act", StringComparison.OrdinalIgnoreCase)
            || ActFile.LooksReversed(data);
        Palette palette = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".act" => ActFile.Read(data, actReversed),
            ".pal" => ReadJascPal(data),
            ".png" => ReadPng(data),
            ".pcx" => ReadPcx(data),
            ".rgba" => PaletteRgbaFile.Read(data),
            var extension => throw new NotSupportedException($"Palette files with extension '{extension}' are not supported."),
        };
        palette.Source = PaletteSource.Embedded;
        palette.SourcePath = null;
        palette.SourceActReversed = actReversed;
        if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".pcx", StringComparison.OrdinalIgnoreCase))
            palette.SourceData = (byte[])data.Clone();
        return palette;
    }

    private static Palette ReadJascPal(byte[] data)
    {
        string text = Encoding.UTF8.GetString(data).TrimStart('\uFEFF');
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 3 || !string.Equals(lines[0], "JASC-PAL", StringComparison.OrdinalIgnoreCase)
            || lines[1] != "0100")
            throw new InvalidDataException("This is not a Paint Shop Pro JASC-PAL file (expected JASC-PAL and version 0100).");
        if (!int.TryParse(lines[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count is < 1 or > 256)
            throw new InvalidDataException("The JASC-PAL color count must be from 1 to 256.");
        if (lines.Length < count + 3)
            throw new InvalidDataException($"The JASC-PAL header says {count} colors, but the file contains only {Math.Max(0, lines.Length - 3)} color lines.");

        var colors = new Rgba[count];
        for (int i = 0; i < count; i++)
        {
            var parts = lines[i + 3].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte r)
                || !byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte g)
                || !byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte b))
                throw new InvalidDataException($"JASC-PAL color {i} must contain three RGB values from 0 to 255.");
            colors[i] = new Rgba(r, g, b, i == 0 ? (byte)0 : (byte)255);
        }
        return new Palette { Colors = colors };
    }

    private static Palette ReadPng(byte[] data)
    {
        var image = Png.Decode(data);
        if (image.Indices is null || image.Plte is null)
            throw new InvalidDataException("Only indexed PNG files contain a palette to import.");
        return new Palette { Colors = image.Plte };
    }

    private static Palette ReadPcx(byte[] data)
    {
        var colors = Pcx.ReadPaletteOnly(data);
        if (colors is null)
            throw new InvalidDataException("This PCX file does not contain an embedded palette.");
        return new Palette { Colors = colors };
    }
}
