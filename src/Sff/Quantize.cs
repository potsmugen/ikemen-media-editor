namespace Sff;

/// <summary>
/// Cuts colors down by median cut, splitting boxes along their widest RGB channel
/// and using pixel frequency to choose split points and weighted averages.
/// </summary>
public static class Quantize
{
    private readonly record struct WeightedColor(Rgba Color, long Weight, int Source);

    private sealed class Box
    {
        public List<WeightedColor> Colors { get; }
        public long Weight { get; }
        public double Priority { get; }
        public int MinR { get; }
        public int MaxR { get; }
        public int MinG { get; }
        public int MaxG { get; }
        public int MinB { get; }
        public int MaxB { get; }

        public Box(List<WeightedColor> colors)
        {
            Colors = colors;
            int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
            long weight = 0;
            foreach (var point in colors)
            {
                var c = point.Color;
                minR = Math.Min(minR, c.R); maxR = Math.Max(maxR, c.R);
                minG = Math.Min(minG, c.G); maxG = Math.Max(maxG, c.G);
                minB = Math.Min(minB, c.B); maxB = Math.Max(maxB, c.B);
                weight += point.Weight;
            }
            MinR = minR; MaxR = maxR; MinG = minG; MaxG = maxG; MinB = minB; MaxB = maxB;
            Weight = weight;
            Priority = (double)(maxR - minR + 1) * (maxG - minG + 1) * (maxB - minB + 1) * weight;
        }

        public int WidestChannel()
        {
            int r = MaxR - MinR, g = MaxG - MinG, b = MaxB - MinB;
            return r >= g && r >= b ? 0 : g >= b ? 1 : 2;
        }
    }

    /// <summary>Keeps the transparency mask and reduces colors by median cut, treating each color equally</summary>
    public static Palette Reduce(Rgba[] colors, int maxColors = 255)
    {
        ArgumentNullException.ThrowIfNull(colors);
        var weights = new long[colors.Length];
        Array.Fill(weights, 1L);
        return Reduce(colors, weights, maxColors);
    }

    /// <summary>Reduces colors by median cut, giving frequent colors more influence</summary>
    public static Palette Reduce(Rgba[] colors, long[] weights, int maxColors = 255) =>
        new() { Colors = ReduceGrouped(colors, weights, maxColors).Colors };

    /// <summary>
    /// Median cut that also reports where each input went: GroupOf[i] is the reduced entry
    /// (1-based, 0 = mask) that input color i was merged into.
    /// </summary>
    public static (Rgba[] Colors, int[] GroupOf) ReduceGrouped(Rgba[] colors, long[] weights, int maxColors = 255)
    {
        ArgumentNullException.ThrowIfNull(colors);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxColors, 1);
        if (colors.Length == 0) throw new ArgumentException("a palette needs a mask entry", nameof(colors));
        if (weights.Length != colors.Length) throw new ArgumentException("color and weight counts must match", nameof(weights));

        var points = new List<WeightedColor>(colors.Length - 1);
        for (int i = 1; i < colors.Length; i++)
        {
            if (weights[i] <= 0) throw new ArgumentOutOfRangeException(nameof(weights), "visible colors need positive weights");
            points.Add(new WeightedColor(colors[i], weights[i], i));
        }

        var boxes = new List<Box>();
        if (points.Count > 0) boxes.Add(new Box(points));
        while (boxes.Count < maxColors)
        {
            int widest = -1;
            double priority = double.NegativeInfinity;
            for (int i = 0; i < boxes.Count; i++)
                if (boxes[i].Colors.Count > 1 && boxes[i].Priority > priority)
                {
                    widest = i;
                    priority = boxes[i].Priority;
                }
            if (widest < 0) break;

            var box = boxes[widest];
            int axis = box.WidestChannel();
            box.Colors.Sort((a, b) => Channel(a.Color, axis).CompareTo(Channel(b.Color, axis)));
            long halfway = box.Weight / 2;
            long accumulated = 0;
            int split = 0;
            while (split < box.Colors.Count - 1 && accumulated < halfway)
                accumulated += box.Colors[split++].Weight;

            boxes[widest] = new Box(box.Colors.GetRange(0, split));
            boxes.Add(new Box(box.Colors.GetRange(split, box.Colors.Count - split)));
        }

        var reduced = new Rgba[boxes.Count + 1];
        var groupOf = new int[colors.Length];
        reduced[0] = colors[0];
        for (int i = 0; i < boxes.Count; i++)
        {
            reduced[i + 1] = Average(boxes[i].Colors);
            foreach (var point in boxes[i].Colors) groupOf[point.Source] = i + 1;
        }
        return (reduced, groupOf);
    }

    private static int Channel(Rgba color, int axis) => axis == 0 ? color.R : axis == 1 ? color.G : color.B;

    private static Rgba Average(List<WeightedColor> box)
    {
        long weight = 0, r = 0, g = 0, b = 0, a = 0;
        foreach (var point in box)
        {
            weight += point.Weight;
            r += point.Color.R * point.Weight;
            g += point.Color.G * point.Weight;
            b += point.Color.B * point.Weight;
            a += point.Color.A * point.Weight;
        }
        // Image colors are opaque, so alpha only varies when merging palettes.
        return new Rgba((byte)(r / weight), (byte)(g / weight), (byte)(b / weight), (byte)(a / weight));
    }
}
