namespace Sff;

/// <summary>
/// Combines several palettes into one, every entry except those marked as padding. Identical RGBA
/// colors share an entry; over 256 (with the mask) are reduced by frequency-weighted median cut. Colors keep their old index when that slot is
/// still free (earlier palettes win), the rest fill free slots most used first.
/// Index 0 is the first palette's mask; free slots get the fill color.
/// </summary>
public static class PaletteMerge
{
    /// <summary>
    /// A palette to merge. Weights[i] is how often index i is used (at least 1 is assumed);
    /// Skip[i] leaves index i out as padding.
    /// </summary>
    public sealed record Source(Rgba[] Colors, long[]? Weights = null, bool[]? Skip = null);

    /// <summary>
    /// The merged colors, and for each source a 256-entry map from its old indexes to new ones.
    /// UniqueColors counts distinct colors before reduction.
    /// </summary>
    public sealed record Result(Rgba[] Colors, byte[][] Maps, int UniqueColors, bool Reduced);

    /// <summary>Fill colors the slots no color takes; null keeps the first palette's colors there.</summary>
    public static Result Build(IReadOnlyList<Source> sources, Rgba? fill = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0) throw new ArgumentException("no palettes to merge", nameof(sources));
        if (sources[0].Colors.Length == 0) throw new ArgumentException("the first palette has no colors", nameof(sources));

        // Distinct used colors with their total weight, in first-seen order.
        var lookup = new Dictionary<Rgba, int>();
        var unique = new List<Rgba>();
        var weights = new List<long>();
        var idOf = new int[sources.Count][]; // per source, index -> unique id (-1 unused)
        for (int s = 0; s < sources.Count; s++)
        {
            var src = sources[s];
            int count = Math.Min(src.Colors.Length, 256);
            idOf[s] = new int[count];
            Array.Fill(idOf[s], -1);
            for (int i = 1; i < count; i++)
            {
                if (src.Skip is { } skip && i < skip.Length && skip[i]) continue;
                long w = Math.Max(1, src.Weights is { } ws && i < ws.Length ? ws[i] : 0);
                var c = src.Colors[i];
                if (!lookup.TryGetValue(c, out int id))
                {
                    id = unique.Count;
                    lookup.Add(c, id);
                    unique.Add(c);
                    weights.Add(0);
                }
                weights[id] += w;
                idOf[s][i] = id;
            }
        }
        if (unique.Count == 0) throw new InvalidOperationException("the chosen palettes have no colors left");

        // Group colors: one group each, or the median-cut box they fell into.
        bool reduced = unique.Count > GifImport.MaxSharedColors;
        Rgba[] groupColors;
        int[] groupOf;
        if (reduced)
        {
            var (colors, groups) = Quantize.ReduceGrouped([default, .. unique], [0, .. weights], GifImport.MaxSharedColors);
            groupColors = colors[1..];
            groupOf = groups[1..].Select(g => g - 1).ToArray();
        }
        else
        {
            groupColors = [.. unique];
            groupOf = Enumerable.Range(0, unique.Count).ToArray();
        }
        var groupWeight = new long[groupColors.Length];
        for (int id = 0; id < unique.Count; id++) groupWeight[groupOf[id]] += weights[id];

        // The largest source's size, grown to a power of two only when the colors don't fit.
        int largest = sources.Max(s => Math.Min(s.Colors.Length, 256));
        int length = groupColors.Length + 1 <= largest ? largest : PowerOfTwoAtLeast(groupColors.Length + 1);
        var slotOf = new int[groupColors.Length];
        Array.Fill(slotOf, -1);
        var taken = new bool[length];
        taken[0] = true;

        // Pass 1: keep old indexes where the slot is still free.
        for (int s = 0; s < sources.Count; s++)
            for (int i = 1; i < idOf[s].Length && i < length; i++)
            {
                if (idOf[s][i] < 0) continue;
                int g = groupOf[idOf[s][i]];
                if (slotOf[g] >= 0 || taken[i]) continue;
                slotOf[g] = i;
                taken[i] = true;
            }

        // Pass 2: the rest into free slots, most used first (stable, so ties keep first-seen order).
        int free = 1;
        foreach (int g in Enumerable.Range(0, groupColors.Length).Where(g => slotOf[g] < 0).OrderByDescending(g => groupWeight[g]))
        {
            while (taken[free]) free++;
            slotOf[g] = free;
            taken[free] = true;
        }

        // Untaken slots get fill, else the first palette's color there, else opaque black.
        var first = sources[0].Colors;
        var result = new Rgba[length];
        for (int i = 0; i < length; i++)
            result[i] = i == 0 ? first[0] : fill ?? (i < first.Length ? first[i] : new Rgba(0, 0, 0, 255));
        for (int g = 0; g < groupColors.Length; g++) result[slotOf[g]] = groupColors[g];

        var maps = new byte[sources.Count][];
        for (int s = 0; s < sources.Count; s++)
        {
            var map = new byte[256];
            for (int i = 0; i < 256; i++)
                map[i] = i < idOf[s].Length && idOf[s][i] >= 0 ? (byte)slotOf[groupOf[idOf[s][i]]] : (byte)i;
            maps[s] = map;
        }
        return new Result(result, maps, unique.Count, reduced);
    }

    private static int PowerOfTwoAtLeast(int n)
    {
        int p = 2;
        while (p < n) p <<= 1;
        return Math.Min(p, 256);
    }
}
