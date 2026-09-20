namespace Sff;

/// <summary>
/// Trims a sprite's excess mask-color border down to the tightest box around
/// its actual pixels. The axis moves along with the crop (by exactly the
/// amount trimmed off the top and left) so nothing shifts on screen — the
/// same pixel that used to sit at the axis still does.
/// </summary>
public static class SpriteCrop
{
    /// <summary>How much border a crop removed, or Changed=false if there was nothing to trim (already tight, or entirely mask).</summary>
    public readonly record struct Result(bool Changed, int Left, int Top, int Right, int Bottom);

    /// <summary>
    /// Crops in place. Refuses a sprite that borrows another's pixels (links
    /// are never removed as a side effect); crop the one it shares with.
    /// </summary>
    public static Result Crop(Sprite sprite)
    {
        if (!sprite.HasPixels) return new Result(false, 0, 0, 0, 0);
        int w = sprite.Width, h = sprite.Height;
        if (w <= 0 || h <= 0) return new Result(false, 0, 0, 0, 0);

        if (!TightBounds(sprite, w, h, out int minX, out int minY, out int maxX, out int maxY))
            return new Result(false, 0, 0, 0, 0); // fully mask-colored: nothing to keep

        if (minX == 0 && minY == 0 && maxX == w - 1 && maxY == h - 1)
            return new Result(false, 0, 0, 0, 0); // already tight

        RefuseBorrower(sprite);

        int newW = maxX - minX + 1, newH = maxY - minY + 1;
        if (sprite.Indexed is { } src)
        {
            var dst = new byte[newW * newH];
            for (int y = 0; y < newH; y++)
                Array.Copy(src, (minY + y) * w + minX, dst, y * newW, newW);
            sprite.Indexed = dst;
        }
        else
        {
            var direct = sprite.Direct!;
            var dst = new byte[newW * newH * 4];
            for (int y = 0; y < newH; y++)
                Array.Copy(direct, ((minY + y) * w + minX) * 4, dst, y * newW * 4, newW * 4);
            sprite.Direct = dst;
        }

        sprite.Width = newW;
        sprite.Height = newH;
        sprite.AxisX -= minX;
        sprite.AxisY -= minY;
        sprite.PixelsDirty = true;

        return new Result(true, minX, minY, w - 1 - maxX, h - 1 - maxY);
    }

    /// <summary>
    /// Resizes the sprite canvas around its center. Positive offsets mean the
    /// old image moved right/down in the new canvas; negative values crop it.
    /// Refuses a sprite that borrows another's pixels, like Crop.
    /// </summary>
    public static (bool Changed, int OffsetX, int OffsetY) Resize(Sprite sprite, int newWidth, int newHeight)
    {
        if (!sprite.HasPixels || newWidth <= 0 || newHeight <= 0
            || (sprite.Width == newWidth && sprite.Height == newHeight))
            return (false, 0, 0);

        RefuseBorrower(sprite);
        int oldWidth = sprite.Width, oldHeight = sprite.Height;
        int offsetX = (newWidth - oldWidth) / 2;
        int offsetY = (newHeight - oldHeight) / 2;

        if (sprite.Indexed is { } indexed)
        {
            var resized = new byte[newWidth * newHeight];
            Array.Fill(resized, (byte)Sprite.TransparentIndex);
            for (int y = 0; y < oldHeight; y++)
            {
                int dy = y + offsetY;
                if (dy < 0 || dy >= newHeight) continue;
                int sourceX = Math.Max(0, -offsetX);
                int destinationX = Math.Max(0, offsetX);
                int count = Math.Min(oldWidth - sourceX, newWidth - destinationX);
                if (count > 0)
                    indexed.AsSpan(y * oldWidth + sourceX, count)
                        .CopyTo(resized.AsSpan(dy * newWidth + destinationX, count));
            }
            sprite.Indexed = resized;
        }
        else if (sprite.Direct is { } direct)
        {
            var resized = new byte[newWidth * newHeight * 4];
            for (int y = 0; y < oldHeight; y++)
            {
                int dy = y + offsetY;
                if (dy < 0 || dy >= newHeight) continue;
                int sourceX = Math.Max(0, -offsetX);
                int destinationX = Math.Max(0, offsetX);
                int count = Math.Min(oldWidth - sourceX, newWidth - destinationX);
                if (count > 0)
                    direct.AsSpan((y * oldWidth + sourceX) * 4, count * 4)
                        .CopyTo(resized.AsSpan((dy * newWidth + destinationX) * 4, count * 4));
            }
            sprite.Direct = resized;
        }
        else return (false, 0, 0);

        sprite.Width = newWidth;
        sprite.Height = newHeight;
        sprite.AxisX += offsetX;
        sprite.AxisY += offsetY;
        sprite.PixelsDirty = true;
        return (true, offsetX, offsetY);
    }

    internal static void RefuseBorrower(Sprite sprite)
    {
        if (sprite.SharesWith is { } t)
            throw new InvalidOperationException(
                $"sprite {sprite.Group},{sprite.Index} shares pixels with {t.Group},{t.Index}; unshare it first");
    }

    /// <summary>Finds the tightest box around non-mask pixels. False if every pixel is mask.</summary>
    public static bool TightBounds(Sprite sprite, int w, int h, out int minX, out int minY, out int maxX, out int maxY)
    {
        minX = w; minY = h; maxX = -1; maxY = -1;
        var indexed = sprite.Indexed;
        var direct = sprite.Direct;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool mask = indexed is not null
                    ? indexed[y * w + x] == Sprite.TransparentIndex
                    : direct![(y * w + x) * 4 + 3] == 0; // BGRA: alpha is the 4th byte
                if (mask) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX >= 0;
    }
}
