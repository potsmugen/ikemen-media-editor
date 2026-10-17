namespace Sff;

/// <summary>
/// Generates Clsn-style boxes covering a sprite's opaque pixels. The sprite is
/// first cut into vertical parts at empty rows — a standing character's head,
/// torso and legs, say — and each part becomes the tight bounding box of its
/// own pixels. A band box deliberately bridges the gaps inside its part, like
/// the space between a standing character's legs, which per-pixel rectangles
/// can't do. Parts are then merged or split to reach the requested box count
/// (greedily and by a beam search, keeping the tighter result). The
/// layout restricts the cuts: Stacked only cuts across rows (bands on top of
/// each other), SideBySide only down columns (the same search on the sprite
/// turned sideways), Auto takes whichever cut wastes least.
/// A starting point for hand-made Clsn2, deliberately with few knobs (see
/// AGENTS.md for the minimum-box-size experiments that were dropped).
/// </summary>
/// <summary>Stacked: boxes on top of each other. SideBySide: next to each other. Auto: least waste.</summary>
public enum CoverLayout { Stacked, SideBySide, Auto }

public static class SpriteCover
{
    public readonly record struct Rect(int X1, int Y1, int X2, int Y2);

    /// <param name="alphaThreshold">32-bit pixels with alpha below this count as transparent.</param>
    public static List<Rect> Cover(Sprite sprite, int maxBoxes, CoverLayout layout = CoverLayout.Stacked, byte alphaThreshold = 1)
    {
        int w = sprite.Width, h = sprite.Height;
        if (w <= 0 || h <= 0 || !sprite.HasPixels) return [];
        maxBoxes = Math.Max(1, maxBoxes);

        var opaque = OpaquePixels(sprite, w, h, alphaThreshold);
        if (opaque is null) return [];
        if (Array.TrueForAll(opaque, o => o)) return EvenSplit(w, h, maxBoxes, layout, -sprite.AxisX, -sprite.AxisY);
        return CoverMask(opaque, w, h, maxBoxes, layout, -sprite.AxisX, -sprite.AxisY);
    }

    /// <summary>A sprite with no transparency: every cut wastes nothing, so the search would peel off
    /// 1px strips. Instead, equal strips with shared seams: Stacked bands, Side by side columns, Auto
    /// along the longer side (bands when square). At most one strip per pixel row or column.</summary>
    private static List<Rect> EvenSplit(int w, int h, int maxBoxes, CoverLayout layout, int offsetX, int offsetY)
    {
        bool columns = layout == CoverLayout.SideBySide || (layout == CoverLayout.Auto && w > h);
        int length = columns ? w : h;
        int n = Math.Clamp(maxBoxes, 1, length);
        var result = new List<Rect>(n);
        for (int i = 0; i < n; i++)
        {
            // Each strip starts on the previous strip's last pixel: the seam is shared, as in CoverMask.
            int start = i == 0 ? 0 : i * length / n - 1;
            int end = (i + 1) * length / n - 1;
            result.Add(columns
                ? new Rect(start + offsetX, offsetY, end + offsetX, h - 1 + offsetY)
                : new Rect(offsetX, start + offsetY, w - 1 + offsetX, end + offsetY));
        }
        return result;
    }

    /// <summary>Generates boxes around the sprite after the frame's flip, scale,
    /// and rotation, returning coordinates relative to the sprite's local axis.</summary>
    public static List<Rect> CoverTransformed(Sprite sprite, int maxBoxes, CoverLayout layout,
        bool flipH, bool flipV, double scaleX, double scaleY, double angle, byte alphaThreshold = 1)
    {
        int w = sprite.Width, h = sprite.Height;
        if (w <= 0 || h <= 0 || !sprite.HasPixels) return [];
        maxBoxes = Math.Max(1, maxBoxes);
        if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY) || !double.IsFinite(angle))
            throw new ArgumentOutOfRangeException(nameof(scaleX), "The frame transform must contain finite values.");
        if (scaleX == 0 || scaleY == 0) return [];

        var source = OpaquePixels(sprite, w, h, alphaThreshold);
        if (source is null) return [];

        double sx = scaleX * (flipH ? -1 : 1);
        double sy = scaleY * (flipV ? -1 : 1);
        double rad = -angle * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!source[y * w + x]) continue;
                var (tx, ty) = TransformPixel(x, y);
                minX = Math.Min(minX, tx); minY = Math.Min(minY, ty);
                maxX = Math.Max(maxX, tx); maxY = Math.Max(maxY, ty);
            }

        long transformedWidth = (long)maxX - minX + 1;
        long transformedHeight = (long)maxY - minY + 1;
        if (transformedWidth <= 0 || transformedHeight <= 0
            || transformedWidth > int.MaxValue / transformedHeight)
            throw new InvalidOperationException("The transformed sprite is too large to generate Clsn2 boxes.");

        int transformedLength = (int)(transformedWidth * transformedHeight);
        int tw = (int)transformedWidth, th = (int)transformedHeight;
        // No transparency and a right-angle turn: the result is still an upright rectangle.
        if (Math.Abs(Math.IEEERemainder(angle, 90)) < 1e-9 && Array.TrueForAll(source, o => o))
            return EvenSplit(tw, th, maxBoxes, layout, minX, minY);
        var transformed = new bool[transformedLength];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!source[y * w + x]) continue;
                var (tx, ty) = TransformPixel(x, y);
                transformed[(ty - minY) * tw + (tx - minX)] = true;
            }

        return CoverMask(transformed, tw, th, maxBoxes, layout, minX, minY);

        (int X, int Y) TransformPixel(int x, int y)
        {
            double localX = (double)x - sprite.AxisX, localY = (double)y - sprite.AxisY;
            double transformedX = cos * sx * localX - sin * sy * localY;
            double transformedY = sin * sx * localX + cos * sy * localY;
            if (!double.IsFinite(transformedX) || !double.IsFinite(transformedY)
                || transformedX < int.MinValue || transformedX > int.MaxValue
                || transformedY < int.MinValue || transformedY > int.MaxValue)
                throw new InvalidOperationException("The transformed sprite is outside the supported coordinate range.");
            return ((int)Math.Round(transformedX, MidpointRounding.AwayFromZero),
                    (int)Math.Round(transformedY, MidpointRounding.AwayFromZero));
        }
    }

    private static bool[]? OpaquePixels(Sprite sprite, int w, int h, byte alphaThreshold)
    {
        var opaque = new bool[checked(w * h)];
        var indexed = sprite.Indexed;
        var direct = sprite.Direct;
        bool any = false;
        for (int i = 0; i < opaque.Length; i++)
        {
            bool o = indexed is not null
                ? indexed[i] != Sprite.TransparentIndex
                : direct![i * 4 + 3] >= alphaThreshold; // BGRA: alpha is the 4th byte
            if (o) { opaque[i] = true; any = true; }
        }
        return any ? opaque : null;
    }

    private static List<Rect> CoverMask(bool[] opaque, int w, int h, int maxBoxes,
        CoverLayout layout, int offsetX, int offsetY)
    {
        var result = new List<Rect>();

        List<Part> parts;
        if (layout == CoverLayout.SideBySide)
        {
            // Columns are rows of the sprite turned sideways: transpose, cover, transpose back.
            var turned = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    turned[x * h + y] = opaque[y * w + x];
            parts = BestCover(turned, h, w, maxBoxes, horizontalOnly: true);
            foreach (var p in parts) (p.X1, p.Y1, p.X2, p.Y2) = (p.Y1, p.X1, p.Y2, p.X2);
        }
        else
            parts = BestCover(opaque, w, h, maxBoxes, horizontalOnly: layout == CoverLayout.Stacked);

        // Close 1px seams: boxes that merely touch — one ends at 40, the
        // next starts at 41 — are made to share the coordinate, so there is
        // no gap for an attack to slip through. Only pairs that meet
        // side-to-side are extended; a diagonal neighbor would otherwise
        // grow into empty air. Starts move, ends never do, so each seam
        // closes exactly once.
        for (int i = 0; i < parts.Count; i++)
            for (int j = i + 1; j < parts.Count; j++)
            {
                bool xOverlap = parts[i].X1 <= parts[j].X2 && parts[j].X1 <= parts[i].X2;
                bool yOverlap = parts[i].Y1 <= parts[j].Y2 && parts[j].Y1 <= parts[i].Y2;
                if (parts[i].Y2 + 1 == parts[j].Y1 && xOverlap) parts[j].Y1 = parts[i].Y2;
                if (parts[j].Y2 + 1 == parts[i].Y1 && xOverlap) parts[i].Y1 = parts[j].Y2;
                if (parts[i].X2 + 1 == parts[j].X1 && yOverlap) parts[j].X1 = parts[i].X2;
                if (parts[j].X2 + 1 == parts[i].X1 && yOverlap) parts[i].X1 = parts[j].X2;
            }

        foreach (var p in parts)
            result.Add(new Rect(p.X1 + offsetX, p.Y1 + offsetY,
                                p.X2 + offsetX, p.Y2 + offsetY));
        return result;
    }

    /// <summary>Today's greedy cover and a beam search over the same moves; the one
    /// enclosing less empty space wins (greedy on a tie), so it's never worse.
    /// </summary>
    private static List<Part> BestCover(bool[] opaque, int w, int h, int maxBoxes, bool horizontalOnly)
    {
        var greedy = Greedy(opaque, w, h, maxBoxes, horizontalOnly);
        var beam = Beam(opaque, w, h, maxBoxes, horizontalOnly);
        return beam.Count >= greedy.Count && beam.Sum(p => p.Waste) < greedy.Sum(p => p.Waste) ? beam : greedy;
    }

    /// <summary>Atomic parts: maximal runs of consecutive rows that hold pixels, top to bottom.</summary>
    private static List<Part> AtomicParts(bool[] opaque, int w, int h)
    {
        var parts = new List<Part>();
        for (int y = 0; y < h; )
        {
            if (!RowHas(opaque, w, y)) { y++; continue; }
            int start = y;
            while (y < h && RowHas(opaque, w, y)) y++;
            parts.Add(Part.Scan(opaque, w, start, y - 1)!);
        }
        return parts;
    }

    /// <summary>One merge or split at a time, always the one that looks best now.</summary>
    private static List<Part> Greedy(bool[] opaque, int w, int h, int maxBoxes, bool horizontalOnly)
    {
        var parts = AtomicParts(opaque, w, h);

        // Too many parts: merge the pairs that fit together best.
        while (parts.Count > maxBoxes)
        {
            int bi = BestPair(parts, horizontalOnly);
            parts[bi] = Merge(parts[bi], parts[bi + 1]);
            parts.RemoveAt(bi + 1);
        }

        // Too few: split up to the count, each round taking the cut that
        // removes the most waste (only horizontal cuts when horizontalOnly).
        while (parts.Count < maxBoxes)
        {
            Part? bestPart = null;
            Part bestA = null!, bestB = null!;
            int bestOverall = int.MinValue;

            foreach (var p in parts)
            {
                if (TryBestSplit(p, opaque, w, horizontalOnly, out var a, out var b, out int childrenWaste))
                {
                    int reduction = p.Waste - childrenWaste;
                    if (reduction > bestOverall)
                    {
                        bestOverall = reduction; bestPart = p; bestA = a; bestB = b;
                    }
                }
            }

            if (bestPart == null) break;
            int idx = parts.IndexOf(bestPart);
            parts[idx] = bestA;
            parts.Insert(idx + 1, bestB);
        }
        return parts;
    }

    // Beam search: keeps the BeamWidth least-wasteful partial covers at each
    // step, trying every adjacent merge, or each part's CutsPerPart best cuts.
    // Width 8 got nearly all the gain of 32 in the Python prototype.
    private const int BeamWidth = 8, CutsPerPart = 4;

    /// <summary>
    /// The same moves as Greedy, but keeping several candidates: greedy's
    /// first cut can rule out a better set of boxes, mostly at 6+ boxes.
    /// horizontalOnly follows greedy's rules: only wide merges when there is one,
    /// and only horizontal cuts.
    /// </summary>
    private static List<Part> Beam(bool[] opaque, int w, int h, int maxBoxes, bool horizontalOnly)
    {
        var states = new List<List<Part>> { AtomicParts(opaque, w, h) };

        while (states[0].Count > maxBoxes)
        {
            var next = new Dictionary<string, (List<Part> Parts, int Waste)>();
            foreach (var s in states)
            {
                foreach (var t in Merges(s, horizontalOnly))
                    AddState(next, t, t.Sum(p => p.Waste));
            }
            states = Keep(next);
        }

        var cuts = new Dictionary<Part, List<(int Waste, Part A, Part B)>>(); // parts are shared between states
        while (states[0].Count < maxBoxes)
        {
            var next = new Dictionary<string, (List<Part> Parts, int Waste)>();
            foreach (var s in states)
            {
                int baseWaste = s.Sum(p => p.Waste);
                for (int i = 0; i < s.Count; i++)
                {
                    var p = s[i];
                    if (!cuts.TryGetValue(p, out var mine)) cuts[p] = mine = BestCuts(p, opaque, w, horizontalOnly);
                    foreach (var (childWaste, a, b) in mine)
                    {
                        var t = new List<Part>(s);
                        t[i] = a;
                        t.Insert(i + 1, b);
                        AddState(next, t, baseWaste - p.Waste + childWaste);
                    }
                }
            }
            if (next.Count == 0) break; // nothing left to split
            states = Keep(next);
        }
        return states.MinBy(s => s.Sum(p => p.Waste))!;
    }

    /// <summary>A part's CutsPerPart least-wasteful cuts.</summary>
    private static List<(int Waste, Part A, Part B)> BestCuts(Part p, bool[] opaque, int w, bool horizontalOnly) =>
        AllCuts(p, opaque, w, horizontalOnly)
            .OrderBy(c => c.Waste).Take(CutsPerPart).Select(c => (c.Waste, c.A, c.B)).ToList();

    /// <summary>Every adjacent merge of a top-to-bottom part list; with horizontalOnly,
    /// only the ones giving a box at least as wide as tall, when there are any.</summary>
    private static List<List<Part>> Merges(List<Part> s, bool horizontalOnly)
    {
        var merges = new List<(bool Wide, int At, Part Merged)>();
        for (int i = 0; i + 1 < s.Count; i++)
        {
            var m = Merge(s[i], s[i + 1]);
            merges.Add((m.X2 - m.X1 >= m.Y2 - m.Y1, i, m));
        }
        if (horizontalOnly && merges.Any(x => x.Wide)) merges.RemoveAll(x => !x.Wide);
        return merges.Select(x =>
        {
            var t = new List<Part>(s);
            t[x.At] = x.Merged;
            t.RemoveAt(x.At + 1);
            return t;
        }).ToList();
    }

    private static string Key(List<Part> parts) =>
        string.Join(";", parts.Select(p => $"{p.X1},{p.Y1},{p.X2},{p.Y2}").Order(StringComparer.Ordinal));

    /// <summary>Adds a candidate unless the same set of boxes is already there.</summary>
    private static void AddState(Dictionary<string, (List<Part> Parts, int Waste)> next, List<Part> parts, int waste)
    {
        next.TryAdd(Key(parts), (parts, waste));
    }

    private static List<List<Part>> Keep(Dictionary<string, (List<Part> Parts, int Waste)> next) =>
        next.Values.OrderBy(v => v.Waste).Take(BeamWidth).Select(v => v.Parts).ToList();

    /// <summary>
    /// Adjacent pair that fits together best: with horizontalOnly, pairs whose
    /// merged box is wider than tall come first, then highest merged fill.
    /// </summary>
    private static int BestPair(List<Part> parts, bool horizontalOnly)
    {
        int bi = -1;
        double bestWide = -1, bestFill = -1;
        for (int i = 0; i + 1 < parts.Count; i++)
        {
            var a = parts[i];
            var b = parts[i + 1];
            double fill = MergedFill(a, b);
            int mw = Math.Max(a.X2, b.X2) - Math.Min(a.X1, b.X1) + 1;
            int mh = b.Y2 - a.Y1 + 1;
            double wide = horizontalOnly && mw >= mh ? 1 : 0;
            if (wide > bestWide || (wide == bestWide && fill > bestFill))
            {
                bestWide = wide; bestFill = fill; bi = i;
            }
        }
        return bi;
    }

    private static double MergedFill(Part a, Part b) =>
        (a.Count + b.Count)
        / (double)((Math.Max(a.X2, b.X2) - Math.Min(a.X1, b.X1) + 1) * (b.Y2 - a.Y1 + 1));

    private static Part Merge(Part a, Part b) => new()
    {
        Y1 = a.Y1, Y2 = b.Y2,
        X1 = Math.Min(a.X1, b.X1), X2 = Math.Max(a.X2, b.X2),
        Count = a.Count + b.Count,
    };

    private static bool RowHas(bool[] opaque, int w, int y)
    {
        int baseIdx = y * w;
        for (int x = 0; x < w; x++)
            if (opaque[baseIdx + x]) return true;
        return false;
    }

    /// <summary>
    /// One vertical part of the sprite: a row range plus its cached pixel
    /// bounding box. Endpoints always hold pixels; internal rows may not
    /// (merged parts span the empty gap between their halves).
    /// </summary>
    private sealed class Part
    {
        public int Y1, Y2;
        public int X1, X2;
        public int Count;

        public int Area => (X2 - X1 + 1) * (Y2 - Y1 + 1);
        public int Waste => Area - Count;
        public double Fill => Count / (double)Area;

        /// <summary>Scans rows y1..y2, trimming the range down to rows that hold pixels.</summary>
        public static Part? Scan(bool[] opaque, int w, int y1, int y2)
        {
            while (y1 <= y2 && !RowHas(opaque, w, y1)) y1++;
            while (y2 >= y1 && !RowHas(opaque, w, y2)) y2--;
            if (y1 > y2) return null;
            var p = new Part { Y1 = y1, Y2 = y2, X1 = int.MaxValue, X2 = -1 };
            for (int y = y1; y <= y2; y++)
            {
                int baseIdx = y * w;
                for (int x = 0; x < w; x++)
                    if (opaque[baseIdx + x])
                    {
                        if (x < p.X1) p.X1 = x;
                        if (x > p.X2) p.X2 = x;
                        p.Count++;
                    }
            }
            return p;
        }
    }

    /// <summary>
    /// Finds the split of p into two parts that wastes the least, over
    /// horizontal cuts (between rows) and, unless horizontalOnly, vertical
    /// cuts (between columns). Children come back with trimmed ranges and
    /// cached bounds. The first of equally good cuts wins, horizontal first.
    /// </summary>
    private static bool TryBestSplit(Part p, bool[] opaque, int w, bool horizontalOnly,
        out Part a, out Part b, out int childrenWaste)
    {
        a = null!; b = null!;
        childrenWaste = int.MaxValue;
        foreach (var c in AllCuts(p, opaque, w, horizontalOnly))
            if (c.Waste < childrenWaste) (childrenWaste, a, b) = (c.Waste, c.A, c.B);
        return childrenWaste != int.MaxValue;
    }

    /// <summary>
    /// Every cut of p into two non-empty parts: horizontal cuts (between rows)
    /// top to bottom, then, unless horizontalOnly, vertical ones left to
    /// right; each with its children's total waste.
    /// </summary>
    private static List<(int Waste, Part A, Part B, bool Horizontal)> AllCuts(Part p, bool[] opaque, int w, bool horizontalOnly)
    {
        var result = new List<(int Waste, Part A, Part B, bool Horizontal)>();

        // Row profiles, then horizontal cuts between rows.
        int rows = p.Y2 - p.Y1 + 1;
        var cnt = new int[rows];
        var xmin = new int[rows];
        var xmax = new int[rows];
        for (int y = 0; y < rows; y++)
        {
            xmin[y] = int.MaxValue;
            xmax[y] = -1;
            int baseIdx = (p.Y1 + y) * w;
            for (int x = p.X1; x <= p.X2; x++)
                if (opaque[baseIdx + x])
                {
                    cnt[y]++;
                    if (x < xmin[y]) xmin[y] = x;
                    if (x > xmax[y]) xmax[y] = x;
                }
        }
        // Suffix aggregates for the bottom child of a cut after row i.
        var sCnt = new int[rows + 1];
        var sXmin = new int[rows + 1];
        var sXmax = new int[rows + 1];
        var sFirst = new int[rows + 1];
        sXmin[rows] = int.MaxValue;
        sXmax[rows] = -1;
        for (int i = rows - 1; i >= 0; i--)
        {
            sCnt[i] = sCnt[i + 1] + cnt[i];
            sXmin[i] = Math.Min(cnt[i] > 0 ? xmin[i] : int.MaxValue, sXmin[i + 1]);
            sXmax[i] = Math.Max(xmax[i], sXmax[i + 1]);
            sFirst[i] = cnt[i] > 0 ? i : sFirst[i + 1];
        }
        int tCnt = 0, tXmin = int.MaxValue, tXmax = -1, tLast = -1;
        for (int i = 0; i < rows - 1; i++)
        {
            if (cnt[i] > 0)
            {
                tCnt += cnt[i];
                if (xmin[i] < tXmin) tXmin = xmin[i];
                if (xmax[i] > tXmax) tXmax = xmax[i];
                tLast = i;
            }
            if (tCnt == 0 || sCnt[i + 1] == 0) continue;
            int topWaste = (tXmax - tXmin + 1) * (tLast + 1) - tCnt;
            int botWaste = (sXmax[i + 1] - sXmin[i + 1] + 1) * (rows - sFirst[i + 1]) - sCnt[i + 1];
            result.Add((topWaste + botWaste,
                new Part { Y1 = p.Y1, Y2 = p.Y1 + tLast, X1 = tXmin, X2 = tXmax, Count = tCnt },
                new Part { Y1 = p.Y1 + sFirst[i + 1], Y2 = p.Y2, X1 = sXmin[i + 1], X2 = sXmax[i + 1], Count = sCnt[i + 1] },
                true));
        }

        if (horizontalOnly) return result;

        // Column profiles, then vertical cuts between columns.
        int cols = p.X2 - p.X1 + 1;
        var ccnt = new int[cols];
        var cymin = new int[cols];
        var cymax = new int[cols];
        for (int x = 0; x < cols; x++)
        {
            cymin[x] = int.MaxValue;
            cymax[x] = -1;
            for (int y = p.Y1; y <= p.Y2; y++)
                if (opaque[y * w + p.X1 + x])
                {
                    ccnt[x]++;
                    if (y < cymin[x]) cymin[x] = y;
                    if (y > cymax[x]) cymax[x] = y;
                }
        }
        var rCnt = new int[cols + 1];
        var rYmin = new int[cols + 1];
        var rYmax = new int[cols + 1];
        var rFirst = new int[cols + 1];
        rYmin[cols] = int.MaxValue;
        rYmax[cols] = -1;
        for (int i = cols - 1; i >= 0; i--)
        {
            rCnt[i] = rCnt[i + 1] + ccnt[i];
            rYmin[i] = Math.Min(ccnt[i] > 0 ? cymin[i] : int.MaxValue, rYmin[i + 1]);
            rYmax[i] = Math.Max(cymax[i], rYmax[i + 1]);
            rFirst[i] = ccnt[i] > 0 ? i : rFirst[i + 1];
        }
        int lCnt = 0, lYmin = int.MaxValue, lYmax = -1, lLast = -1;
        for (int i = 0; i < cols - 1; i++)
        {
            if (ccnt[i] > 0)
            {
                lCnt += ccnt[i];
                if (cymin[i] < lYmin) lYmin = cymin[i];
                if (cymax[i] > lYmax) lYmax = cymax[i];
                lLast = i;
            }
            if (lCnt == 0 || rCnt[i + 1] == 0) continue;
            int leftWaste = (lLast + 1) * (lYmax - lYmin + 1) - lCnt;
            int rightWaste = (cols - rFirst[i + 1]) * (rYmax[i + 1] - rYmin[i + 1] + 1) - rCnt[i + 1];
            result.Add((leftWaste + rightWaste,
                new Part { Y1 = lYmin, Y2 = lYmax, X1 = p.X1, X2 = p.X1 + lLast, Count = lCnt },
                new Part { Y1 = rYmin[i + 1], Y2 = rYmax[i + 1], X1 = p.X1 + rFirst[i + 1], X2 = p.X2, Count = rCnt[i + 1] },
                false));
        }

        return result;
    }
}
