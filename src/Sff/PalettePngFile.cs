namespace Sff;

/// <summary>Writes a palette as an indexed PNG swatch sheet: 16×16 grid, color 0 top-left, alpha via tRNS (PaletteFileImport reads it back).</summary>
public static class PalettePngFile
{
    public static byte[] Write(Palette palette, int cell)
    {
        var colors = new Rgba[256];
        for (int i = 0; i < 256; i++) colors[i] = i < palette.Colors.Length ? palette.Colors[i] : default;
        int size = 16 * cell;
        var indices = new byte[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                indices[y * size + x] = (byte)(y / cell * 16 + x / cell);
        return Png.EncodeIndexed(indices, size, size, colors);
    }

    public static void Save(Palette palette, string path, int cell) => File.WriteAllBytes(path, Write(palette, cell));
}
