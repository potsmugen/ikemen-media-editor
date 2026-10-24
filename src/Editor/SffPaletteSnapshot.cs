using Sff;

namespace Editor;

/// <summary>The SFF's own palette tables as last loaded or saved, so the Palettes tab only shows '*' when they changed.</summary>
internal sealed class SffPaletteSnapshot
{
    private readonly (Palette Palette, int Group, int Index, Palette? LinksTo, Rgba[] Colors)[] _items;

    private SffPaletteSnapshot(SffFile file) =>
        _items = Embedded(file).Select(p => (p, p.Group, p.Index, p.LinksTo, (Rgba[])p.Colors.Clone())).ToArray();

    public static SffPaletteSnapshot Take(SffFile file) => new(file);

    /// <summary>Palette set, order, keys, links or colors differ; sprite assignments don't count.</summary>
    public bool Differs(SffFile file)
    {
        int i = 0;
        foreach (var p in Embedded(file))
        {
            if (i >= _items.Length) return true;
            var s = _items[i++];
            if (!ReferenceEquals(p, s.Palette) || p.Group != s.Group || p.Index != s.Index
                || !ReferenceEquals(p.LinksTo, s.LinksTo) || !p.Colors.AsSpan().SequenceEqual(s.Colors))
                return true;
        }
        return i != _items.Length;
    }

    private static IEnumerable<Palette> Embedded(SffFile file) =>
        file.Palettes.Where(p => p.Source == PaletteSource.Embedded);
}
