namespace Sff;

/// <summary>One sprite found in a sheet, as a box in the sheet's own pixels.</summary>
public readonly record struct SheetCell(int X, int Y, int Width, int Height);

/// <summary>
/// The sprites found in a sheet, and the background they were found against.
/// </summary>
public sealed class SheetLayout
{
    /// <summary>The sprites in rough rows, top to bottom and left to right.</summary>
    public IReadOnlyList<SheetCell> Cells { get; }

    /// <summary>
    /// The color treated as background, or null when the sheet carries real
    /// transparency and nothing had to be guessed.
    /// </summary>
    public Rgba? Background { get; }

    /// <summary>
    /// True when <see cref="Background"/> is the sheet's most common color
    /// rather than transparency the file actually declares. A guess, so a
    /// caller that shows the sheet should show this too.
    /// </summary>
    public bool AssumedBackground { get; }

    public SheetLayout(IReadOnlyList<SheetCell> cells, Rgba? background, bool assumedBackground)
    {
        ArgumentNullException.ThrowIfNull(cells);
        // The status line names the background whenever it was assumed, so the
        // two cannot be set apart.
        if (assumedBackground && background is null)
            throw new ArgumentException("an assumed background needs a color to name", nameof(background));
        Cells = cells;
        Background = background;
        AssumedBackground = assumedBackground;
    }

    public int Count => Cells.Count;

    /// <summary>One line for the status bar.</summary>
    public override string ToString()
    {
        var found = Count == 1 ? "1 sprite found" : $"{Count} sprites found";
        return AssumedBackground && Background is { } bg
            ? $"{found}, treating {SheetImport.Describe(bg)} as the background"
            : found;
    }
}

/// <summary>
/// Finds the sprites in an uneven sheet: sprites are wherever they are, at any
/// size, and each comes out cropped tight. Nothing is sliced at fixed
/// intervals, so a sheet with no common grid imports as well as one with a
/// perfect one.
/// </summary>
/// <remarks>
/// Detection is connected-component labeling over the sheet's non-background
/// pixels, 4-connected. Splitting on empty rows and columns would be simpler,
/// but it merges sprites staggered diagonally — overlapping in both axes
/// without touching — which is common enough in real sheets to be wrong often.
/// Each component's bounding box is the sprite, so cropping falls out of
/// detection rather than being a second pass.
/// </remarks>
public static class SheetImport
{
    /// <summary>
    /// Default minimum sprite size in pixels, measured on a sprite's longer
    /// side. Chosen to sit above the keyed-edge speckle a real sheet produces
    /// (all of it under 12px on the one measured) and below the smallest real
    /// sprite (30px on that same sheet).
    /// </summary>
    public const int DefaultMinimumSize = 16;

    /// <summary>
    /// How much of a sheet has to be transparent before that transparency is
    /// read as its background, as a percentage of its pixels. Below this the
    /// sheet is read against its most common color instead: a keyed sheet with
    /// one stray semi-transparent pixel would otherwise be read by alpha
    /// alone, and the key color -- which surrounds and separates every sprite --
    /// would join them all into a single component.
    /// </summary>
    public const int MinimumTransparentPercent = 1;

    /// <summary>
    /// Sprites smaller than this on their longer side are dropped rather than
    /// imported, because a sheet's stray pixels and compression speckle are
    /// indistinguishable from small sprites. The editor sets this from
    /// settings.json at startup. 0 keeps everything, however small.
    /// </summary>
    public static int MinimumSize { get; set; } = DefaultMinimumSize;

    /// <summary>
/// Finds the sprites in a PNG or PCX. Throws the same errors importing a
/// single image would.
/// </summary>
public static SheetLayout Detect(byte[] image) => Detect(image, null);

    /// <summary>
    /// Finds the sprites in a PNG or PCX, treating <paramref name="background"/>
    /// as the background instead of deciding for itself. The caller uses this
    /// to correct a guess: detection reports what it assumed, and a user who
    /// says otherwise gets a second look with their answer.
    /// </summary>
    public static SheetLayout Detect(byte[] image, Rgba? background)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Detect(ImageImport.Decode(image), background);
    }

    /// <summary>
    /// Finds the sprites in an already-decoded sheet. A sheet with real
    /// transparency is read against that; one without has its most common
    /// color treated as the background, which is a guess and is reported as
    /// <see cref="SheetLayout.AssumedBackground"/>.
    /// </summary>
    internal static SheetLayout Detect(Png.Image img) => Detect(img, null);

    internal static SheetLayout Detect(Png.Image img, Rgba? background)
    {
        ArgumentNullException.ThrowIfNull(img);
        var (mask, bg, assumed) = Foreground(img, background);
        var found = Label(mask, img.Width, img.Height);
        var cells = MinimumSize > 0 ? found.Where(IsBigEnough).ToList() : found;
        return new SheetLayout(cells, bg, assumed);
    }

    /// <summary>
    /// Measured on the longer side, so a sprite that is tall and thin is not
    /// mistaken for a speck.
    /// </summary>
    private static bool IsBigEnough(SheetCell c) =>
        Math.Max(c.Width, c.Height) >= MinimumSize;

    /// <summary>
    /// Which pixels belong to a sprite, plus the background they were read
    /// against and whether that background was guessed. Decided in one pass so
    /// the mask and the reported background cannot disagree. A background the
    /// caller supplies wins over both readings, and is reported as the caller's
    /// rather than a guess.
    /// </summary>
    private static (bool[] Mask, Rgba? Background, bool Assumed) Foreground(Png.Image img, Rgba? chosen)
    {
        int threshold = ImageImport.TransparentAlphaThreshold;
        int w = img.Width, h = img.Height;
        var mask = new bool[w * h];

        if (img.Indices is { } indices)
        {
            RequireLength(indices.Length, w * h, "indexed");
            var plte = img.Plte ?? throw new InvalidDataException("png: indexed image with no palette");
            if (plte.Length == 0) throw new InvalidDataException("png: the indexed sheet has an empty palette");

            if (chosen is { } picked)
            {
                var mine = Array.IndexOf(plte, picked);
                for (int i = 0; i < mask.Length; i++)
                    mask[i] = indices[i] != mine;
                return (mask, picked, false);
            }

            // An indexed sheet keeps index 0 as the mask, which is the same
            // convention the sprite formats use.
            if (plte.Length > 0 && plte[0].A < threshold)
            {
                for (int i = 0; i < mask.Length; i++)
                    mask[i] = indices[i] != Sprite.TransparentIndex;
                return (mask, null, false);
            }

            var bg = MostCommonIndexed(indices, plte);
            for (int i = 0; i < mask.Length; i++)
            {
                byte v = indices[i];
                mask[i] = v < plte.Length && plte[v] != bg;
            }
            return (mask, bg, true);
        }

        var bgra = img.Bgra ?? throw new InvalidDataException("png: no pixel data");
        RequireLength(bgra.Length, w * h * 4, "true-color");

        if (chosen is { } answer)
        {
            for (int i = 0; i < mask.Length; i++)
                if (!SameColor(bgra, i * 4, answer)) mask[i] = true;
            return (mask, answer, false);
        }

        // Only real transparency decides this. A single stray semi-transparent
        // pixel on a keyed sheet must not switch the whole sheet to reading by
        // alpha, because the key color runs between and around every sprite and
        // would join them all into one component.
        if (TransparentShare(bgra, w * h, threshold) >= MinimumTransparentPercent)
        {
            for (int i = 0; i < mask.Length; i++)
                mask[i] = bgra[i * 4 + 3] >= threshold;
            return (mask, null, false);
        }

        var background = MostCommonBgra(bgra, w * h);
        for (int i = 0; i < mask.Length; i++)
            if (!SameColor(bgra, i * 4, background)) mask[i] = true;
        return (mask, background, true);
    }

    private static void RequireLength(int have, int need, string kind)
    {
        if (have < need)
            throw new InvalidDataException($"png: the {kind} image is shorter than its dimensions claim");
    }

    /// <summary>Percent of the sheet's own pixels that are transparent.</summary>
    private static int TransparentShare(byte[] bgra, int pixels, int threshold)
    {
        long clear = 0;
        for (int i = 0; i < pixels; i++)
            if (bgra[i * 4 + 3] < threshold) clear++;
        return (int)Math.Min(100, clear * 100 / Math.Max(1, pixels));
    }

    /// <summary>
    /// The palette entry used most often. Ties go to the lowest index, so the
    /// same sheet always detects the same background.
    /// </summary>
    private static Rgba MostCommonIndexed(byte[] indices, Rgba[] plte)
    {
        var counts = new int[plte.Length];
        for (int i = 0; i < indices.Length; i++)
            if (indices[i] < plte.Length) counts[indices[i]]++;

        int best = 0;
        for (int i = 1; i < counts.Length; i++)
            if (counts[i] > counts[best]) best = i;
        return plte[best];
    }

    /// <summary>
    /// The most common opaque color, scanning only the image area. Ties go to
    /// the color seen first, so the same sheet always detects the same
    /// background.
    /// </summary>
    private static Rgba MostCommonBgra(byte[] bgra, int pixels)
    {
        var counts = new Dictionary<uint, int>();
        Rgba best = default;
        int bestCount = -1;

        for (int i = 0; i < pixels; i++)
        {
            int at = i * 4;
            uint key = (uint)(bgra[at + 2] << 16 | bgra[at + 1] << 8 | bgra[at]);
            int seen = counts.TryGetValue(key, out int n) ? n : 0;
            seen++;
            counts[key] = seen;
            if (seen > bestCount)
            {
                bestCount = seen;
                best = new Rgba(bgra[at + 2], bgra[at + 1], bgra[at], bgra[at + 3]);
            }
        }
        return best;
    }

    /// <summary>The color of the first (top-left) pixel of the image.</summary>
    public static Rgba FirstPixelColor(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var img = ImageImport.Decode(image);
        if (img.Indices is { } indices)
        {
            var plte = img.Plte ?? throw new InvalidDataException("png: indexed image with no palette");
            if (plte.Length == 0) throw new InvalidDataException("png: the indexed sheet has an empty palette");
            byte v = indices[0];
            return v < plte.Length ? plte[v] : plte[0];
        }
        var bgra = img.Bgra ?? throw new InvalidDataException("png: no pixel data");
        return new Rgba(bgra[2], bgra[1], bgra[0], bgra[3]);
    }

    private static bool SameColor(byte[] bgra, int at, Rgba c) =>
        bgra[at + 2] == c.R && bgra[at + 1] == c.G && bgra[at] == c.B;

    /// <summary>
    /// 4-connected components over the non-background pixels, each as its
    /// bounding box. Bands are anchored by their leftmost box; boxes overlapping
    /// that anchor by at least half the shorter height join the band. Bands are
    /// ordered top to bottom, then left to right. The stack is explicit because
    /// a recursive fill on a large sheet would run out.
    /// </summary>
    private static List<SheetCell> Label(bool[] mask, int w, int h)
    {
        var cells = new List<SheetCell>();
        var stack = new List<int>();

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int start = y * w + x;
                if (!mask[start]) continue;

                int minX = x, maxX = x, minY = y, maxY = y;
                stack.Clear();
                stack.Add(start);
                mask[start] = false;

                while (stack.Count > 0)
                {
                    int at = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    int px = at % w, py = at / w;

                    if (px < minX) minX = px; else if (px > maxX) maxX = px;
                    if (py < minY) minY = py; else if (py > maxY) maxY = py;

                    // Visited pixels are cleared as they are pushed, so the mask
                    // doubles as the "already seen" mark.
                    if (px > 0) Take(mask, at - 1, stack);
                    if (px < w - 1) Take(mask, at + 1, stack);
                    if (py > 0) Take(mask, at - w, stack);
                    if (py < h - 1) Take(mask, at + w, stack);
                }

                cells.Add(new SheetCell(minX, minY, maxX - minX + 1, maxY - minY + 1));
            }

        return OrderRows(cells);
    }

    private static List<SheetCell> OrderRows(List<SheetCell> cells)
    {
        cells.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        var assigned = new bool[cells.Count];
        var rows = new List<(SheetCell Anchor, List<SheetCell> Cells)>();
        var bottoms = new int[cells.Count];
        for (int i = 0; i < cells.Count; i++)
            bottoms[i] = cells[i].Y + cells[i].Height;

        for (int i = 0; i < cells.Count; i++)
        {
            if (assigned[i]) continue;

            var anchor = cells[i];
            var row = new List<SheetCell> { anchor };
            assigned[i] = true;
            int anchorBottom = bottoms[i];

            for (int j = i + 1; j < cells.Count; j++)
            {
                if (assigned[j]) continue;
                var cell = cells[j];
                int overlap = Math.Min(anchorBottom, bottoms[j]) - Math.Max(anchor.Y, cell.Y);
                int shorterHeight = Math.Min(anchor.Height, cell.Height);
                if (overlap <= 0 || overlap * 2 < shorterHeight) continue;

                row.Add(cell);
                assigned[j] = true;
            }

            rows.Add((anchor, row));
        }

        rows.Sort((a, b) => a.Anchor.Y != b.Anchor.Y
            ? a.Anchor.Y.CompareTo(b.Anchor.Y)
            : a.Anchor.X.CompareTo(b.Anchor.X));
        var ordered = new List<SheetCell>(cells.Count);
        foreach (var row in rows)
            ordered.AddRange(row.Cells);
        return ordered;
    }

    private static void Take(bool[] mask, int at, List<int> stack)
    {
        if (!mask[at]) return;
        mask[at] = false;
        stack.Add(at);
    }

    /// <summary>
    /// Builds one sprite per cell through the same import plan a single image
    /// would use, so the dialog's palette and index choices apply here too.
    /// An indexed sheet shares its own palette across the cells, the way GIF
    /// frames share one; a true-color sheet builds its colors from the cells
    /// between them.
    /// </summary>
    public static List<Sprite> ToSprites(byte[] image, SheetLayout layout, ImportPlan plan,
        out List<ImageImport.Report> reports)
    {
        ArgumentNullException.ThrowIfNull(image);
        return ToSprites(ImageImport.Decode(image), layout, plan, out reports);
    }

    internal static List<Sprite> ToSprites(Png.Image img, SheetLayout layout, ImportPlan plan,
        out List<ImageImport.Report> reports)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var cells = layout.Cells;

        if (img.Indices is not null)
            return BuildIndexed(img, cells, layout.Background, plan, out reports);

        return BuildColor(img, cells, layout.Background, plan, null, out reports);
    }

    /// <summary>Gets the colors used across a true-color sheet's detected sprites.</summary>
    public static GifImport.GifColors SharedColors(byte[] image, SheetLayout layout)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(layout);
        var img = ImageImport.Decode(image);
        if (img.Indices is not null)
            throw new InvalidOperationException("an indexed sheet already has its own palette");
        return GifImport.SharedColors(ColorFrames(img, layout.Cells, layout.Background));
    }

    /// <summary>
    /// Builds one sprite per cell against a palette they all share, for when
    /// the caller has already settled what to do about the sheet's colors.
    /// </summary>
    public static List<Sprite> ToSprites(byte[] image, SheetLayout layout, ImportPlan plan,
        Palette sharedPalette, out List<ImageImport.Report> reports)
    {
        ArgumentNullException.ThrowIfNull(image);
        return ToSprites(ImageImport.Decode(image), layout, plan, sharedPalette, out reports);
    }

    internal static List<Sprite> ToSprites(Png.Image img, SheetLayout layout, ImportPlan plan,
        Palette sharedPalette, out List<ImageImport.Report> reports)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(sharedPalette);

        // An indexed sheet already carries one palette for every cell, so there
        // is nothing to substitute: the sheet's own wins whatever the caller
        // passes. Same rule as GifImport's overload, where the palette is used
        // only when the plan asks for the image's own.
        if (img.Indices is not null)
            return BuildIndexed(img, layout.Cells, layout.Background, plan, out reports);

        return BuildColor(img, layout.Cells, layout.Background, plan, sharedPalette, out reports);
    }

    private static List<GifImport.DecodedFrame> ColorFrames(
        Png.Image img, IReadOnlyList<SheetCell> cells, Rgba? background)
    {
        var bgra = ImageImport.ToBgra(img);
        RequireLength(bgra.Length, img.Width * img.Height * 4, "true-color");
        RequireCells(cells, img.Width, img.Height);

        // Clear the key color inside each box so holes do not become picture
        var frames = new List<GifImport.DecodedFrame>(cells.Count);
        foreach (var cell in cells)
        {
            var crop = CropBgra(bgra, img.Width, cell);
            ClearBackground(crop, background);
            frames.Add(new GifImport.DecodedFrame(cell.Width, cell.Height, crop, 0));
        }
        return frames;
    }

    /// <summary>
    /// Builds the cells of a true-color sheet. The GIF frame shape is reused for
    /// the shared palette and the too-many-colors answer, but not for its pixel
    /// handling: a GIF's alpha is only ever 0 or 255, which is not true of a
    /// PNG, so a soft-edged sheet would come back with its edges hardened.
    /// </summary>
    private static List<Sprite> BuildColor(Png.Image img, IReadOnlyList<SheetCell> cells, Rgba? background,
        ImportPlan plan, Palette? sharedPalette, out List<ImageImport.Report> reports)
    {
        var frames = ColorFrames(img, cells, background);

        if (sharedPalette is not null)
            return GifImport.ToSprites(frames, plan, sharedPalette, out reports);

        if (plan.TrueColor)
        {
            // 32-bit color is meant to keep every pixel exactly, so the GIF path
            // (which forces alpha to 0 or 255) is bypassed for it.
            reports = new List<ImageImport.Report>(frames.Count);
            var kept = new List<Sprite>(frames.Count);
            foreach (var f in frames)
            {
                kept.Add(ImageImport.FromImage(PngExport.Encode(f.Bgra, f.Width, f.Height), plan, out var report));
                reports.Add(report);
            }
            return kept;
        }

        return GifImport.ToSprites(frames, plan, out reports);
    }

    /// <summary>Makes every pixel of <paramref name="background"/> transparent.</summary>
    private static void ClearBackground(byte[] bgra, Rgba? background)
    {
        if (background is not { } bg) return;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
            if (SameColor(bgra, i, bg)) bgra[i + 3] = 0;
    }

    /// <summary>An indexed sheet keeps its indexes: every cell is re-encoded against the
    /// sheet's own palette, which is what lets the import dialog offer to keep
    /// them. The cells share one palette instance rather than a clone each.
    /// </summary>
    private static List<Sprite> BuildIndexed(Png.Image img, IReadOnlyList<SheetCell> cells, Rgba? background,
        ImportPlan plan, out List<ImageImport.Report> reports)
    {
        var plte = img.Plte ?? throw new InvalidDataException("png: indexed image with no palette");
        var indices = img.Indices!;
        RequireLength(indices.Length, img.Width * img.Height, "indexed");
        RequireCells(cells, img.Width, img.Height);

        reports = new List<ImageImport.Report>(cells.Count);
        var sprites = new List<Sprite>(cells.Count);

        foreach (var c in cells)
        {
            var crop = Crop(indices, img.Width, c);
            // The guessed background is cleared here as well, so a key color
            // inside a sprite's hole is not imported as picture.
            ClearBackground(crop, plte, background);
            // Re-encoded as a PNG only to reuse ImageImport's pixel handling;
            // the plan decides the result.
            var png = PngExport.EncodeIndexed(crop, c.Width, c.Height, plte);
            sprites.Add(ImageImport.FromImage(png, plan, out var report));
            reports.Add(report);
        }

        if (!plan.TrueColor && plan.OwnPalette)
        {
            // An imported palette is copied out of the PNG, so point every cell
            // at the one instance they share.
            var shared = sprites.Count > 0 ? sprites[0].Palette! : null;
            foreach (var s in sprites) s.Palette = shared;
        }
        return sprites;
    }

    /// <summary>Moves every pixel of the guessed background onto the mask index.</summary>
    private static void ClearBackground(byte[] indices, Rgba[] plte, Rgba? background)
    {
        if (background is not { } bg) return;
        for (int i = 0; i < indices.Length; i++)
            if (indices[i] < plte.Length && plte[indices[i]] == bg) indices[i] = Sprite.TransparentIndex;
    }

    /// <summary>Every cell has to be inside the sheet, since a crop copies it out.</summary>
    private static void RequireCells(IReadOnlyList<SheetCell> cells, int width, int height)
    {
        foreach (var c in cells)
            if (c.X < 0 || c.Y < 0 || c.Width <= 0 || c.Height <= 0
                || c.X + c.Width > width || c.Y + c.Height > height)
                throw new ArgumentException(
                    $"a sprite at ({c.X},{c.Y}) {c.Width}x{c.Height} does not fit the {width}x{height} sheet it was detected from",
                    nameof(cells));
    }

    private static byte[] Crop(byte[] src, int sheetWidth, SheetCell c)
    {
        var dst = new byte[c.Width * c.Height];
        for (int y = 0; y < c.Height; y++)
            Array.Copy(src, (c.Y + y) * sheetWidth + c.X, dst, y * c.Width, c.Width);
        return dst;
    }

    private static byte[] CropBgra(byte[] src, int sheetWidth, SheetCell c)
    {
        var dst = new byte[c.Width * c.Height * 4];
        for (int y = 0; y < c.Height; y++)
            Array.Copy(src, ((c.Y + y) * sheetWidth + c.X) * 4, dst, y * c.Width * 4, c.Width * 4);
        return dst;
    }

    /// <summary>Formats a color the way the status line and the sheet dialog show it.</summary>
    public static string Describe(Rgba c) => $"RGB({c.R}, {c.G}, {c.B})";
}