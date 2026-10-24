using System.Numerics;

namespace Sff;

/// <summary>
/// Palette template sheet: the 16×16 swatch grid of PalettePngFile at the top left, then sprites picked
/// so every index they use shows, packed in rows, written as one PNG indexed to the palette.
/// </summary>
public static class PaletteTemplate
{
    /// <summary>A sprite's visible (nonzero) rectangle and the indexes it uses. It points into the
    /// sprite's own bitmap rather than copying it; Build copies only the pieces it places.</summary>
    public sealed class Piece
    {
        public required byte[] Source { get; init; }
        public required int SourceWidth { get; init; }
        public required int X { get; init; }
        public required int Y { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }

        /// <summary>Bit set of the nonzero indexes used, 256 bits.</summary>
        public required ulong[] Used { get; init; }
    }

    /// <summary>The chosen pieces (positions in the input), indexes they cover, indexes any piece uses,
    /// and how many pieces the greedy cover needs for all of them.</summary>
    public sealed record Selection(List<int> Chosen, int Covered, int Used, int NeededForAll);

    /// <summary>An index buffer: 0 is the background (the mask).</summary>
    public sealed record Sheet(byte[] Indices, int Width, int Height);

    /// <summary>Finds an indexed bitmap's nonzero rectangle; null when it has none. Keeps a reference to indexed.</summary>
    public static Piece? Trim(byte[] indexed, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1;
        var used = new ulong[4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte v = indexed[y * width + x];
                if (v == 0) continue;
                used[v >> 6] |= 1UL << (v & 63);
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        if (right < 0) return null;

        return new Piece
        {
            Source = indexed, SourceWidth = width, X = left, Y = top,
            Width = right - left + 1, Height = bottom - top + 1, Used = used,
        };
    }

    /// <summary>
    /// Greedy cover first (most new indexes, random ties), then random pieces up to count.
    /// A valid first piece is always taken and starts the cover. The same seed gives the same selection.
    /// </summary>
    public static Selection Choose(IReadOnlyList<Piece> pieces, int count, int seed, int? first = null)
    {
        var rng = new Random(seed);
        count = Math.Clamp(count, 0, pieces.Count);
        var all = new ulong[4];
        foreach (var p in pieces) Or(all, p.Used);

        var remaining = (ulong[])all.Clone();
        var taken = new bool[pieces.Count];
        var cover = new List<int>();
        var ties = new List<int>();
        if (first is int f && f >= 0 && f < pieces.Count && count > 0)
        {
            taken[f] = true;
            cover.Add(f);
            for (int k = 0; k < 4; k++) remaining[k] &= ~pieces[f].Used[k];
        }
        while (PopCount(remaining) > 0)
        {
            int best = 0;
            ties.Clear();
            for (int i = 0; i < pieces.Count; i++)
            {
                if (taken[i]) continue;
                int gain = 0;
                for (int k = 0; k < 4; k++) gain += BitOperations.PopCount(pieces[i].Used[k] & remaining[k]);
                if (gain == 0 || gain < best) continue;
                if (gain > best) { best = gain; ties.Clear(); }
                ties.Add(i);
            }
            int pick = ties[rng.Next(ties.Count)];
            taken[pick] = true;
            cover.Add(pick);
            for (int k = 0; k < 4; k++) remaining[k] &= ~pieces[pick].Used[k];
        }

        var chosen = cover.Take(count).ToList();
        if (chosen.Count < count)
        {
            var rest = Enumerable.Range(0, pieces.Count).Where(i => !taken[i]).ToArray();
            rng.Shuffle(rest);
            chosen.AddRange(rest.Take(count - chosen.Count));
        }

        var covered = new ulong[4];
        foreach (int i in chosen) Or(covered, pieces[i].Used);
        return new Selection(chosen, PopCount(covered), PopCount(all), cover.Count);
    }

    /// <summary>
    /// Puts the 16×16 swatch grid (swatchCell pixels per entry) at the top left, then packs the
    /// pieces in rows after it, tallest first, padding pixels apart and from the edges.
    /// A valid first piece goes right after the swatch, ahead of the others.
    /// </summary>
    public static Sheet Build(IReadOnlyList<Piece> pieces, int padding, int swatchCell, int? first = null)
    {
        int swatch = 16 * swatchCell;
        int lead = first is int f && f >= 0 && f < pieces.Count ? f : -1;
        var order = pieces.Select((p, i) => (p, i)).OrderByDescending(t => t.i == lead).ThenByDescending(t => t.p.Height)
            .ThenBy(t => t.i).Select(t => t.p).ToList();
        long area = (long)(swatch + padding) * (swatch + padding)
            + order.Sum(p => (long)(p.Width + padding) * (p.Height + padding));
        int widest = order.Count == 0 ? 0 : order.Max(p => p.Width);
        int target = Math.Max(Math.Max(widest, swatch), (int)Math.Ceiling(Math.Sqrt(area) * 1.25));

        // The swatch is the first item of the first row.
        var places = new List<(Piece P, int X, int Y)>();
        int x = padding + swatch + padding, y = padding, rowHeight = swatch, usedWidth = swatch;
        foreach (var p in order)
        {
            if (x > padding && x + p.Width > padding + target)
            {
                y += rowHeight + padding;
                x = padding;
                rowHeight = 0;
            }
            places.Add((p, x, y));
            usedWidth = Math.Max(usedWidth, x + p.Width - padding);
            x += p.Width + padding;
            rowHeight = Math.Max(rowHeight, p.Height);
        }

        int width = padding * 2 + usedWidth;
        int height = y + rowHeight + padding;
        var indices = new byte[width * height];
        for (int sy = 0; sy < swatch; sy++)
            for (int sx = 0; sx < swatch; sx++)
                indices[(padding + sy) * width + padding + sx] = (byte)(sy / swatchCell * 16 + sx / swatchCell);
        foreach (var (p, px, py) in places)
            for (int row = 0; row < p.Height; row++)
                Array.Copy(p.Source, (p.Y + row) * p.SourceWidth + p.X, indices, (py + row) * width + px, p.Width);
        return new Sheet(indices, width, height);
    }

    /// <summary>The sheet as an indexed PNG; entries past the palette's end are transparent black, as in PalettePngFile.</summary>
    public static byte[] Encode(Sheet sheet, Rgba[] colors) =>
        Png.EncodeIndexed(sheet.Indices, sheet.Width, sheet.Height, Padded(colors));

    /// <summary>The sheet as BGRA through the same 256 colors the PNG gets.</summary>
    public static byte[] ToBgra(Sheet sheet, Rgba[] colors)
    {
        var table = Padded(colors);
        var bgra = new byte[sheet.Indices.Length * 4];
        for (int i = 0; i < sheet.Indices.Length; i++)
        {
            var c = table[sheet.Indices[i]];
            (bgra[i * 4], bgra[i * 4 + 1], bgra[i * 4 + 2], bgra[i * 4 + 3]) = (c.B, c.G, c.R, c.A);
        }
        return bgra;
    }

    private static Rgba[] Padded(Rgba[] colors)
    {
        var table = new Rgba[256];
        Array.Copy(colors, table, Math.Min(colors.Length, 256));
        return table;
    }

    private static void Or(ulong[] into, ulong[] bits)
    {
        for (int k = 0; k < 4; k++) into[k] |= bits[k];
    }

    private static int PopCount(ulong[] bits)
    {
        int n = 0;
        foreach (ulong b in bits) n += BitOperations.PopCount(b);
        return n;
    }
}
