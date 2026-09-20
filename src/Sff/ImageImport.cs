namespace Sff;

/// <summary>
/// How an imported image's colors should be brought in: which palette the
/// sprite ends up using, and how the image's pixel indexes map onto it.
/// The two choices are independent, so any palette can be combined with either
/// index mapping.
/// </summary>
public readonly record struct ImportPlan
{
    /// <summary>Store the image as 32-bit color. Needs an SFF 2.0.1.0 file.</summary>
    public bool TrueColor { get; init; }

    /// <summary>Use the palette the image itself carries (or one built from a true-color image in first-seen order).</summary>
    public bool OwnPalette { get; init; }

    /// <summary>The palette to use when OwnPalette is false.</summary>
    public Palette? Palette { get; init; }

    /// <summary>
    /// Keep the image's pixel indexes and look them up in the chosen palette —
    /// the right choice when the image was drawn against the same index layout.
    /// With OwnPalette this is the only meaningful mapping (matching against
    /// itself is always exact), so Own() always sets it.
    /// </summary>
    public bool KeepIndexes { get; init; }

    /// <summary>The image keeps its own palette and indexes, untouched.</summary>
    public static ImportPlan Own() => new() { OwnPalette = true, KeepIndexes = true };

    /// <summary>Pixels land in a chosen palette, keeping indexes or matching nearest colors.</summary>
    public static ImportPlan Given(Palette palette, bool keepIndexes) => new() { Palette = palette, KeepIndexes = keepIndexes };

    /// <summary>Store as 32-bit color. Needs SFF 2.0.1.0.</summary>
    public static ImportPlan TrueColor32() => new() { TrueColor = true };
}

/// <summary>Brings outside images into a file.</summary>
public static class ImageImport
{
    /// <summary>Default alpha below which an imported pixel is treated as transparent (index 0).</summary>
    public const int DefaultTransparentAlphaThreshold = 32;

    /// <summary>
    /// Alpha below which an imported pixel is treated as transparent (index 0).
    /// The editor sets this from settings.json at startup; the default matches
    /// Settings.DefaultNearestColorAlphaThreshold.
    /// </summary>
    public static int TransparentAlphaThreshold { get; set; } = DefaultTransparentAlphaThreshold;

    /// <summary>What happened during an import, so the editor can say something useful.</summary>
    public readonly record struct Report(
        int Width, int Height, int Colors, int ExactMatches,
        bool TrueColor, bool OwnPalette, bool KeptIndexes, int OutOfRange, bool Reduced = false)
    {
        public bool Lossless => !Reduced && (TrueColor || OwnPalette
            || (KeptIndexes ? OutOfRange == 0 : Colors == ExactMatches));

        public override string ToString() => Reduced
            ? $"{Width}x{Height}, reduced from {Colors} colors to {GifImport.MaxSharedColors} colors by median cut; {ExactMatches} colors kept exactly"
            : TrueColor ? $"{Width}x{Height}, kept as 32-bit color"
            : OwnPalette ? $"{Width}x{Height}, {Colors}-color palette kept in its original order"
            : KeptIndexes ? $"{Width}x{Height}, indexes kept as they are"
                          + (OutOfRange > 0 ? $"; {OutOfRange} pixels fall outside the palette" : "")
            : $"{Width}x{Height}, {ExactMatches} of {Colors} colors matched the palette exactly";
    }

    private readonly record struct OwnPaletteResult(
        byte[] Indices, Palette Palette, int SourceColors, int ExactMatches, bool Reduced);

    public sealed class PreparedOwnPaletteImport
    {
        private readonly Png.Image _image;
        private readonly byte[]? _bgra;
        private readonly List<Rgba>? _colors;
        private readonly List<long>? _weights;
        private readonly Dictionary<uint, int>? _lookup;
        private readonly byte[]? _indices;
        private int _nextPixel;
        private bool _built;

        public bool RequiresMedianCut { get; private set; }
        public int Width => _image.Width;
        public int Height => _image.Height;

        private PreparedOwnPaletteImport(Png.Image image)
        {
            _image = image;
            if (image.Indices is not null)
            {
                if (image.Plte is null)
                    throw new InvalidDataException("this image carries no palette of its own; import it with 'keep the image's indexes' against a chosen palette");
                return;
            }

            _bgra = ToBgra(image);
            _colors = [new Rgba(0, 0, 0, 0)];
            _weights = [0];
            _lookup = new Dictionary<uint, int>();
            _indices = new byte[_bgra.Length / 4];
            ScanUntilOverflow();
        }

        internal static PreparedOwnPaletteImport Prepare(Png.Image image) => new(image);

        public Sprite Build(out Report report)
        {
            if (_built) throw new InvalidOperationException("this prepared import has already been built");
            _built = true;
            var own = BuildPalette();
            if (own.Palette.Colors.Length > byte.MaxValue + 1)
                throw TooManyColors(own.Palette.Colors.Length - 1);

            report = new Report(Width, Height, own.SourceColors, own.ExactMatches,
                false, OwnPalette: true, KeptIndexes: !own.Reduced, 0, own.Reduced);
            return new Sprite
            {
                Width = Width,
                Height = Height,
                Indexed = own.Indices,
                Direct = null,
                Palette = own.Palette,
                SrcFormat = SpriteFormat.Rle8,
                SrcDepth = 8,
                DecodeError = null,
                PixelsDirty = true,
            };
        }

        private void ScanUntilOverflow()
        {
            while (_nextPixel < _indices!.Length)
            {
                int pixel = _nextPixel;
                byte b = _bgra![pixel * 4], g = _bgra[pixel * 4 + 1];
                byte r = _bgra[pixel * 4 + 2], a = _bgra[pixel * 4 + 3];
                if (a < TransparentAlphaThreshold)
                {
                    _indices[pixel] = Sprite.TransparentIndex;
                    _nextPixel++;
                    continue;
                }

                uint key = (uint)(r << 16 | g << 8 | b);
                if (_lookup!.TryGetValue(key, out int index)) _weights![index]++;
                else
                {
                    if (_colors!.Count >= byte.MaxValue + 1)
                    {
                        RequiresMedianCut = true;
                        return;
                    }
                    index = _colors.Count;
                    _lookup.Add(key, index);
                    _colors.Add(new Rgba(r, g, b, 255));
                    _weights!.Add(1);
                }
                _indices[pixel] = (byte)index;
                _nextPixel++;
            }
        }

        private void CompleteColorScan()
        {
            while (_nextPixel < _indices!.Length)
            {
                int pixel = _nextPixel++;
                byte b = _bgra![pixel * 4], g = _bgra[pixel * 4 + 1];
                byte r = _bgra[pixel * 4 + 2], a = _bgra[pixel * 4 + 3];
                if (a < TransparentAlphaThreshold) continue;

                uint key = (uint)(r << 16 | g << 8 | b);
                if (_lookup!.TryGetValue(key, out int index)) _weights![index]++;
                else
                {
                    _lookup.Add(key, _colors!.Count);
                    _colors.Add(new Rgba(r, g, b, 255));
                    _weights!.Add(1);
                }
            }
        }

        private OwnPaletteResult BuildPalette()
        {
            if (_image.Indices is not null)
            {
                var palette = PadTo256(new Palette { Colors = (Rgba[])_image.Plte!.Clone() });
                return new OwnPaletteResult(_image.Indices, palette, _image.Plte.Length, _image.Plte.Length, false);
            }

            if (!RequiresMedianCut)
            {
                var palette = PadTo256(new Palette { Colors = _colors!.ToArray() });
                return new OwnPaletteResult(_indices!, palette, _colors.Count, _colors.Count, false);
            }

            CompleteColorScan();
            var reduced = Quantize.Reduce(_colors!.ToArray(), _weights!.ToArray(), GifImport.MaxSharedColors);
            var mapped = Match(_bgra!, reduced, out int sourceColors, out int exactMatches);
            return new OwnPaletteResult(mapped, PadTo256(reduced), sourceColors, exactMatches, true);
        }
    }

    internal static Palette PadTo256(Palette palette)
    {
        if (palette.Colors.Length >= 256) return palette;
        var colors = new Rgba[256];
        Array.Copy(palette.Colors, colors, palette.Colors.Length);
        Array.Fill(colors, new Rgba(0, 0, 0, 255), palette.Colors.Length, 256 - palette.Colors.Length);
        palette.Colors = colors;
        return palette;
    }

    /// <summary>Too many colors for one 8-bit palette.</summary>
    public static NotSupportedException TooManyColors(int count) =>
        new NotSupportedException($"Too many colors for one palette: {count}. Use 32-bit color or reduce.");

    /// <summary>Prepares an own-palette import and stops scanning once it finds a 256th color.</summary>
    public static PreparedOwnPaletteImport PrepareOwnPalette(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return PreparedOwnPaletteImport.Prepare(Decode(image));
    }

    /// <summary>Reads a PNG or PCX image and builds a sprite from it.</summary>
    public static Sprite FromImage(byte[] image, ImportPlan plan, out Report report)
    {
        var sprite = new Sprite();
        report = ReplaceImage(sprite, image, plan);
        return sprite;
    }

    /// <summary>
    /// Whether the image's pixels are palette indexes, decidable before
    /// importing: PCX is 8-bit indexed by definition; PNG is sniffed from its
    /// IHDR. Unknown data answers false, which only disables "keep the
    /// image's indexes".
    /// </summary>
    public static bool ImageIsIndexed(byte[] data) => LooksLikePcx(data) || PngExport.IsIndexed(data);

    /// <summary>Decodes a PNG or PCX image to straight BGRA pixels for display.</summary>
    public static (byte[] Bgra, int Width, int Height) DecodeToBgra(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var decoded = Decode(image);
        return (ToBgra(decoded), decoded.Width, decoded.Height);
    }

    private static bool LooksLikePcx(byte[] data) => data.Length >= 4 && data[0] == 0x0a;

    private static bool LooksLikePng(byte[] data) =>
        data.Length >= 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G';

    /// <summary>Decodes PNG or PCX into the internal image shape the plan paths share.</summary>
    internal static Png.Image Decode(byte[] image)
    {
        if (LooksLikePcx(image))
        {
            var pcx = Pcx.Decode(image);
            return new Png.Image(pcx.Width, pcx.Height, pcx.Indices, null, pcx.Palette);
        }
        if (LooksLikePng(image)) return Png.Decode(image);
        throw new InvalidDataException("unsupported image; the editor reads PNG and PCX");
    }

    /// <summary>
    /// Replaces a sprite's pixels from an image, leaving its group, number and
    /// axis alone. A sprite that was sharing stops sharing, since it now has
    /// pixels of its own.
    /// </summary>
    public static Report ReplaceImage(Sprite sprite, byte[] image, ImportPlan plan)
    {
        var img = Decode(image);

        // A PCX saved without its own palette (the v1 "same as previous" case)
        // can only be imported by keeping its indexes against a chosen palette.
        if (img.Bgra is null && img.Plte is null && (plan.OwnPalette || !plan.KeepIndexes))
            throw new InvalidDataException(
                "this image carries no palette of its own; import it with 'keep the image's indexes' against a chosen palette");

        var imported = new Sprite
        {
            Width = img.Width,
            Height = img.Height,
            DecodeError = null,
            PixelsDirty = true,
        };

        if (plan.TrueColor)
        {
            imported.Direct = ToBgra(img);
            imported.Indexed = null;
            imported.Palette = null;
            imported.SrcFormat = SpriteFormat.Png32;
            imported.SrcDepth = 32;
            var report = new Report(img.Width, img.Height, 0, 0, TrueColor: true, false, false, 0);
            Commit();
            return report;
        }

        if (plan.OwnPalette)
        {
            var own = PreparedOwnPaletteImport.Prepare(img).Build(out var report);
            imported.Indexed = own.Indexed;
            imported.Direct = own.Direct;
            imported.Palette = own.Palette;
            imported.SrcFormat = own.SrcFormat;
            imported.SrcDepth = own.SrcDepth;
            Commit();
            return report;
        }

        var target = plan.Palette ?? throw new InvalidOperationException(
            "the import plan names no palette; use OwnPalette for the image's own");
        imported.Palette = target;
        imported.Direct = null;
        imported.SrcFormat = SpriteFormat.Rle8;
        imported.SrcDepth = 8;

        if (plan.KeepIndexes && img.Indices is { } indices)
        {
            imported.Indexed = indices;
            var report = new Report(img.Width, img.Height, indices.Distinct().Count(), 0,
                false, false, KeptIndexes: true, CountOutOfRange(indices, target));
            Commit();
            return report;
        }

        if (plan.KeepIndexes)
        {
            // A true-color image has no indexes to keep: derive them in
            // first-seen order and use those against the chosen palette.
            var (derived, _) = FirstSeenIndices(img);
            imported.Indexed = derived;
            var report = new Report(img.Width, img.Height, derived.Distinct().Count(), 0,
                false, false, KeptIndexes: true, CountOutOfRange(derived, target));
            Commit();
            return report;
        }

        imported.Indexed = Match(ToBgra(img), target, out int colors, out int exact);
        var matched = new Report(img.Width, img.Height, colors, exact, false, false, false, 0);
        Commit();
        return matched;

        void Commit()
        {
            sprite.SharesWith = imported.SharesWith;
            sprite.Width = imported.Width;
            sprite.Height = imported.Height;
            sprite.DecodeError = imported.DecodeError;
            sprite.PixelsDirty = imported.PixelsDirty;
            sprite.Indexed = imported.Indexed;
            sprite.Direct = imported.Direct;
            sprite.Palette = imported.Palette;
            sprite.SrcFormat = imported.SrcFormat;
            sprite.SrcDepth = imported.SrcDepth;
        }
    }

    private static int CountOutOfRange(byte[] indices, Palette pal)
    {
        int count = 0;
        foreach (var i in indices)
            if (i >= pal.Colors.Length) count++;
        return count;
    }


    /// <summary>Indexes for a true-color image: colors numbered as first seen, index 0 kept as the mask.</summary>
    private static (byte[] Indices, List<Rgba> Colors) FirstSeenIndices(Png.Image img)
    {
        var bgra = ToBgra(img);
        var colors = new List<Rgba> { new(0, 0, 0, 0) }; // index 0 stays the mask
        var lookup = new Dictionary<uint, byte>();
        var indices = new byte[bgra.Length / 4];

        for (int i = 0; i < indices.Length; i++)
        {
            byte b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2], a = bgra[i * 4 + 3];
            if (a < TransparentAlphaThreshold) { indices[i] = Sprite.TransparentIndex; continue; }

            uint key = (uint)(r << 16 | g << 8 | b);
            if (!lookup.TryGetValue(key, out byte idx))
            {
                if (colors.Count >= 256)
                    throw new NotSupportedException(
                        "This image has more than 255 colors, so it has no palette to keep. "
                        + "Choose a palette to match it against, or import it as 32-bit color.");
                idx = (byte)colors.Count;
                colors.Add(new Rgba(r, g, b, 255));
                lookup[key] = idx;
            }
            indices[i] = idx;
        }
        return (indices, colors);
    }

    internal static byte[] ToBgra(Png.Image img)
    {
        if (img.Bgra is not null) return img.Bgra;
        if (img.Indices is null) throw new InvalidDataException("png: no pixel data");

        var plte = img.Plte ?? throw new InvalidDataException("png: indexed image with no palette");
        var bgra = new byte[img.Width * img.Height * 4];
        for (int i = 0; i < img.Indices.Length && i * 4 + 3 < bgra.Length; i++)
        {
            var c = img.Indices[i] < plte.Length ? plte[img.Indices[i]] : default;
            bgra[i * 4] = c.B; bgra[i * 4 + 1] = c.G; bgra[i * 4 + 2] = c.R; bgra[i * 4 + 3] = c.A;
        }
        return bgra;
    }

    /// <summary>
    /// Maps each pixel to its nearest palette entry. Pixels below the transparent
    /// alpha threshold become index 0, and index 0 is never chosen for an opaque
    /// pixel, because it is the transparency mask rather than a usable color.
    /// </summary>
    private static byte[] Match(byte[] bgra, Palette pal, out int colors, out int exact)
    {
        var cache = new Dictionary<uint, byte>();
        var exactly = new HashSet<uint>();
        var outp = new byte[bgra.Length / 4];

        for (int i = 0; i < outp.Length; i++)
        {
            byte b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2], a = bgra[i * 4 + 3];
            if (a < TransparentAlphaThreshold) { outp[i] = Sprite.TransparentIndex; continue; }

            uint key = (uint)(r << 16 | g << 8 | b);
            if (!cache.TryGetValue(key, out byte idx))
            {
                idx = Nearest(pal, r, g, b, out bool isExact);
                cache[key] = idx;
                if (isExact) exactly.Add(key);
            }
            outp[i] = idx;
        }

        colors = cache.Count;
        exact = exactly.Count;
        return outp;
    }

    /// <summary>
    /// Remaps an existing sprite's indexes by matching its current palette colors
    /// to the nearest colors in another palette. Index 0 stays transparent; other
    /// source entries below the transparent alpha threshold also map to index 0.
    /// </summary>
    public static byte[] RemapIndexes(byte[] indices, Palette source, Rgba[] targetColors)
    {
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targetColors);

        var sourceColors = source.Colors;
        var target = new Palette { Colors = targetColors };
        var mappedByIndex = new byte[256];
        var hasMapping = new bool[256];
        var result = new byte[indices.Length];

        for (int pixel = 0; pixel < indices.Length; pixel++)
        {
            byte sourceIndex = indices[pixel];
            if (sourceIndex == Sprite.TransparentIndex)
            {
                result[pixel] = Sprite.TransparentIndex;
                continue;
            }
            if (sourceIndex >= sourceColors.Length)
                throw new InvalidDataException(
                    $"sprite index {sourceIndex} has no color in its current palette");

            var sourceColor = sourceColors[sourceIndex];
            if (sourceColor.A < TransparentAlphaThreshold)
            {
                result[pixel] = Sprite.TransparentIndex;
                continue;
            }

            if (!hasMapping[sourceIndex])
            {
                if (targetColors.Length < 2)
                    throw new InvalidDataException("the destination palette has no non-transparent colors");
                mappedByIndex[sourceIndex] = Nearest(target, sourceColor.R, sourceColor.G, sourceColor.B, out _);
                hasMapping[sourceIndex] = true;
            }
            result[pixel] = mappedByIndex[sourceIndex];
        }

        return result;
    }

    /// <summary>
    /// Maps BGRA pixels to the nearest palette entry, converting a direct-color
    /// sprite to indexed. Pixels below the transparent alpha threshold become index 0.
    /// </summary>
    public static byte[] MatchToPalette(byte[] bgra, Rgba[] targetColors)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(targetColors);
        if (targetColors.Length < 2)
            throw new InvalidDataException("the destination palette has no non-transparent colors");
        return Match(bgra, new Palette { Colors = targetColors }, out _, out _);
    }

    private static byte Nearest(Palette pal, byte r, byte g, byte b, out bool isExact)
    {
        int best = 1, bestDist = int.MaxValue;
        for (int i = 1; i < pal.Colors.Length && i < 256; i++) // skip the mask entry
        {
            var c = pal.Colors[i];
            int dr = c.R - r, dg = c.G - g, db = c.B - b;
            int d = dr * dr + dg * dg + db * db;
            if (d >= bestDist) continue;
            best = i;
            bestDist = d;
            if (d == 0) break;
        }
        isExact = bestDist == 0;
        return (byte)best;
    }
}
