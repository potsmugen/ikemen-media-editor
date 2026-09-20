using System.Globalization;
using System.Text;

namespace Sff;

public static class PaletteRgbaFile
{
    private const string Comment = "# RGBA palette; colors are listed by index as R G B A values from 0 to 255.";
    private const string Magic = "IKEMEN-RGBA";
    private const string Version = "1";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string Write(Palette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (palette.Colors.Length is < 1 or > 256)
            throw new InvalidDataException("An RGBA palette must contain from 1 to 256 colors.");

        var sb = new StringBuilder();
        sb.Append(Comment).Append('\n').Append(Magic).Append('\n').Append(Version).Append('\n')
            .Append(palette.Colors.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var color in palette.Colors)
            sb.Append(color.R.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(color.G.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(color.B.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(color.A.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    public static Palette Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        string text;
        try
        {
            text = Utf8.GetString(data).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The RGBA palette is not valid UTF-8.", ex);
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
        if (lines.Length < 4 || lines.Any(string.IsNullOrWhiteSpace)
            || lines[0] != Comment || lines[1] != Magic || lines[2] != Version)
            throw new InvalidDataException("This is not a supported RGBA palette file (expected the RGBA comment, IKEMEN-RGBA, and version 1).");
        if (!int.TryParse(lines[3], NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count is < 1 or > 256)
            throw new InvalidDataException("The RGBA palette color count must be from 1 to 256.");
        if (lines.Length != count + 4)
            throw new InvalidDataException($"The RGBA palette header says {count} colors, but the file contains {Math.Max(0, lines.Length - 4)} color lines.");

        var colors = new Rgba[count];
        for (int i = 0; i < count; i++)
        {
            var parts = lines[i + 4].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4
                || !byte.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out byte r)
                || !byte.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out byte g)
                || !byte.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out byte b)
                || !byte.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out byte a))
                throw new InvalidDataException($"RGBA palette color {i} must contain four channel values from 0 to 255.");
            colors[i] = new Rgba(r, g, b, a);
        }
        return new Palette { Colors = colors };
    }

    public static Palette Open(string path) => Read(File.ReadAllBytes(path));

    public static void Save(Palette palette, string path) =>
        File.WriteAllText(path, Write(palette), Utf8);
}
