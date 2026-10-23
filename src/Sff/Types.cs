namespace Sff;

/// <summary>A color table entry. Alpha comes from the fourth on-disk byte.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A);

/// <summary>An SFF version quad, printed as hi.lo1.lo2.lo3.</summary>
public readonly record struct SffVersion(byte Hi, byte Lo1, byte Lo2, byte Lo3)
{
    public override string ToString() => $"{Hi}.{Lo1}.{Lo2}.{Lo3}";
}

/// <summary>The on-disk pixel encoding of a sprite (sprite node "fmt").</summary>
public enum SpriteFormat : byte
{
    Raw = 0,
    Link = 1,
    Rle8 = 2,
    Rle5 = 3,
    Lz5 = 4,
    Png8 = 10,
    Png24 = 11,
    Png32 = 12,

    /// <summary>
    /// Not an on-disk id. SFFv1 has no format field because every sprite is a
    /// PCX; this labels those so the editor can say where a sprite came from.
    /// </summary>
    Pcx = 200,
}

/// <summary>What a given SFF version is able to store.</summary>
public static class SffVersions
{
    /// <summary>The versions this library can read.</summary>
    public static readonly SffVersion V1 = new(1, 0, 1, 0);      // 8-bit only
    public static readonly SffVersion V2 = new(2, 0, 0, 0);      // 5 and 8-bit only (MUGEN 1.0)
    public static readonly SffVersion V2_1 = new(2, 0, 1, 0);    // adds 24/32-bit and PNG (MUGEN 1.1)

    /// <summary>True when the version can store the PNG sprite formats.</summary>
    public static bool SupportsPng(SffVersion v) => v.Hi == 2 && (v.Lo1 > 0 || v.Lo2 >= 1);

    /// <summary>
    /// True for SFFv1. On disk it has no separate per-sprite palette resource —
    /// a sprite either carries its own embedded palette or says "same as
    /// whoever came before me". The loader resolves that chain into direct
    /// object references up front, so two sprites sharing one Palette object
    /// in memory reflects a real on-disk relationship, not a coincidence the
    /// way it can be in v2.
    /// </summary>
    public static bool IsV1(SffVersion v) => v.Hi == 1;
}

/// <summary>Whether a palette is stored in the SFF or imported from an external file.</summary>
public enum PaletteSource
{
    /// <summary>
    /// Stored inside the sprite file: the palette table in SFFv2, or carried by
    /// a sprite's PCX in SFFv1. Saving the file saves these with it.
    /// </summary>
    Embedded,

    /// <summary>
    /// Loaded from a separate palette file. External palettes stay outside the
    /// SFF until a sprite uses them.
    /// </summary>
    External,

    /// <summary>Compatibility name for ACT palettes from earlier versions.</summary>
    Act = External,
}

/// <summary>
/// A decoded color table. The spec calls the fourth byte of each entry unused,
/// but real files store alpha there, so it is read as alpha.
/// </summary>
public sealed class Palette
{
    public int Group;
    public int Index;

    private Rgba[] _colors = [];

    /// <summary>The color table. A linked palette reads and writes its target's.</summary>
    public Rgba[] Colors
    {
        get => LinksTo?.Colors ?? _colors;
        set { if (LinksTo is { } target) target.Colors = value; else _colors = value; }
    }

    /// <summary>
    /// SFFv2 palette link: the palette whose colors this one uses (on disk a
    /// node with no data and the target's number). A real link: editing either
    /// changes both, and the writer writes it back as a link. Never a chain
    /// back to itself; the reader refuses those.
    /// </summary>
    public Palette? LinksTo;

    /// <summary>Whether this table lives in the sprite file or beside it.</summary>
    public PaletteSource Source = PaletteSource.Embedded;

    /// <summary>The source file, when this palette was imported.</summary>
    public string? SourcePath;

    public byte[]? SourceData;
    public bool SourceActReversed = true;

    /// <summary>
    /// Editor bookkeeping: set once the colors have been changed since the
    /// palette was loaded or saved. External palettes use it — embedded ones
    /// are covered by the file's own dirty state — and the writers ignore it.
    /// </summary>
    public bool Dirty;

    /// <summary>
    /// The engine treats group 1 as the character's own palettes, numbered 1 to
    /// 12, which is what a .act file supplies for an SFFv1 character.
    /// </summary>
    public bool IsCharacterPalette => Group == 1;

    /// <summary>A short label for lists and menus.</summary>
    public string Describe() =>
        (Source == PaletteSource.External
            ? $"{(System.IO.Path.GetExtension(SourcePath) is { Length: > 0 } ext ? ext : SourcePath is null ? "temp" : "external")}  {(Dirty ? "*" : "")}{System.IO.Path.GetFileName(SourcePath) ?? "new palette"}"
            : IsCharacterPalette ? $"pal {Index}" : $"{Group},{Index}")
        + $"  ({Colors.Length} colors)";

    /// <summary>Lowest and highest alpha in the table, for display.</summary>
    public (byte Lo, byte Hi) AlphaRange()
    {
        if (Colors.Length == 0) return (255, 255);
        byte lo = 255, hi = 0;
        foreach (var c in Colors)
        {
            if (c.A < lo) lo = c.A;
            if (c.A > hi) hi = c.A;
        }
        return (lo, hi);
    }
}

/// <summary>
/// The editor's asset representation. It deliberately does not mirror the SFF
/// node layout; SFF is treated as an import/export format.
/// </summary>
public sealed class Sprite
{
    /// <summary>The palette entry MUGEN and Ikemen treat as the transparent mask.</summary>
    public const int TransparentIndex = 0;

    public int Group;
    public int Index;
    public int AxisX;
    public int AxisY;

    // A linked sprite reads its bitmap and size from the sprite it shares with,
    // so it can never hold a stale copy when that one changes. Writes while
    // linked land in its own (unused) fields, so restoring an old snapshot
    // can never overwrite the target; Unshare takes a real copy.
    private int _width, _height;
    private byte[]? _indexed, _direct;

    public int Width { get => SharesWith?.Width ?? _width; set => _width = value; }
    public int Height { get => SharesWith?.Height ?? _height; set => _height = value; }

    /// <summary>Width*Height palette indices, row-major. Null for true-color sprites.</summary>
    public byte[]? Indexed { get => SharesWith is { } t ? t.Indexed : _indexed; set => _indexed = value; }

    /// <summary>Width*Height*4 BGRA pixels. Null for indexed sprites.</summary>
    public byte[]? Direct { get => SharesWith is { } t ? t.Direct : _direct; set => _direct = value; }

    public Palette? Palette;

    // Provenance, kept so the writer can report what the sprite came from.
    public SpriteFormat SrcFormat;
    public byte SrcDepth;

    /// <summary>
    /// The sprite this one borrows its pixels from, or null. Held as a reference
    /// rather than a list position so inserting or removing sprites cannot
    /// silently repoint a link; positions are only computed when writing.
    /// </summary>
    public Sprite? SharesWith;

    /// <summary>Set when this sprite's pixel data could not be decoded.</summary>
    public string? DecodeError;

    /// <summary>
    /// The sprite's bytes exactly as they were on disk, including the 4-byte
    /// length prefix. The writer reuses them verbatim while PixelsDirty is
    /// false, so saving a file nobody has repainted reproduces the original
    /// compressed data rather than re-encoding it.
    /// </summary>
    internal byte[]? SourceData;

    /// <summary>Set once the pixels differ from what was read, forcing a re-encode.</summary>
    public bool PixelsDirty;

    public bool HasPixels => Indexed is not null || Direct is not null;

    /// <summary>
    /// Stops sharing and takes a private copy of the pixels, so editing this
    /// sprite no longer changes the one it was linked to.
    /// </summary>
    public void Unshare()
    {
        if (SharesWith is not { } target) return;
        var (indexed, direct, w, h) = (target.Indexed?.ToArray(), target.Direct?.ToArray(), target.Width, target.Height);
        SharesWith = null;
        (Indexed, Direct, Width, Height) = (indexed, direct, w, h);
        PixelsDirty = true; // it no longer has on-disk bytes of its own
    }

    /// <summary>
    /// Clears links that loop back on themselves (a file can say A links to B
    /// and B to A), marking those sprites undecodable. Readers call this before
    /// anything reads a linked sprite's pixels, which would otherwise recurse.
    /// </summary>
    internal static void BreakLinkLoops(List<Sprite> sprites)
    {
        foreach (var s in sprites)
        {
            int steps = 0;
            for (var t = s.SharesWith; t is not null && steps <= sprites.Count; t = t.SharesWith) steps++;
            if (steps <= sprites.Count) continue;
            s.SharesWith = null;
            s.DecodeError = "sprite links form a loop";
        }
    }

    /// <summary>For SffFile.Clone: this sprite's own fields, no link, a given palette.</summary>
    internal Sprite CopyOwnFields(Palette? palette) => new()
    {
        Group = Group, Index = Index, AxisX = AxisX, AxisY = AxisY,
        _width = _width, _height = _height, _indexed = _indexed?.ToArray(), _direct = _direct?.ToArray(),
        Palette = palette, SrcFormat = SrcFormat, SrcDepth = SrcDepth,
        DecodeError = DecodeError, SourceData = SourceData, PixelsDirty = PixelsDirty,
    };

    /// <summary>
    /// Resolves the sprite to BGRA, the layout a WriteableBitmap wants.
    /// Index 0 is transparent, and any other index can also render invisible
    /// via its own palette alpha byte (SFF palette byte 4 — "unused" per the
    /// spec, but real alpha in practice). showMask forces every pixel opaque,
    /// so the editor can show what's actually there under either kind of
    /// transparency, not just the designated transparent index.
    /// </summary>
    public byte[] ToBgra(Palette? pal, bool showMask = false)
    {
        var dst = new byte[Width * Height * 4];
        if (Direct is not null)
        {
            Array.Copy(Direct, dst, Math.Min(Direct.Length, dst.Length));
            if (showMask)
                for (int i = 3; i < dst.Length; i += 4) dst[i] = 255; // force every pixel's alpha byte opaque
            return dst;
        }
        if (Indexed is null) return dst;

        var colors = pal?.Colors ?? [];
        for (int i = 0; i < Indexed.Length && i * 4 + 3 < dst.Length; i++)
        {
            int idx = Indexed[i];
            if (idx == TransparentIndex && !showMask) continue;

            var c = idx < colors.Length ? colors[idx] : default;
            byte a = showMask ? (byte)255 : c.A;
            dst[i * 4 + 0] = c.B;
            dst[i * 4 + 1] = c.G;
            dst[i * 4 + 2] = c.R;
            dst[i * 4 + 3] = a;
        }
        return dst;
    }
}

/// <summary>A decoded SFF container.</summary>
public sealed class SffFile
{
    public SffVersion Version;
    public SffVersion CompatVersion;
    /// <summary>Read from SFFv1 header byte 32 (SprMaker "Shared palette"), only to tell the
    /// user; saving always writes the standard "use previous palette" format.</summary>
    public bool V1SharedPalette;
    /// <summary>Links read pointing at a later sprite, which Ikemen GO loads as blank. Saving turns
    /// them around (SffWriter.LinkTargets), so the editor warns; cleared once saved.</summary>
    public List<Sprite> ForwardLinksOnLoad = [];
    public List<Sprite> Sprites = [];
    public List<Palette> Palettes = [];

    public static SffFile Open(string path) => SffReader.Decode(File.ReadAllBytes(path));

    public void Save(string path) => SffWriter.Save(this, path);

    /// <summary>Writes the file in a chosen version rather than the default.</summary>
    public void Save(string path, SffVersion version, bool clamp = false) => SffWriter.Save(this, path, version, clamp);

    /// <summary>
    /// A deep copy: new sprites and palettes with their own pixel arrays, links
    /// and palette references pointing inside the copy. On-disk bytes are
    /// shared (never modified), so untouched sprites still write back as-is.
    /// </summary>
    public SffFile Clone()
    {
        var pal = new Dictionary<Palette, Palette>(Palettes.Count);
        Palette Map(Palette p)
        {
            if (pal.TryGetValue(p, out var c)) return c;
            c = new Palette
            {
                Group = p.Group, Index = p.Index, Source = p.Source, SourcePath = p.SourcePath, Dirty = p.Dirty,
                Colors = p.LinksTo is null ? p.Colors.ToArray() : Array.Empty<Rgba>(),
            };
            pal[p] = c;
            if (p.LinksTo is { } t) c.LinksTo = Map(t);
            return c;
        }

        var copy = new SffFile { Version = Version, CompatVersion = CompatVersion, V1SharedPalette = V1SharedPalette };
        foreach (var p in Palettes) copy.Palettes.Add(Map(p));

        var spr = new Dictionary<Sprite, Sprite>(Sprites.Count);
        foreach (var s in Sprites) spr[s] = s.CopyOwnFields(s.Palette is { } p ? Map(p) : null);
        foreach (var s in Sprites)
        {
            var c = spr[s];
            copy.Sprites.Add(c);
            if (s.SharesWith is { } t && spr.TryGetValue(t, out var ct)) c.SharesWith = ct;
        }
        copy.ForwardLinksOnLoad = [.. ForwardLinksOnLoad.Where(spr.ContainsKey).Select(s => spr[s])];
        return copy;
    }

    /// <summary>The first sprite with the given group and index, or null.</summary>
    public Sprite? Find(int group, int index) =>
        Sprites.FirstOrDefault(s => s.Group == group && s.Index == index);

    /// <summary>Position of that sprite in the list, or -1.</summary>
    public int IndexOf(int group, int index) =>
        Sprites.FindIndex(s => s.Group == group && s.Index == index);

    /// <summary>Position of a sprite in the list, or -1.</summary>
    public int PositionOf(Sprite? s) => s is null ? -1 : Sprites.IndexOf(s);

    /// <summary>
    /// Adds a sprite at a position, or at the end when at is out of range.
    /// Links are references, so nothing else needs adjusting.
    /// </summary>
    public void Insert(int at, Sprite sprite)
    {
        if (at < 0 || at > Sprites.Count) at = Sprites.Count;
        Sprites.Insert(at, sprite);
        EnsurePalette(sprite.Palette);
    }

    /// <summary>Adds a palette to the file if it is not already there.</summary>
    public void EnsurePalette(Palette? palette)
    {
        if (palette is not null && !Palettes.Contains(palette)) Palettes.Add(palette);
    }

    /// <summary>
    /// Removes a sprite. Anything sharing its pixels is given a private copy
    /// first, so removing a sprite never destroys another one's image.
    /// </summary>
    public int Remove(Sprite sprite)
    {
        int detached = 0;
        foreach (var other in Sprites)
        {
            if (!ReferenceEquals(other.SharesWith, sprite)) continue;
            other.Unshare();
            detached++;
        }
        Sprites.Remove(sprite);
        return detached;
    }

    /// <summary>
    /// Points one sprite at another's pixels. Refuses anything that would make a
    /// sprite depend on itself, directly or through a chain.
    /// </summary>
    public bool Share(Sprite sprite, Sprite target)
    {
        for (var t = target; t is not null; t = t.SharesWith)
            if (ReferenceEquals(t, sprite)) return false;
        if (!target.HasPixels) return false;

        sprite.SharesWith = target;
        sprite.Indexed = target.Indexed;
        sprite.Direct = target.Direct;
        sprite.Width = target.Width;
        sprite.Height = target.Height;
        sprite.DecodeError = null;
        sprite.PixelsDirty = false;
        return true;
    }

    /// <summary>
    /// Groups sprites that could be one linked sprite: same size and identical
    /// pixel bytes (indexed with indexed, true color with true color). In v1 a
    /// link takes its target's palette on load unless flagged to use the previous
    /// one, so the palette colors must match too — SprMaker 0.97 -f's rule (whole PCX, palette included, byte for
    /// byte). In v2 a link keeps its own palette, so it is ignored. Undecodable
    /// or empty sprites are left out. Each set lists its members in file order;
    /// only sets with something to change are returned.
    /// </summary>
    public List<List<Sprite>> FindDuplicateSprites() => FindDuplicateSprites(SffVersions.IsV1(Version));

    /// <summary>As FindDuplicateSprites, by v1 or v2 rules whatever this file's version.</summary>
    public List<List<Sprite>> FindDuplicateSprites(bool v1)
    {
        var buckets = new Dictionary<(int W, int H, bool Direct, int Hash), List<List<Sprite>>>();
        var sets = new List<List<Sprite>>();
        foreach (var s in Sprites)
        {
            if (s.DecodeError is not null || (s.Indexed ?? s.Direct) is not { } px) continue;
            var h = new HashCode();
            h.AddBytes(px);
            if (v1) h.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(PaletteColors(s).AsSpan()));
            var key = (s.Width, s.Height, s.Direct is not null, h.ToHashCode());
            if (!buckets.TryGetValue(key, out var candidates)) buckets[key] = candidates = [];
            var set = candidates.FirstOrDefault(c => SamePixels(c[0], px)
                && (!v1 || PaletteColors(c[0]).AsSpan().SequenceEqual(PaletteColors(s))));
            if (set is null) { set = [s]; candidates.Add(set); sets.Add(set); }
            else set.Add(s);
        }
        // A set needs work unless every later member already links straight to the first.
        return sets.Where(c => c.Count > 1
            && (c[0].SharesWith is not null || c.Skip(1).Any(m => !ReferenceEquals(m.SharesWith, c[0])))).ToList();

        static Rgba[] PaletteColors(Sprite a) => a.Palette?.Colors ?? [];

        static bool SamePixels(Sprite a, byte[] px) =>
            (a.Indexed ?? a.Direct) is { } q && (ReferenceEquals(q, px) || q.AsSpan().SequenceEqual(px));
    }

    /// <summary>One sprite's result from <see cref="FindSimilarSprites"/>, with the share of
    /// visible positions that matched, 0 to 100.</summary>
    public readonly record struct SimilarSprite(Sprite Sprite, int Percent);

    /// <summary>
    /// Finds the sprites at least <paramref name="percent"/>% the same as
    /// <paramref name="reference"/>, best match first. The percentage is the share of
    /// positions visible in either drawing whose pixel data match, so 100 means
    /// the drawings are visually identical. Positions transparent in both do not count.
    ///
    /// Each sprite is normalized to the tightest box around its non-mask pixels, so a
    /// cropped and an uncropped copy of one drawing compare equal whatever size their
    /// canvases are. Transparent pixels do not count, and indexed pixels are compared by
    /// their resolved colors. The axis and canvas position take no part in it, which also
    /// means the same drawing placed at the top of one canvas and the bottom of another
    /// is a match. When <paramref name="requireSamePalette"/> is set, palettes must also
    /// match, which matters in SFFv1 where a sprite's colors come from its palette.
    ///
    /// A sprite of a different kind (indexed against true color) never matches: there
    /// is no shared representation to compare. The reference itself, sprites that
    /// borrow their pixels from another (<see cref="Sprite.SharesWith"/>), sprites that
    /// failed to decode and sprites that are entirely mask color are all left out — a
    /// borrower would match its own target trivially, and an empty box would match
    /// everything.
    /// </summary>
    public List<SimilarSprite> FindSimilarSprites(Sprite reference, int percent = 90, bool requireSamePalette = false)
    {
        var matches = new List<SimilarSprite>();
        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;
        if (!Normalized(reference, out var from)) return matches;

        foreach (var s in Sprites)
        {
            if (ReferenceEquals(s, reference) || s.DecodeError is not null || s.SharesWith is not null) continue;
            if (s.DecodeError is not null || !Normalized(s, out var to)) continue;
            if (s.Direct is not null != reference.Direct is not null) continue;
            if (requireSamePalette && !SamePalette(reference, s)) continue;
            int match = MatchPercent(from, to, percent);
            if (match >= percent) matches.Add(new SimilarSprite(s, match));
        }
        // Best first, then file order, so two equally close sprites keep a stable order.
        return [.. matches.OrderByDescending(m => m.Percent)];
    }

    /// <summary>A sprite cropped to its tightest non-mask box. <paramref name="Stride"/> is
    /// bytes per pixel: 1 for an indexed box, 4 for a true-color one.</summary>
    private readonly record struct Box(int Width, int Height, int Stride, byte[] Pixels, int VisibleCount);

    /// <summary>A sprite cropped to its tightest non-mask box, so a crop that only moved
    /// the border doesn't change it. False when there is nothing to compare: no pixels,
    /// or every one of them mask color (an empty box would match everything).</summary>
    private static bool Normalized(Sprite s, out Box box)
    {
        box = default;
        int w = s.Width, h = s.Height;
        if (w <= 0 || h <= 0) return false;
        var indexed = s.Indexed;
        var direct = s.Direct;
        if (indexed is null && direct is null) return false;
        // Width/height and the pixel buffer must agree, or reading the tight box would
        // run off the end (an empty buffer with a real canvas size reaches here).
        if (indexed is not null ? indexed.Length < w * h : direct!.Length < w * h * 4) return false;
        if (!VisibleBounds(s, w, h, out int minX, out int minY, out int maxX, out int maxY, out int visibleCount)) return false;

        int bw = maxX - minX + 1, bh = maxY - minY + 1;
        if (indexed is not null)
        {
            var pixels = new byte[bw * bh];
            for (int y = 0; y < bh; y++)
                Array.Copy(indexed, (minY + y) * w + minX, pixels, y * bw, bw);
            box = new Box(bw, bh, 1, pixels, visibleCount);
            return true;
        }
        var rgba = new byte[bw * bh * 4];
        for (int y = 0; y < bh; y++)
            Array.Copy(direct!, ((minY + y) * w + minX) * 4, rgba, y * bw * 4, bw * 4);
        box = new Box(bw, bh, 4, rgba, visibleCount);
        return true;
    }

    private static bool VisibleBounds(Sprite s, int width, int height, out int minX, out int minY, out int maxX, out int maxY, out int visibleCount)
    {
        minX = width; minY = height; maxX = -1; maxY = -1;
        visibleCount = 0;
        var indexed = s.Indexed;
        var direct = s.Direct;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = y * width + x;
                bool visible = indexed is not null
                    ? indexed[at] != Sprite.TransparentIndex
                    : direct![(at * 4) + 3] != 0;
                if (!visible) continue;
                visibleCount++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX >= 0;
    }

    /// <summary>Same resolved colors, the rule <see cref="FindDuplicateSprites"/> uses in v1.
    /// Index 0's RGB is ignored when both alphas are zero, like the palette comparison.</summary>
    private static bool SamePalette(Sprite a, Sprite b)
    {
        var ca = ResolvedColors(a.Palette);
        var cb = ResolvedColors(b.Palette);
        if (ca.Length != cb.Length) return false;
        for (int i = 0; i < ca.Length; i++)
        {
            if (i == 0 && ca[i].A == 0 && cb[i].A == 0) continue;
            if (ca[i] != cb[i]) return false;
        }
        return true;
    }

    private static Rgba[] ResolvedColors(Palette? p) =>
        p is null ? [] : p.LinksTo is { } target ? ResolvedColors(target) : p.Colors;

    /// <summary>Whole percent of visible positions that agree, comparing the two tight boxes
    /// as one union. Transparent pixels do not count; differing visible pixels do. Stops when
    /// the requested percentage can no longer be reached.</summary>
    private static int MatchPercent(Box a, Box b, int needed)
    {
        if (a.Width == b.Width && a.Height == b.Height
            && a.VisibleCount == a.Width * a.Height && b.VisibleCount == b.Width * b.Height)
        {
            int opaqueCount = a.Width * a.Height;
            int budget = opaqueCount - (int)Math.Ceiling(opaqueCount * needed / 100.0);
            int different = 0;
            for (int i = 0; i < opaqueCount; i++)
            {
                int at = i * a.Stride;
                bool equal = true;
                for (int channel = 0; channel < a.Stride; channel++)
                    if (a.Pixels[at + channel] != b.Pixels[at + channel]) { equal = false; break; }
                if (!equal && ++different > budget) return -1;
            }
            return (int)Math.Floor((opaqueCount - (double)different) * 100 / opaqueCount);
        }

        int width = Math.Max(a.Width, b.Width), height = Math.Max(a.Height, b.Height);
        int count = width * height;
        if (count == 0) return 0;
        int visible = 0, same = 0, remainingVisibleA = a.VisibleCount, remainingVisibleB = b.VisibleCount;
        for (int y = 0; y < height; y++)
        {
            int aRow = y * a.Width * a.Stride, bRow = y * b.Width * b.Stride;
            for (int x = 0; x < width; x++)
            {
                bool inA = y < a.Height && x < a.Width;
                bool inB = y < b.Height && x < b.Width;
                bool maskA = !inA || IsMask(a, aRow, x);
                bool maskB = !inB || IsMask(b, bRow, x);
                if (!maskA) remainingVisibleA--;
                if (!maskB) remainingVisibleB--;
                if (!maskA || !maskB)
                {
                    visible++;
                    if (!maskA && !maskB && SameVisiblePixel(a, aRow, x, b, bRow, y)) same++;
                }

                int possibleMatches = Math.Min(remainingVisibleA, remainingVisibleB);
                if (needed > 0 && (same + possibleMatches) * 100L / (visible + possibleMatches) < needed)
                    return -1;
            }
        }
        return visible == 0 ? 0 : (int)(same * 100L / visible);
    }

    /// <summary>Whether the visible pixel bytes agree.</summary>
    private static bool SameVisiblePixel(Box a, int aRow, int x, Box b, int bRow, int y)
    {
        int at = aRow + x * a.Stride, bt = bRow + x * b.Stride;
        for (int i = 0; i < a.Stride; i++)
            if (a.Pixels[at + i] != b.Pixels[bt + i]) return false;
        return true;
    }

    /// <summary>Whether a pixel inside the box is transparent.</summary>
    private static bool IsMask(Box box, int row, int x)
    {
        int at = row + x * box.Stride;
        return box.Stride == 1 ? box.Pixels[at] == Sprite.TransparentIndex : box.Pixels[at + 3] == 0;
    }

    /// <summary>
    /// Links every later member of each set to its first. If the first borrows
    /// from a later member, it takes over that member's on-disk data first, so
    /// nothing is re-encoded and no loop forms. Anything still linked to a
    /// member is repointed at the first, so no chains are left. Returns how
    /// many sprites got a new link target.
    /// In v1 each relinked sprite is also pointed at the first's palette object,
    /// as a reload would do; a palette that nothing uses afterwards is removed
    /// from the file's list (only ones this call displaced).
    /// </summary>
    public int ShareDuplicateSprites(IEnumerable<List<Sprite>> sets) =>
        ShareDuplicateSprites(sets, SffVersions.IsV1(Version));

    /// <summary>As ShareDuplicateSprites, by v1 or v2 rules whatever this file's version.</summary>
    public int ShareDuplicateSprites(IEnumerable<List<Sprite>> sets, bool v1)
    {
        var displaced = new HashSet<Palette>();
        int linked = 0;
        foreach (var set in sets)
        {
            var root = set[0];
            if (root.SharesWith is not null)
            {
                var owner = root.SharesWith;
                while (owner.SharesWith is not null) owner = owner.SharesWith;
                if (!set.Contains(owner)) continue; // cannot happen for identical pixels; skip rather than guess
                var (indexed, direct, w, h) = (owner.Indexed, owner.Direct, owner.Width, owner.Height);
                root.SharesWith = null;
                (root.Indexed, root.Direct, root.Width, root.Height) = (indexed, direct, w, h);
                root.SourceData = owner.SourceData;
                root.SrcFormat = owner.SrcFormat;
                root.SrcDepth = owner.SrcDepth;
                root.PixelsDirty = owner.PixelsDirty;
            }

            var members = new HashSet<Sprite>(set.Skip(1));
            foreach (var s in Sprites)
            {
                bool member = members.Contains(s);
                if (!member && (s.SharesWith is null || !members.Contains(s.SharesWith))) continue;
                if (ReferenceEquals(s.SharesWith, root)) continue;
                s.SharesWith = root;
                s.Indexed = root.Indexed;
                s.Direct = root.Direct;
                s.Width = root.Width;
                s.Height = root.Height;
                s.PixelsDirty = false;
                if (v1 && !ReferenceEquals(s.Palette, root.Palette))
                {
                    if (s.Palette is not null) displaced.Add(s.Palette);
                    s.Palette = root.Palette;
                }
                if (member) linked++;
            }
        }
        if (displaced.Count > 0)
        {
            var used = Sprites.Select(x => x.Palette).OfType<Palette>().ToHashSet();
            Palettes.RemoveAll(p => displaced.Contains(p) && !used.Contains(p));
        }
        return linked;
    }

    /// <summary>Every sprite's pixel, link and palette state plus the palette list, for undoing bulk link changes.</summary>
    public PixelLinkSnapshot SnapshotPixelLinks() => new(this);

    /// <summary>
    /// Finds a palette already in the file whose colors match exactly — same
    /// colors, same order — so an import can share it instead of adding a
    /// duplicate.
    /// </summary>
    public Palette? FindIdenticalPalette(Palette palette) =>
        Palettes.FirstOrDefault(p => p.Colors.AsSpan().SequenceEqual(palette.Colors));

    /// <summary>
    /// Groups palettes whose resolved colors match. By default, index 0's RGB
    /// is ignored when both alpha values are zero; set
    /// <paramref name="ignoreTransparentMaskRgb"/> to false for exact RGBA matching.
    /// The first palette in file order is kept; only duplicate sets are returned.
    /// </summary>
    public List<List<Palette>> FindDuplicatePalettes(bool ignoreTransparentMaskRgb = true) =>
        FindDuplicatePalettes(Palettes, ignoreTransparentMaskRgb);

    /// <summary>Finds duplicate palettes within the supplied collection.</summary>
    public List<List<Palette>> FindDuplicatePalettes(
        IEnumerable<Palette> palettes, bool ignoreTransparentMaskRgb = true)
    {
        var buckets = new Dictionary<(int Count, int Hash), List<List<Palette>>>();
        var sets = new List<List<Palette>>();
        foreach (var p in palettes)
        {
            var colors = ResolveColors(p);
            if (colors.Length == 0) continue;
            var h = new HashCode();
            h.Add(colors.Length);
            for (int i = 0; i < colors.Length; i++)
            {
                var c = colors[i];
                if (ignoreTransparentMaskRgb && i == 0 && c.A == 0)
                    c = c with { R = 0, G = 0, B = 0 };
                h.Add(c);
            }
            var key = (colors.Length, h.ToHashCode());
            if (!buckets.TryGetValue(key, out var candidates)) buckets[key] = candidates = [];
            var set = candidates.FirstOrDefault(c => ResolvedColorsMatch(c[0], p, ignoreTransparentMaskRgb));
            if (set is null) { set = [p]; candidates.Add(set); sets.Add(set); }
            else set.Add(p);
        }
        return sets.Where(s => s.Count > 1).ToList();

        static Rgba[] ResolveColors(Palette p)
        {
            if (p.LinksTo is { } target) return ResolveColors(target);
            return p.Colors;
        }

        static bool ResolvedColorsMatch(Palette a, Palette b, bool ignoreTransparentMaskRgb)
        {
            var ca = ResolveColors(a);
            var cb = ResolveColors(b);
            if (ca.Length != cb.Length) return false;
            for (int i = 0; i < ca.Length; i++)
            {
                if (ignoreTransparentMaskRgb && i == 0 && ca[i].A == 0 && cb[i].A == 0) continue;
                if (ca[i] != cb[i]) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Links every later member of each set to its first (sets <see cref="FindDuplicatePalettes"/>).
    /// When <paramref name="discard"/> is true, discarded palettes are removed from the file and
    /// every sprite pointing at one is repointed at the kept palette. Returns how many
    /// palettes got a new link target or were removed.
    /// </summary>
    public int ShareDuplicatePalettes(IEnumerable<List<Palette>> sets, bool discard = false)
    {
        int linked = 0;
        var removed = new HashSet<Palette>();
        var replacements = new Dictionary<Palette, Palette>();
        foreach (var set in sets)
        {
            var keep = set[0];
            if (set.Skip(1).Any(p => ReferenceEquals(keep.LinksTo, p))) Materialize(keep);
            foreach (var p in set.Skip(1))
            {
                if (discard)
                {
                    foreach (var s in Sprites)
                        if (ReferenceEquals(s.Palette, p)) s.Palette = keep;
                    removed.Add(p);
                    replacements[p] = keep;
                    linked++;
                }
                else if (!ReferenceEquals(p.LinksTo, keep))
                {
                    p.LinksTo = keep;
                    linked++;
                }
            }
        }
        if (discard)
        {
            foreach (var p in Palettes)
            {
                if (removed.Contains(p) || p.LinksTo is not { } target
                    || !replacements.TryGetValue(target, out var keep)) continue;
                if (ReferenceEquals(p, keep)) Materialize(p);
                else p.LinksTo = keep;
            }
            var used = Sprites.Select(s => s.Palette).OfType<Palette>().ToHashSet();
            Palettes.RemoveAll(p => removed.Contains(p) && !used.Contains(p));
        }
        return linked;

        static void Materialize(Palette palette)
        {
            var colors = palette.Colors.ToArray();
            palette.LinksTo = null;
            palette.Colors = colors;
        }
    }

    /// <summary>
    /// Removes <paramref name="source"/> by merging its users into an earlier,
    /// independent embedded palette. Direct sprite users and palettes linked to
    /// the source are redirected to <paramref name="target"/>.
    /// </summary>
    public (int Sprites, int Palettes) MergePalette(Palette source, Palette target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (SffVersions.IsV1(Version)) throw new InvalidOperationException("Palette merging is only available in SFFv2.");
        if (!Palettes.Contains(source)) throw new ArgumentException("The source palette is not in this file.", nameof(source));
        if (!Palettes.Contains(target)) throw new ArgumentException("The target palette is not in this file.", nameof(target));
        if (ReferenceEquals(source, target)) throw new ArgumentException("A palette cannot be merged into itself.", nameof(target));
        if (source.Source != PaletteSource.Embedded || target.Source != PaletteSource.Embedded)
            throw new ArgumentException("Only embedded palettes can be merged.");

        int sourcePosition = Palettes.IndexOf(source);
        if (Palettes.IndexOf(target) > sourcePosition)
            throw new ArgumentException("The target palette must appear earlier in the palette list.", nameof(target));
        if (target.LinksTo is not null)
            throw new ArgumentException("The target palette must have its own colors.", nameof(target));

        int spriteUsers = 0;
        foreach (var sprite in Sprites)
        {
            if (!ReferenceEquals(sprite.Palette, source)) continue;
            sprite.Palette = target;
            spriteUsers++;
        }

        int linkedPalettes = 0;
        foreach (var palette in Palettes)
        {
            if (ReferenceEquals(palette, source) || !ReferenceEquals(palette.LinksTo, source)) continue;
            palette.LinksTo = target;
            linkedPalettes++;
        }

        Palettes.Remove(source);
        return (spriteUsers, linkedPalettes);
    }

    /// <summary>The palette "the previous sprite's palette" resolves to for this sprite, or null.</summary>
    public Palette? PaletteBefore(int from)
    {
        bool v1 = SffVersions.IsV1(Version);
        for (int i = from; i >= 0; i--)
        {
            var s = Sprites[i];
            if (v1 && s.SharesWith is not null) continue;
            if (s.Palette is not null) return s.Palette;
        }
        return null;
    }

    /// <summary>The palette "the previous sprite's palette" resolves to for this sprite, or null.</summary>
    public Palette? PreviousPalette(Sprite sprite) => PaletteBefore(PositionOf(sprite) - 1);

    /// <summary>
    /// Whether "make palette unique" would change anything: the sprite has a
    /// palette and can carry one (a v1 pixel-linked sprite cannot), and its
    /// palette is not already its own. In v2 that means no other sprite
    /// references the same object; in v1 a sprite's palette is "its own" when
    /// no *earlier* non-link sprite shares the object — sprites after it
    /// following along is the normal run state, not sharing.
    /// </summary>
    public bool PaletteCanBeMadeUnique(Sprite sprite)
    {
        if (sprite.Palette is null) return false;
        if (SffVersions.IsV1(Version) && sprite.SharesWith is not null) return false;
        var old = sprite.Palette;

        if (SffVersions.IsV1(Version))
        {
            // Walk back over the palette chain: links take no part in it, and
            // the first earlier carrier decides whose run this sprite is in.
            for (int i = PositionOf(sprite) - 1; i >= 0; i--)
            {
                var s = Sprites[i];
                if (s.SharesWith is not null) continue;
                return ReferenceEquals(s.Palette, old); // same object = a follower
            }
            return false; // no earlier carrier at all: this sprite carries its own
        }

        return Sprites.Any(s => !ReferenceEquals(s, sprite) && ReferenceEquals(s.Palette, old));
    }

    /// <summary>
    /// Gives a sprite a palette of its own: a private copy of the colors,
    /// added to the file's palette list.
    ///
    /// In v1 the palette chain is sequential — a sprite carries a palette or
    /// reuses the previous one's — so the sprites after this one that shared
    /// the old object follow it onto the copy, until the first sprite whose
    /// palette is a different object. Sprites before it are untouched. Editing
    /// this sprite's palette from now on reaches its followers but not the
    /// sprites before it.
    /// In v2 a palette is an addressed resource, so only this sprite is
    /// repointed and every other sprite keeps the original.
    ///
    /// Returns the (sprite, old palette) pairs it repointed, so a caller can
    /// undo; empty when there is nothing to do.
    /// </summary>
    public List<(Sprite Sprite, Palette? Old)> MakePaletteUnique(Sprite sprite)
    {
        var changed = new List<(Sprite, Palette?)>();
        if (!PaletteCanBeMadeUnique(sprite)) return changed;

        var old = sprite.Palette!;
        var copy = new Palette { Group = old.Group, Index = Palettes.Count + 1, Colors = (Rgba[])old.Colors.Clone() };
        changed.Add((sprite, old));
        sprite.Palette = copy;

        if (SffVersions.IsV1(Version))
        {
            for (int i = PositionOf(sprite) + 1; i < Sprites.Count; i++)
            {
                var t = Sprites[i];
                if (ReferenceEquals(t.Palette, old)) { changed.Add((t, old)); t.Palette = copy; }
                else if (t.SharesWith is null) break; // a sprite carrying its own palette ends the run
            }
        }
        EnsurePalette(copy);
        return changed;
    }

    /// <summary>
    /// Points a sprite at the previous sprite's palette (PreviousPalette).
    /// The v1 run rule from MakePaletteUnique applies in reverse: sprites after
    /// this one that shared its old palette follow it onto the previous one,
    /// because on disk their "same palette as previous" flag chains through
    /// this sprite. In v2 only this sprite changes.
    ///
    /// Returns the (sprite, old palette) pairs it repointed, so a caller can
    /// undo; empty when there is no previous palette or the sprite already
    /// uses it.
    /// </summary>
    public List<(Sprite Sprite, Palette? Old)> UsePreviousPalette(Sprite sprite)
    {
        var changed = new List<(Sprite, Palette?)>();
        if (SffVersions.IsV1(Version) && sprite.SharesWith is not null) return changed;
        if (PreviousPalette(sprite) is not { } previous) return changed;
        var old = sprite.Palette;
        if (ReferenceEquals(old, previous)) return changed;

        changed.Add((sprite, old));
        sprite.Palette = previous;

        if (SffVersions.IsV1(Version) && old is not null)
        {
            for (int i = PositionOf(sprite) + 1; i < Sprites.Count; i++)
            {
                var t = Sprites[i];
                if (ReferenceEquals(t.Palette, old)) { changed.Add((t, old)); t.Palette = previous; }
                else if (t.SharesWith is null) break;
            }
        }
        return changed;
    }
}

/// <summary>
/// Pixel, sprite-palette and palette-link state (including the on-disk bytes
/// the writer reuses), plus the palette list, so bulk link and merge operations
/// can be undone exactly.
/// </summary>
public sealed class PixelLinkSnapshot
{
    private readonly SffFile _file;
    private readonly List<Palette> _palettes;
    private readonly (Palette Palette, Palette? LinksTo)[] _paletteLinks;
    private readonly (Sprite S, Sprite? Shares, byte[]? Indexed, byte[]? Direct, int W, int H, bool Dirty,
        byte[]? Source, SpriteFormat Fmt, byte Depth, Palette? Pal)[] _items;

    internal PixelLinkSnapshot(SffFile file)
    {
        _file = file;
        _palettes = [.. file.Palettes];
        _paletteLinks = file.Palettes.Select(p => (p, p.LinksTo)).ToArray();
        _items = file.Sprites.Select(s => (s, s.SharesWith, s.Indexed, s.Direct, s.Width, s.Height, s.PixelsDirty,
            s.SourceData, s.SrcFormat, s.SrcDepth, s.Palette)).ToArray();
    }

    public void Restore()
    {
        foreach (var x in _items)
        {
            x.S.SharesWith = x.Shares;
            x.S.Indexed = x.Indexed;
            x.S.Direct = x.Direct;
            x.S.Width = x.W;
            x.S.Height = x.H;
            x.S.PixelsDirty = x.Dirty;
            x.S.SourceData = x.Source;
            x.S.SrcFormat = x.Fmt;
            x.S.SrcDepth = x.Depth;
            x.S.Palette = x.Pal;
        }
        _file.Palettes.Clear();
        _file.Palettes.AddRange(_palettes);
        foreach (var (palette, linksTo) in _paletteLinks) palette.LinksTo = linksTo;
    }
}
