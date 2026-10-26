namespace Sff;

/// <summary>
/// Finds how far to move a sprite on the canvas so it lines up with another one, as both
/// are drawn: a pixel at (x, y) sits at (x - AxisX, y - AxisY). Colors are compared as drawn,
/// each sprite through its own palette, so different palettes and kinds still compare.
/// A pixel is visible when its drawn alpha is above 0.
/// </summary>
public static class SpriteAlign
{
    public enum Method { Similarity, CenterOfMass, BoxCenter, BottomCenter }

    /// <summary>A canvas move in pixels (right and down positive). For Similarity, the whole
    /// percent of visible positions whose colors are close after the move.</summary>
    public readonly record struct Move(int X, int Y, int? Percent);

    /// <summary>Two colors are close when their R, G and B differences add up to at most this.</summary>
    internal const int Tolerance = 48;
    /// <summary>Similarity shrinks both sprites by half until the larger side is at most this.</summary>
    internal const int CoarseSize = 48;
    /// <summary>How far each finer level searches around the coarser level's answer.</summary>
    private const int Refine = 2;

    /// <summary>A sprite's visible pixels in their tightest box, placed on the canvas.</summary>
    public sealed class Placed
    {
        internal Level Full = null!;
        internal double MassX, MassY;
    }

    /// <summary>One size of a sprite: a box on that level's canvas grid, packed RGB per pixel,
    /// -1 where transparent.</summary>
    internal sealed class Level
    {
        internal int Left, Top, Width, Height, VisibleCount;
        internal int[] Pixels = [];
    }

    /// <summary>Snapshots a sprite's drawn pixels and position. Null when nothing is visible.</summary>
    public static Placed? Prepare(Sprite sprite)
    {
        int w = sprite.Width, h = sprite.Height;
        if (w <= 0 || h <= 0 || !sprite.HasPixels || sprite.DecodeError is not null) return null;
        var bgra = sprite.ToBgra(sprite.Palette);
        int minX = w, minY = h, maxX = -1, maxY = -1, count = 0;
        double sumX = 0, sumY = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (bgra[(y * w + x) * 4 + 3] == 0) continue;
                count++;
                sumX += x + 0.5;
                sumY += y + 0.5;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        if (count == 0) return null;

        int bw = maxX - minX + 1, bh = maxY - minY + 1;
        var pixels = new int[bw * bh];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                int at = ((minY + y) * w + minX + x) * 4;
                pixels[y * bw + x] = bgra[at + 3] == 0 ? -1 : bgra[at + 2] << 16 | bgra[at + 1] << 8 | bgra[at];
            }
        return new Placed
        {
            Full = new Level { Left = minX - sprite.AxisX, Top = minY - sprite.AxisY, Width = bw, Height = bh, VisibleCount = count, Pixels = pixels },
            MassX = sumX / count - sprite.AxisX, MassY = sumY / count - sprite.AxisY,
        };
    }

    /// <summary>
    /// The move that lines <paramref name="target"/> up with <paramref name="reference"/>.
    /// Similarity looks for the move with the highest share of visible positions whose colors
    /// are close. It tries every move that keeps the two overlapping on shrunken copies, then
    /// only around that answer at each larger size, so it is fast but can miss on repeating
    /// patterns. Ties go to the smallest move; null when no move matches.
    /// The center methods line up one point and round halves toward no move.
    /// </summary>
    public static Move? Find(Placed reference, Placed target, Method method, IProgress<double>? progress = null)
    {
        switch (method)
        {
            case Method.CenterOfMass:
                return new Move(Round(reference.MassX - target.MassX), Round(reference.MassY - target.MassY), null);
            case Method.BoxCenter:
                return new Move(Round(CenterX(reference.Full) - CenterX(target.Full)),
                    Round(reference.Full.Top + reference.Full.Height / 2.0 - (target.Full.Top + target.Full.Height / 2.0)), null);
            case Method.BottomCenter:
                return new Move(Round(CenterX(reference.Full) - CenterX(target.Full)),
                    reference.Full.Top + reference.Full.Height - (target.Full.Top + target.Full.Height), null);
            default:
                return CoarseToFine(reference.Full, target.Full, progress);
        }
    }

    private static double CenterX(Level p) => p.Left + p.Width / 2.0;

    /// <summary>Nearest whole pixel, halves toward 0 so a tie moves less.</summary>
    private static int Round(double v)
    {
        double a = Math.Abs(v);
        double floor = Math.Floor(a);
        int n = (int)floor + (a - floor > 0.5 + 1e-9 ? 1 : 0);
        return v < 0 ? -n : n;
    }

    private static Move? CoarseToFine(Level reference, Level target, IProgress<double>? progress)
    {
        var refs = new List<Level> { reference };
        var targets = new List<Level> { target };
        while (Math.Max(Math.Max(refs[^1].Width, refs[^1].Height), Math.Max(targets[^1].Width, targets[^1].Height)) > CoarseSize)
        {
            refs.Add(Half(refs[^1]));
            targets.Add(Half(targets[^1]));
        }

        // Every move that keeps the two boxes overlapping, at the smallest size.
        int top = refs.Count - 1;
        Level r = refs[top], t = targets[top];
        var (x, y, same, union) = Best(r, t, r.Left - t.Left - t.Width + 1, r.Left + r.Width - t.Left - 1,
            r.Top - t.Top - t.Height + 1, r.Top + r.Height - t.Top - 1);
        for (int level = top - 1; level >= 0; level--)
        {
            progress?.Report((double)(top - level) / (top + 1));
            (x, y, same, union) = Best(refs[level], targets[level], 2 * x - Refine, 2 * x + Refine, 2 * y - Refine, 2 * y + Refine);
        }
        progress?.Report(1);
        if (same <= 0) return null;
        return new Move(x, y, (int)(same * 100 / union));
    }

    /// <summary>The best move in the given ranges: highest share of close visible positions,
    /// then the smallest move.</summary>
    private static (int X, int Y, long Same, long Union) Best(Level r, Level t, int fromX, int toX, int fromY, int toY)
    {
        long bestSame = -1, bestUnion = 1;
        int bestX = 0, bestY = 0;
        for (int dy = fromY; dy <= toY; dy++)
            for (int dx = fromX; dx <= toX; dx++)
            {
                var (same, union) = Score(r, t, dx, dy);
                long lhs = same * bestUnion, rhs = bestSame * union;
                if (bestSame < 0 || lhs > rhs || (lhs == rhs && Closer(dx, dy, bestX, bestY)))
                    (bestSame, bestUnion, bestX, bestY) = (same, union, dx, dy);
            }
        return (bestX, bestY, Math.Max(0, bestSame), bestUnion);
    }

    /// <summary>Close-colored and union counts of visible positions for one move.</summary>
    private static (long Same, long Union) Score(Level r, Level t, int dx, int dy)
    {
        // A target box position plus (ox, oy) is the same canvas spot in the reference box.
        int ox = t.Left + dx - r.Left, oy = t.Top + dy - r.Top;
        int x0 = Math.Max(0, -ox), x1 = Math.Min(t.Width, r.Width - ox);
        int y0 = Math.Max(0, -oy), y1 = Math.Min(t.Height, r.Height - oy);
        long both = 0, same = 0;
        var tp = t.Pixels;
        var rp = r.Pixels;
        for (int y = y0; y < y1; y++)
        {
            int tRow = y * t.Width, rRow = (y + oy) * r.Width + ox;
            for (int x = x0; x < x1; x++)
            {
                int a = tp[tRow + x];
                if (a < 0) continue;
                int b = rp[rRow + x];
                if (b < 0) continue;
                both++;
                if (Close(a, b)) same++;
            }
        }
        return (same, r.VisibleCount + t.VisibleCount - both);
    }

    private static bool Close(int a, int b) =>
        a == b || Math.Abs((a >> 16) - (b >> 16)) + Math.Abs((a >> 8 & 255) - (b >> 8 & 255)) + Math.Abs((a & 255) - (b & 255)) <= Tolerance;

    /// <summary>Half size on the canvas grid, so both sprites' blocks line up: a pixel is visible
    /// when any of its 2×2 block is (so thin lines survive), colored with their average.</summary>
    private static Level Half(Level s)
    {
        int left = s.Left >> 1, top = s.Top >> 1; // arithmetic shift floors negatives too
        int w = ((s.Left + s.Width - 1) >> 1) - left + 1, h = ((s.Top + s.Height - 1) >> 1) - top + 1;
        var pixels = new int[w * h];
        int visible = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int n = 0, red = 0, green = 0, blue = 0;
                for (int by = 0; by < 2; by++)
                    for (int bx = 0; bx < 2; bx++)
                    {
                        int sx = 2 * (left + x) + bx - s.Left, sy = 2 * (top + y) + by - s.Top;
                        if (sx < 0 || sy < 0 || sx >= s.Width || sy >= s.Height) continue;
                        int p = s.Pixels[sy * s.Width + sx];
                        if (p < 0) continue;
                        n++;
                        red += p >> 16; green += p >> 8 & 255; blue += p & 255;
                    }
                if (n == 0) { pixels[y * w + x] = -1; continue; }
                visible++;
                pixels[y * w + x] = (red / n) << 16 | (green / n) << 8 | blue / n;
            }
        return new Level { Left = left, Top = top, Width = w, Height = h, VisibleCount = visible, Pixels = pixels };
    }

    /// <summary>Tie-break: the shorter move, then the smaller vertical part.</summary>
    private static bool Closer(int x, int y, int bestX, int bestY)
    {
        int d = Math.Abs(x) + Math.Abs(y), bestD = Math.Abs(bestX) + Math.Abs(bestY);
        if (d != bestD) return d < bestD;
        return Math.Abs(y) < Math.Abs(bestY);
    }
}
