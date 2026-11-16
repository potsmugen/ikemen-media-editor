namespace Sff;

/// <summary>
/// Converts sprites between indexed and 32-bit color without the user picking a palette
/// </summary>
public static class SpriteDepth
{
    /// <summary>
    /// An indexed sprite's pixels as 32-bit BGRA, drawn exactly as the editor shows them:
    /// every index takes its palette color and alpha
    /// </summary>
    public static byte[] ToTrueColor(Sprite sprite, Palette palette)
    {
        ArgumentNullException.ThrowIfNull(sprite);
        ArgumentNullException.ThrowIfNull(palette);
        if (sprite.Indexed is null) throw new InvalidOperationException("the sprite is not indexed");
        var bgra = sprite.ToBgra(palette);
        // Clear pixels keep no color, as the decoder does for true color, so it can't bleed when scaled
        for (int at = 0; at + 3 < bgra.Length; at += 4)
            if (bgra[at + 3] == 0) bgra[at] = bgra[at + 1] = bgra[at + 2] = 0;
        return bgra;
    }

    /// <summary>
    /// Builds one palette from the visible colors of the given BGRA pixel sets and matches each set to it
    /// Same rules as Create From Images: index 0 is the mask, over 255 colors are reduced
    /// </summary>
    public static (Palette Palette, bool Reduced, byte[][] Indexes) ToIndexed(IReadOnlyList<byte[]> bgras)
    {
        ArgumentNullException.ThrowIfNull(bgras);
        var builder = new PaletteFromImages();
        foreach (var bgra in bgras) builder.AddBgra(bgra);
        // A fully clear sprite has no colors to collect; it gets a palette holding only the mask
        var (palette, reduced) = builder.ColorCount > 0
            ? builder.Build()
            : (ImageImport.PadTo256(new Palette { Colors = [new Rgba(0, 0, 0, 0)] }), false);
        var indexes = bgras.Select(bgra => ImageImport.MatchToPalette(bgra, palette.Colors)).ToArray();
        return (palette, reduced, indexes);
    }
}
