using System.Globalization;
using System.Text;

namespace Sff;

/// <summary>Writes a Paint Shop Pro JASC-PAL text palette, index 0 first (the order PaletteFileImport reads).</summary>
public static class JascPalFile
{
    public static string Write(Palette palette)
    {
        int count = Math.Min(palette.Colors.Length, 256);
        var sb = new StringBuilder();
        sb.Append("JASC-PAL\r\n0100\r\n").Append(count).Append("\r\n");
        for (int i = 0; i < count; i++)
        {
            var c = palette.Colors[i];
            sb.Append(CultureInfo.InvariantCulture, $"{c.R} {c.G} {c.B}\r\n");
        }
        return sb.ToString();
    }

    public static void Save(Palette palette, string path) =>
        File.WriteAllText(path, Write(palette), new UTF8Encoding(false));
}
