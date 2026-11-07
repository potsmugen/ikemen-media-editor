namespace Sff;

public static class PaletteDepth
{
    public static readonly int[] SupportedColorCounts = [2, 4, 8, 16, 32, 64, 128, 256];

    /// <summary>The depth a palette of this many colors stores, 1-8 bits; 0 when the count is not one of the supported depths.</summary>
    public static int BitsForColorCount(int colors) => Array.IndexOf(SupportedColorCounts, colors) + 1;

    public static bool CanConvert(SffFile file, Palette palette, int colors)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(palette);
        if (!SupportedColorCounts.Contains(colors) || SffVersions.IsV1(file.Version)
            || palette.Source != PaletteSource.Embedded || palette.LinksTo is not null
            || !SupportedColorCounts.Contains(palette.Colors.Length) || palette.Colors.Length == colors
            || !file.Palettes.Contains(palette))
            return false;

        // Shrinking may only cut padding, meaning entries past the new size that all repeat one color
        if (colors < palette.Colors.Length && palette.Colors[colors..].Any(c => c != palette.Colors[^1]))
            return false;

        return file.Sprites
            .Where(s => ReferenceEquals(s.Palette?.LinksTo ?? s.Palette, palette))
            .All(s => s.Indexed is { } indices
                ? indices.All(index => index < colors)
                : s.DecodeError is null);
    }

    public static Rgba[] Convert(Palette palette, int colors)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (!SupportedColorCounts.Contains(colors)) throw new ArgumentOutOfRangeException(nameof(colors));
        if (!SupportedColorCounts.Contains(palette.Colors.Length) || palette.Colors.Length == colors)
            throw new InvalidOperationException("The palette is not at a different supported color depth.");

        var converted = new Rgba[colors];
        Array.Copy(palette.Colors, converted, Math.Min(palette.Colors.Length, colors));
        if (colors > palette.Colors.Length)
            Array.Fill(converted, new Rgba(0, 0, 0, 255), palette.Colors.Length, colors - palette.Colors.Length);
        return converted;
    }
}
