namespace Sff;

/// <summary>
/// Builds one palette from the visible colors of several PNG or PCX images.
/// Index 0 is the mask; frequent colors come first, and over 255 colors are reduced by median cut.
/// </summary>
public sealed class PaletteFromImages
{
    private readonly Dictionary<uint, int> _lookup = [];
    private readonly List<Rgba> _colors = [];
    private readonly List<long> _weights = [];

    /// <summary>Visible colors counted so far.</summary>
    public int ColorCount => _colors.Count;

    /// <summary>Adds an image's visible pixels; pixels below the transparent alpha threshold are skipped.</summary>
    public void Add(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var bgra = ImageImport.ToBgra(ImageImport.Decode(image));
        for (int at = 0; at + 3 < bgra.Length; at += 4)
        {
            byte b = bgra[at], g = bgra[at + 1], r = bgra[at + 2], a = bgra[at + 3];
            if (a < ImageImport.TransparentAlphaThreshold) continue;
            uint key = (uint)(r << 16 | g << 8 | b);
            if (_lookup.TryGetValue(key, out int index)) _weights[index]++;
            else
            {
                _lookup.Add(key, _colors.Count);
                _colors.Add(new Rgba(r, g, b, 255));
                _weights.Add(1);
            }
        }
    }

    /// <summary>The 256-color palette; Reduced says whether median cut merged colors.</summary>
    public (Palette Palette, bool Reduced) Build()
    {
        if (_colors.Count == 0) throw new InvalidOperationException("no visible colors to build a palette from");
        var mask = new Rgba(0, 0, 0, 0);
        bool reduced = _colors.Count > GifImport.MaxSharedColors;
        Palette palette;
        if (reduced)
            palette = Quantize.Reduce([mask, .. _colors], [0, .. _weights], GifImport.MaxSharedColors);
        else
        {
            // Stable sort: equal counts keep first-seen order.
            var order = Enumerable.Range(0, _colors.Count).OrderByDescending(i => _weights[i]);
            palette = new Palette { Colors = [mask, .. order.Select(i => _colors[i])] };
        }
        return (ImageImport.PadTo256(palette), reduced);
    }
}
