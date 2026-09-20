namespace Sff;

/// <summary>
/// Turns a sprite in quarter turns, mirrors it, and turns it by a free angle. The quarter
/// turns and mirrors only rearrange pixels, never resampling them, so an indexed sprite
/// rotates by index and stays indexed and a true-color one rotates by BGRA and keeps its
/// exact colors: nothing is re-quantized. A free angle is the one lossy path here, and
/// says so. Refuses a sprite that borrows another's pixels, like <see cref="SpriteCrop"/>.
/// </summary>
public static class SpriteRotate
{
    /// <summary>What holds still while the sprite turns or mirrors.</summary>
    public enum Pivot
    {
        /// <summary>The sprite itself: it turns in place, about the middle of its canvas.</summary>
        Sprite,
        /// <summary>The axis point, which holds still on screen while the sprite swings around it.</summary>
        Axis,
        /// <summary>Nothing in particular: the axis keeps the same coordinates, so the
        /// sprite grows right and down from its top-left corner.</summary>
        None,
    }

    /// <summary>How a turned pixel is picked out of the source.</summary>
    public enum Interpolation
    {
        /// <summary>The nearest source pixel: the classic crisp look, and the sampler that
        /// invents nothing at all. At an off-angle it samples single points, so some source
        /// pixels are never reached and their detail is deleted while others are copied
        /// several times. Area avoids that, and is the default.</summary>
        Nearest,
        /// <summary>Weighted by how much of each source pixel the destination pixel actually
        /// covers, over a 3×3 neighbourhood rather than bilinear's 2×2. Every overlapping
        /// source pixel contributes, so nothing is silently dropped. An indexed sprite takes
        /// the index with the most total weight, which keeps the palette exact; a true-color
        /// one averages, alpha included. The default, because it keeps the most of the
        /// original at the cost of looking chunkier than a blend.</summary>
        Area,
        /// <summary>A weighted blend of the four surrounding pixels, alpha included, so a
        /// diagonal edge fades instead of stair-stepping. True-color sprites only: blending
        /// palette indexes means nothing, and resolving the result back to an index would
        /// re-quantize, which nothing else here does.</summary>
        Bilinear,
    }

    /// <summary>Quarter turns clockwise: 1 is 90° right, 2 is 180°, 3 is 90° left.</summary>
    public const int QuarterRight = 1, Half = 2, QuarterLeft = 3;

    /// <summary>What the transform did. Width/Height and AxisX/AxisY are where they ended up.</summary>
    public readonly record struct Result(bool Changed, int Width, int Height, int AxisX, int AxisY);

    /// <summary>
    /// Turns the sprite clockwise by whole quarter turns. Preferred over
    /// <see cref="RotateFree"/> at a multiple of 90°: it only rearranges pixels, so it is
    /// exact, and its axis lands on whole pixels where the free path has to round. All three
    /// samplers agree with it there, so nothing is given up by choosing it.
    /// </summary>

    /// <summary>
    /// Turns the sprite clockwise around <paramref name="pivot"/>. Quarter turns 0 and
    /// multiples of 4 do nothing and report Changed=false.
    /// </summary>
    public static Result Rotate(Sprite sprite, int quartersClockwise, Pivot pivot)
    {
        int q = ((quartersClockwise % 4) + 4) % 4;
        if (q == 0) return Unchanged(sprite);
        return Apply(sprite, pivot, Turn(q, sprite.Width, sprite.Height));
    }

    /// <summary>
    /// Mirrors the sprite around <paramref name="pivot"/>. A mirror never changes the
    /// canvas size, so only the axis moves. Doing neither reports Changed=false.
    /// </summary>
    public static Result Flip(Sprite sprite, bool horizontal, bool vertical, Pivot pivot)
    {
        if (!horizontal && !vertical) return Unchanged(sprite);
        return Apply(sprite, pivot, Mirror(horizontal, vertical, sprite.Width, sprite.Height));
    }

    // ---- free angle -------------------------------------------------------

    /// <summary>A destination canvas, and where the turned sprite sits inside it.</summary>
    public readonly record struct Placement(int Width, int Height, int OffsetX, int OffsetY);

    /// <summary>
    /// The smallest canvas that holds the whole sprite turned by
    /// <paramref name="degrees"/>, with the sprite at that canvas's top-left, so a caller
    /// can offer the fit and let the user grow it from there.
    /// </summary>
    public static Placement Fit(int width, int height, double degrees)
    {
        if (width <= 0 || height <= 0) return new Placement(0, 0, 0, 0);

        double c = Math.Cos(Radians(degrees)), s = Math.Sin(Radians(degrees));
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in new[] { (0d, 0d), (width - 1d, 0d), (0d, height - 1d), (width - 1d, height - 1d) })
        {
            double qx = x * c - y * s, qy = x * s + y * c; // clockwise, on screen where Y grows down
            minX = Math.Min(minX, qx); minY = Math.Min(minY, qy);
            maxX = Math.Max(maxX, qx); maxY = Math.Max(maxY, qy);
        }
        return new Placement(
            Math.Max(1, Round(maxX - minX) + 1), Math.Max(1, Round(maxY - minY) + 1),
            Round(-minX), Round(-minY));
    }

    /// <summary>
    /// Turns the sprite by any angle, resampling into the canvas <paramref name="place"/>
    /// describes. Clockwise, like the engine's element angle negated in the editor.
    ///
    /// This is not the lossless path. Anything but a multiple of 90° has to invent the
    /// pixels it lands between, and the axis works out on a fraction — it is rounded to
    /// the nearest whole pixel at the end. A quarter turn still goes through
    /// <see cref="Rotate"/>, which rearranges pixels exactly and lands on whole pixels.
    /// Refuses a sprite that borrows another's pixels, like <see cref="SpriteCrop"/>.
    /// </summary>
    public static Result RotateFree(Sprite sprite, double degrees, Placement place,
        Interpolation interpolation, Pivot pivot = Pivot.Sprite)
    {
        if (!sprite.HasPixels) return Unchanged(sprite);
        int w = sprite.Width, h = sprite.Height;
        if (w <= 0 || h <= 0) return Unchanged(sprite);
        if (place.Width <= 0 || place.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(place), "The destination canvas needs at least 1 pixel in each dimension.");
        SpriteCrop.RefuseBorrower(sprite);

        if (sprite.Indexed is not null && interpolation == Interpolation.Bilinear)
            throw new NotSupportedException(
                "Bilinear blending would have to resolve blended colors back to a palette index, so it needs a true-color sprite.");

        double c = Math.Cos(Radians(degrees)), s = Math.Sin(Radians(degrees));
        double ToX(double x, double y) => x * c - y * s + place.OffsetX;
        double ToY(double x, double y) => x * s + y * c + place.OffsetY;

        if (sprite.Indexed is { } indexed)
            sprite.Indexed = interpolation == Interpolation.Area
                ? ResampleIndexedArea(indexed, w, h, place, c, s) : ResampleIndexed(indexed, w, h, place, c, s);
        else if (interpolation == Interpolation.Area) sprite.Direct = ResampleAreaDirect(sprite.Direct!, w, h, place, c, s);
        else sprite.Direct = ResampleDirect(sprite.Direct!, w, h, place, c, s, interpolation);

        int axisX = sprite.AxisX, axisY = sprite.AxisY;
        switch (pivot)
        {
            case Pivot.Axis:
                // The axis holds its place on screen, so it takes where its own point went.
                axisX = Round(ToX(sprite.AxisX, sprite.AxisY));
                axisY = Round(ToY(sprite.AxisX, sprite.AxisY));
                break;
            case Pivot.Sprite:
                // The sprite's center holds its old screen point; the axis follows by the
                // same shift. Ends up a fraction for most angles, hence the rounding.
                axisX = Round(ToX((w - 1) / 2d, (h - 1) / 2d) - (w - 1) / 2d + sprite.AxisX);
                axisY = Round(ToY((w - 1) / 2d, (h - 1) / 2d) - (h - 1) / 2d + sprite.AxisY);
                break;
            // None: same coordinates.
        }

        sprite.Width = place.Width;
        sprite.Height = place.Height;
        sprite.AxisX = axisX;
        sprite.AxisY = axisY;
        sprite.PixelsDirty = true;
        return new Result(true, place.Width, place.Height, axisX, axisY);
    }

    /// <summary>
    /// The index carrying the most weight. Mask (index 0) only wins outright; a real index
    /// matching it takes the tie, so a half-covered edge cannot quietly paint artwork into
    /// the transparent color.
    /// </summary>
    private static byte Heaviest(ReadOnlySpan<int> weight)
    {
        int bestAny = -1, bestAnyWeight = 0, bestSolid = -1, bestSolidWeight = 0;
        for (int i = 0; i < weight.Length; i++)
        {
            if (weight[i] <= 0) continue;
            if (weight[i] > bestAnyWeight) { bestAny = i; bestAnyWeight = weight[i]; }
            if (i != Sprite.TransparentIndex && weight[i] > bestSolidWeight) { bestSolid = i; bestSolidWeight = weight[i]; }
        }
        return (byte)(bestSolid > 0 && bestSolidWeight >= bestAnyWeight ? bestSolid : Math.Max(bestAny, 0));
    }

    /// <summary>
    /// Turns an indexed sprite: the index carrying the most weight over the 3×3 source
    /// pixels around the destination pixel's own point. The 3×3 reach is what bilinear's 2×2
    /// misses — a unit square turned 45° covers up to three pixels a side, and a 2×2 kernel
    /// drops the corners — and the tent weights sum to 1 over a flat region, so no
    /// overlapping source pixel is left out.
    /// </summary>
    private static byte[] ResampleIndexedArea(byte[] src, int w, int h, Placement place, double c, double s)
    {
        var dst = new byte[place.Width * place.Height];
        Array.Fill(dst, (byte)Sprite.TransparentIndex); // whatever the turn does not cover is mask
        // One buffer for the whole image, cleared per pixel. Inside the loop it would be
        // 1KB per destination pixel, and stackalloc is not released until the method
        // returns, so anything past ~1000 pixels overflowed the stack outright.
        Span<int> tally = stackalloc int[256];
        for (int dy = 0; dy < place.Height; dy++)
            for (int dx = 0; dx < place.Width; dx++)
            {
                var (px, py) = Source(dx, dy, place, c, s);
                int bx = (int)Math.Floor(px), by = (int)Math.Floor(py);
                tally.Clear();
                bool any = false;
                for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int sx = bx + ox, sy = by + oy;
                        if ((uint)sx >= (uint)w || (uint)sy >= (uint)h) continue;
                        double wx = 1 - Math.Abs(sx - px), wy = 1 - Math.Abs(sy - py);
                        if (wx <= 0 || wy <= 0) continue;
                        int scaled = (int)(wx * wy * 1024); // fixed point, so the mode is not a float compare
                        if (scaled > 0) { tally[src[sy * w + sx]] += scaled; any = true; }
                    }
                if (any) dst[dy * place.Width + dx] = Heaviest(tally);
            }
        return dst;
    }

    /// <summary>The true-color twin of the above: the same 3×3 tent, averaged.</summary>
    private static byte[] ResampleAreaDirect(byte[] src, int w, int h, Placement place, double c, double s)
    {
        var dst = new byte[place.Width * place.Height * 4];
        for (int dy = 0; dy < place.Height; dy++)
            for (int dx = 0; dx < place.Width; dx++)
            {
                var (px, py) = Source(dx, dy, place, c, s);
                double r = 0, g = 0, b = 0, a = 0, total = 0;
                for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int sx = (int)Math.Floor(px) + ox, sy = (int)Math.Floor(py) + oy;
                        if ((uint)sx >= (uint)w || (uint)sy >= (uint)h) continue;
                        double wx = 1 - Math.Abs(sx - px), wy = 1 - Math.Abs(sy - py);
                        if (wx <= 0 || wy <= 0) continue;
                        int i = (sy * w + sx) * 4;
                        double alpha = src[i + 3] / 255d;
                        r += src[i + 2] * alpha * wx * wy; // BGRA
                        g += src[i + 1] * alpha * wx * wy;
                        b += src[i] * alpha * wx * wy;
                        a += alpha * wx * wy;
                        total += wx * wy;
                    }
                if (total <= 0) continue; // stays zero-alpha
                int o = (dy * place.Width + dx) * 4;
                dst[o + 0] = Byte(b / total);
                dst[o + 1] = Byte(g / total);
                dst[o + 2] = Byte(r / total);
                dst[o + 3] = Byte(a / total * 255);
            }
        return dst;
    }

    private static byte[] ResampleIndexed(byte[] src, int w, int h, Placement place, double c, double s)
    {
        var dst = new byte[place.Width * place.Height];
        Array.Fill(dst, (byte)Sprite.TransparentIndex); // whatever the turn does not cover is mask
        for (int dy = 0; dy < place.Height; dy++)
            for (int dx = 0; dx < place.Width; dx++)
            {
                var (px, py) = Source(dx, dy, place, c, s);
                int sx = (int)Math.Floor(px + 0.5), sy = (int)Math.Floor(py + 0.5);
                if ((uint)sx >= (uint)w || (uint)sy >= (uint)h) continue;
                dst[dy * place.Width + dx] = src[sy * w + sx];
            }
        return dst;
    }

    private static byte[] ResampleDirect(byte[] src, int w, int h, Placement place, double c, double s,
        Interpolation interpolation)
    {
        var dst = new byte[place.Width * place.Height * 4];

        // One tap of the four around a source point. Weights are alpha-premultiplied so a
        // transparent pixel next to an opaque one cannot bleed its color into the blend.
        void Tap(double tx, double ty, double weight, ref double r, ref double g, ref double b, ref double a, ref double total)
        {
            if (weight <= 0) return;
            int x = (int)Math.Floor(tx), y = (int)Math.Floor(ty);
            double fx = tx - x, fy = ty - y;
            Span<(int X, int Y, double Weight)> corners =
            [
                (x, y, (1 - fx) * (1 - fy)), (x + 1, y, fx * (1 - fy)),
                (x, y + 1, (1 - fx) * fy), (x + 1, y + 1, fx * fy),
            ];
            foreach (var (cx, cy, cw) in corners)
            {
                double wgt = weight * cw;
                if (wgt <= 0 || (uint)cx >= (uint)w || (uint)cy >= (uint)h) continue;
                int i = (cy * w + cx) * 4;
                double alpha = src[i + 3] / 255d;
                r += src[i + 2] * alpha * wgt; // BGRA
                g += src[i + 1] * alpha * wgt;
                b += src[i] * alpha * wgt;
                a += alpha * wgt;
                total += wgt;
            }
        }

        for (int dy = 0; dy < place.Height; dy++)
            for (int dx = 0; dx < place.Width; dx++)
            {
                var (px, py) = Source(dx, dy, place, c, s);
                int o = (dy * place.Width + dx) * 4;
                if (interpolation == Interpolation.Nearest)
                {
                    int sx = (int)Math.Floor(px + 0.5), sy = (int)Math.Floor(py + 0.5);
                    if ((uint)sx >= (uint)w || (uint)sy >= (uint)h) continue; // stays zero-alpha
                    Array.Copy(src, (sy * w + sx) * 4, dst, o, 4);
                    continue;
                }
                double r = 0, g = 0, b = 0, a = 0, total = 0;
                Tap(px, py, 1, ref r, ref g, ref b, ref a, ref total);
                if (total <= 0) continue;
                dst[o + 0] = Byte(b / total);
                dst[o + 1] = Byte(g / total);
                dst[o + 2] = Byte(r / total);
                dst[o + 3] = Byte(a / total * 255);
            }
        return dst;
    }

    /// <summary>Where a destination pixel came from, stepping back through the turn.</summary>
    private static (double X, double Y) Source(int dx, int dy, Placement place, double c, double s)
    {
        double qx = dx - place.OffsetX, qy = dy - place.OffsetY;
        return (qx * c + qy * s, -qx * s + qy * c);
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180.0;

    /// <summary>Half away from zero, not Math.Round's banker's rounding.</summary>
    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static byte Byte(double value) => (byte)Math.Clamp(Round(value), 0, 255);

    // An affine map from a source pixel to its destination, with 0/±1 coefficients so
    // these eight cases (four turns, four mirrors) share one loop. Naming follows
    // X(x,y) = Ax*x + Ay*y + Bx and Y(x,y) = Cx*x + Cy*y + Dx.
    private readonly record struct Map(int Ax, int Ay, int Bx, int Cx, int Cy, int Dx)
    {
        public int X(int x, int y) => Ax * x + Ay * y + Bx;
        public int Y(int x, int y) => Cx * x + Cy * y + Dx;
    }

    private static Map Turn(int q, int w, int h) => q switch
    {
        QuarterRight => new Map(0, -1, h - 1, 1, 0, 0),      // (x,y) -> (h-1-y, x)
        Half => new Map(-1, 0, w - 1, 0, -1, h - 1),         // (x,y) -> (w-1-x, h-1-y)
        _ => new Map(0, 1, 0, -1, 0, w - 1),                 // (x,y) -> (y, w-1-x)
    };

    private static Map Mirror(bool horizontal, bool vertical, int w, int h) =>
        new(horizontal ? -1 : 1, 0, horizontal ? w - 1 : 0,
            0, vertical ? -1 : 1, vertical ? h - 1 : 0);

    private static Result Apply(Sprite sprite, Pivot pivot, Map map)
    {
        if (!sprite.HasPixels) return Unchanged(sprite);
        int w = sprite.Width, h = sprite.Height;
        if (w <= 0 || h <= 0) return Unchanged(sprite);
        SpriteCrop.RefuseBorrower(sprite);

        // Every case above is a bijection of the lattice, so each destination pixel is
        // written exactly once — unlike Resize, nothing here needs padding filled first.
        int nw = 0, nh = 0;
        foreach (var (cx, cy) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) })
        {
            nw = Math.Max(nw, map.X(cx, cy) + 1);
            nh = Math.Max(nh, map.Y(cx, cy) + 1);
        }

        if (sprite.Indexed is { } indexed)
        {
            var dst = new byte[nw * nh];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    dst[map.Y(x, y) * nw + map.X(x, y)] = indexed[y * w + x];
            sprite.Indexed = dst;
        }
        else if (sprite.Direct is { } direct)
        {
            var dst = new byte[nw * nh * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    Array.Copy(direct, (y * w + x) * 4, dst, (map.Y(x, y) * nw + map.X(x, y)) * 4, 4);
            sprite.Direct = dst;
        }
        else return Unchanged(sprite);

        int axisX = sprite.AxisX, axisY = sprite.AxisY;
        switch (pivot)
        {
            case Pivot.Axis:
                // The axis keeps its place on screen, so it moves to wherever its own
                // pixel ended up.
                axisX = map.X(sprite.AxisX, sprite.AxisY);
                axisY = map.Y(sprite.AxisX, sprite.AxisY);
                break;
            case Pivot.Sprite:
                // (n-1)/2 - (m-1)/2 is (n-m)/2, and this is Resize's integer division, so
                // an odd difference puts the spare half pixel the same way for both.
                axisX += (nw - w) / 2;
                axisY += (nh - h) / 2;
                break;
            // None: the axis is already measured from the top-left, which holds still.
        }

        sprite.Width = nw;
        sprite.Height = nh;
        sprite.AxisX = axisX;
        sprite.AxisY = axisY;
        sprite.PixelsDirty = true;
        return new Result(true, nw, nh, axisX, axisY);
    }

    private static Result Unchanged(Sprite sprite) =>
        new(false, sprite.Width, sprite.Height, sprite.AxisX, sprite.AxisY);
}
