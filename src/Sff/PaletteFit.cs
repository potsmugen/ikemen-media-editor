namespace Sff;

/// <summary>Which palettes reproduce a batch of images exactly, by index or by color. Colors
/// compare as full RGBA, as duplicate palettes do; index 0 is the mask and never counts.</summary>
public sealed class PaletteFit
{
    /// <summary>One image: its own colors by index (indexed only), the indexes it uses, its visible colors
    /// (null when over 256), and whether it has no transparent pixels.</summary>
    private sealed record Entry(Rgba[]? Own, int[]? Used, HashSet<uint>? Colors, bool Opaque = false);

    private readonly List<Entry?> _images = [];

    /// <summary>Images in the batch, including any that could not be described.</summary>
    public int Count => _images.Count;

    /// <summary>A null sprite, or one without pixels, never matches.</summary>
    public PaletteFit(IEnumerable<Sprite?> sprites)
    {
        ArgumentNullException.ThrowIfNull(sprites);
        foreach (var s in sprites) _images.Add(Describe(s));
    }

    /// <summary>How many images the palette reproduces exactly, by index or by nearest color.</summary>
    public int Matches(Palette palette, bool keepIndexes)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var colors = palette.Colors;
        HashSet<uint>? usable = null;
        int count = 0;
        foreach (var e in _images)
        {
            if (e is null) continue;
            if (keepIndexes)
            {
                if (e.Own is { } own && e.Used is { } used && SameAt(own, colors, used)) count++;
                continue;
            }
            if (e.Colors is not { } wanted) continue;
            usable ??= Usable(colors);
            if (usable.IsSupersetOf(wanted)) count++;
            // Matches ImageImport: in an image without transparency, index 0's RGB is the mask.
            else if (e.Opaque && colors.Length > 0
                && wanted.All(c => usable.Contains(c) || (c & 0xFFFFFF) == (Key(colors[Sprite.TransparentIndex]) & 0xFFFFFF)))
                count++;
        }
        return count;
    }

    private static Entry? Describe(Sprite? s)
    {
        if (s is null || !s.HasPixels) return null;
        int threshold = ImageImport.TransparentAlphaThreshold;
        if (s.Indexed is { } indexed && s.Palette is { } pal)
        {
            var seen = new bool[256];
            foreach (var i in indexed) seen[i] = true;
            seen[Sprite.TransparentIndex] = false;
            var used = Enumerable.Range(0, 256).Where(i => seen[i]).ToArray();
            var colors = new HashSet<uint>();
            foreach (var i in used)
                if (i < pal.Colors.Length && pal.Colors[i].A >= threshold) colors.Add(Key(pal.Colors[i]));
            return new Entry(pal.Colors, used, colors);
        }
        if (s.Direct is { } bgra)
        {
            var colors = new HashSet<uint>();
            bool transparent = false;
            for (int i = 0; i + 3 < bgra.Length; i += 4)
            {
                if (bgra[i + 3] < threshold) { transparent = true; continue; }
                colors.Add(Key(new Rgba(bgra[i + 2], bgra[i + 1], bgra[i], bgra[i + 3])));
                if (colors.Count > 256) return new Entry(null, null, null); // more than any palette holds, mask included
            }
            return new Entry(null, null, colors, Opaque: !transparent);
        }
        return null;
    }

    private static bool SameAt(Rgba[] own, Rgba[] candidate, int[] used)
    {
        foreach (var i in used)
        {
            if (i >= own.Length || i >= candidate.Length) return false;
            if (Key(own[i]) != Key(candidate[i])) return false;
        }
        return true;
    }

    /// <summary>Colors nearest-color mapping can land on: every entry but the mask.</summary>
    private static HashSet<uint> Usable(Rgba[] colors)
    {
        var set = new HashSet<uint>();
        for (int i = 1; i < colors.Length && i < 256; i++) set.Add(Key(colors[i]));
        return set;
    }

    private static uint Key(Rgba c) => (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);
}
