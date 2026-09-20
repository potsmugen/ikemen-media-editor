namespace Sff;

/// <summary>Scales a sprite's pixels without changing its palette or pixel mode.</summary>
public static class SpriteScale
{
    public readonly record struct Result(bool Changed, int Width, int Height, int AxisX, int AxisY);

    /// <summary>Scales to the destination size, sampling existing colors or indexes.</summary>
    public static Result Resize(Sprite sprite, int width, int height, SpriteRotate.Interpolation interpolation)
    {
        ArgumentNullException.ThrowIfNull(sprite);
        if (width is < 1 or > 65535 || height is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(width), "The destination dimensions must be between 1 and 65535 pixels.");
        if (!sprite.HasPixels) return Unchanged(sprite);
        if (sprite.Width == width && sprite.Height == height) return Unchanged(sprite);
        SpriteCrop.RefuseBorrower(sprite);
        if (sprite.Indexed is not null && interpolation == SpriteRotate.Interpolation.Bilinear)
            throw new NotSupportedException("Bilinear blending requires a true-color sprite.");

        int oldWidth = sprite.Width, oldHeight = sprite.Height;
        if (sprite.Indexed is { } indexed)
            sprite.Indexed = interpolation == SpriteRotate.Interpolation.Area
                ? ScaleIndexedArea(indexed, oldWidth, oldHeight, width, height)
                : ScaleIndexedNearest(indexed, oldWidth, oldHeight, width, height);
        else
            sprite.Direct = interpolation == SpriteRotate.Interpolation.Nearest
                ? ScaleDirectNearest(sprite.Direct!, oldWidth, oldHeight, width, height)
                : ScaleDirectFiltered(sprite.Direct!, oldWidth, oldHeight, width, height, interpolation);

        sprite.Width = width;
        sprite.Height = height;
        sprite.AxisX = Round(sprite.AxisX * (double)width / oldWidth);
        sprite.AxisY = Round(sprite.AxisY * (double)height / oldHeight);
        sprite.PixelsDirty = true;
        return new Result(true, width, height, sprite.AxisX, sprite.AxisY);
    }

    private static Result Unchanged(Sprite sprite) => new(false, sprite.Width, sprite.Height, sprite.AxisX, sprite.AxisY);

    private static byte[] ScaleIndexedNearest(byte[] source, int oldWidth, int oldHeight, int width, int height)
    {
        var destination = new byte[checked(width * height)];
        for (int y = 0; y < height; y++)
        {
            int sy = Math.Min(oldHeight - 1, (int)((y + 0.5) * oldHeight / height));
            for (int x = 0; x < width; x++)
            {
                int sx = Math.Min(oldWidth - 1, (int)((x + 0.5) * oldWidth / width));
                destination[y * width + x] = source[sy * oldWidth + sx];
            }
        }
        return destination;
    }

    private static byte[] ScaleIndexedArea(byte[] source, int oldWidth, int oldHeight, int width, int height)
    {
        var destination = new byte[checked(width * height)];
        Span<double> weights = stackalloc double[256];
        double scaleX = oldWidth / (double)width, scaleY = oldHeight / (double)height;
        for (int y = 0; y < height; y++)
        {
            double top = y * scaleY, bottom = (y + 1) * scaleY;
            int firstY = (int)Math.Floor(top), lastY = (int)Math.Ceiling(bottom) - 1;
            for (int x = 0; x < width; x++)
            {
                double left = x * scaleX, right = (x + 1) * scaleX;
                int firstX = (int)Math.Floor(left), lastX = (int)Math.Ceiling(right) - 1;
                weights.Clear();
                for (int sy = firstY; sy <= lastY; sy++)
                {
                    double wy = Math.Min(bottom, sy + 1) - Math.Max(top, sy);
                    for (int sx = firstX; sx <= lastX; sx++)
                    {
                        double wx = Math.Min(right, sx + 1) - Math.Max(left, sx);
                        weights[source[sy * oldWidth + sx]] += wx * wy;
                    }
                }
                destination[y * width + x] = Heaviest(weights);
            }
        }
        return destination;
    }

    private static byte Heaviest(ReadOnlySpan<double> weights)
    {
        int bestAny = 0, bestSolid = -1;
        double bestAnyWeight = 0, bestSolidWeight = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            double weight = weights[i];
            if (weight > bestAnyWeight) { bestAny = i; bestAnyWeight = weight; }
            if (i != Sprite.TransparentIndex && weight > bestSolidWeight) { bestSolid = i; bestSolidWeight = weight; }
        }
        return (byte)(bestSolid > 0 && bestSolidWeight >= bestAnyWeight ? bestSolid : bestAny);
    }

    private static byte[] ScaleDirectNearest(byte[] source, int oldWidth, int oldHeight, int width, int height)
    {
        var destination = new byte[checked(checked(width * height) * 4)];
        for (int y = 0; y < height; y++)
        {
            int sy = Math.Min(oldHeight - 1, (int)((y + 0.5) * oldHeight / height));
            for (int x = 0; x < width; x++)
            {
                int sx = Math.Min(oldWidth - 1, (int)((x + 0.5) * oldWidth / width));
                Array.Copy(source, (sy * oldWidth + sx) * 4, destination, (y * width + x) * 4, 4);
            }
        }
        return destination;
    }

    private static byte[] ScaleDirectFiltered(byte[] source, int oldWidth, int oldHeight, int width, int height,
        SpriteRotate.Interpolation interpolation)
    {
        var destination = new byte[checked(checked(width * height) * 4)];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int output = (y * width + x) * 4;
                if (interpolation == SpriteRotate.Interpolation.Area)
                    AreaPixel(source, oldWidth, oldHeight, width, height, x, y, destination, output);
                else
                    BilinearPixel(source, oldWidth, oldHeight, width, height, x, y, destination, output);
            }
        return destination;
    }

    private static void AreaPixel(byte[] source, int oldWidth, int oldHeight, int width, int height,
        int x, int y, byte[] destination, int output)
    {
        double left = x * oldWidth / (double)width, right = (x + 1) * oldWidth / (double)width;
        double top = y * oldHeight / (double)height, bottom = (y + 1) * oldHeight / (double)height;
        int firstX = (int)Math.Floor(left), lastX = (int)Math.Ceiling(right) - 1;
        int firstY = (int)Math.Floor(top), lastY = (int)Math.Ceiling(bottom) - 1;
        double b = 0, g = 0, r = 0, a = 0, total = 0;
        for (int sy = firstY; sy <= lastY; sy++)
        {
            double wy = Math.Min(bottom, sy + 1) - Math.Max(top, sy);
            for (int sx = firstX; sx <= lastX; sx++)
            {
                double weight = (Math.Min(right, sx + 1) - Math.Max(left, sx)) * wy;
                int input = (sy * oldWidth + sx) * 4;
                double alpha = source[input + 3] / 255d;
                b += source[input] * alpha * weight;
                g += source[input + 1] * alpha * weight;
                r += source[input + 2] * alpha * weight;
                a += alpha * weight;
                total += weight;
            }
        }
        destination[output] = Byte(b / total);
        destination[output + 1] = Byte(g / total);
        destination[output + 2] = Byte(r / total);
        destination[output + 3] = Byte(a / total * 255);
    }

    private static void BilinearPixel(byte[] source, int oldWidth, int oldHeight, int width, int height,
        int x, int y, byte[] destination, int output)
    {
        double px = Math.Clamp((x + 0.5) * oldWidth / width - 0.5, 0, oldWidth - 1);
        double py = Math.Clamp((y + 0.5) * oldHeight / height - 0.5, 0, oldHeight - 1);
        int bx = (int)Math.Floor(px), by = (int)Math.Floor(py);
        double fx = px - bx, fy = py - by;
        double b = 0, g = 0, r = 0, a = 0, total = 0;
        for (int oy = 0; oy <= 1; oy++)
            for (int ox = 0; ox <= 1; ox++)
            {
                int sx = bx + ox, sy = by + oy;
                if ((uint)sx >= (uint)oldWidth || (uint)sy >= (uint)oldHeight) continue;
                double weight = (ox == 0 ? 1 - fx : fx) * (oy == 0 ? 1 - fy : fy);
                int input = (sy * oldWidth + sx) * 4;
                double alpha = source[input + 3] / 255d;
                b += source[input] * alpha * weight;
                g += source[input + 1] * alpha * weight;
                r += source[input + 2] * alpha * weight;
                a += alpha * weight;
                total += weight;
            }
        destination[output] = Byte(b / total);
        destination[output + 1] = Byte(g / total);
        destination[output + 2] = Byte(r / total);
        destination[output + 3] = Byte(a / total * 255);
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
    private static byte Byte(double value) => (byte)Math.Clamp(Round(value), 0, 255);
}
