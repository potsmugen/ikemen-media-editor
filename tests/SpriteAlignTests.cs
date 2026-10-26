using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// Align With Onion Skin: SpriteAlign finds the canvas move that lines a sprite up with
/// the onion, comparing colors as drawn; close colors count as matching.
/// </summary>
public class SpriteAlignTests
{
    private static Palette Palette() =>
        new() { Colors = Enumerable.Range(0, 256).Select(i => new Rgba((byte)i, (byte)(255 - i), 7, 255)).ToArray() };

    /// <summary>A w×h indexed sprite of seeded noise, with index 0 for transparent around a border.</summary>
    private static byte[] Noise(int w, int h, int seed, int border = 2)
    {
        var random = new Random(seed);
        var px = new byte[w * h];
        for (int y = border; y < h - border; y++)
            for (int x = border; x < w - border; x++)
                px[y * w + x] = (byte)random.Next(1, 256);
        return px;
    }

    private static Sprite Indexed(byte[] px, int w, int h, int axisX, int axisY, Palette? pal = null) =>
        new() { Width = w, Height = h, Indexed = px, Palette = pal ?? Palette(), AxisX = axisX, AxisY = axisY, PixelsDirty = true };

    private static SpriteAlign.Move? Align(Sprite reference, Sprite target, SpriteAlign.Method method) =>
        SpriteAlign.Find(SpriteAlign.Prepare(reference)!, SpriteAlign.Prepare(target)!, method);

    [Fact]
    public void SimilarityUndoesAnAxisShift()
    {
        var px = Noise(30, 40, 1);
        // The target draws 3 right and 2 up of the reference, so it has to move 3 left and 2 down.
        var move = Align(Indexed(px, 30, 40, 10, 20), Indexed(px, 30, 40, 7, 22), SpriteAlign.Method.Similarity);
        Assert.Equal(new SpriteAlign.Move(-3, 2, 100), move);
    }

    [Fact]
    public void SimilarityLocksOntoTheBodyWhenALimbChanges()
    {
        var reference = Noise(40, 40, 2);
        var target = (byte[])reference.Clone();
        var random = new Random(9);
        for (int y = 2; y < 12; y++) // a different "arm" across the top quarter
            for (int x = 2; x < 38; x++)
                target[y * 40 + x] = (byte)random.Next(1, 256);
        var move = Align(Indexed(reference, 40, 40, 0, 0), Indexed(target, 40, 40, 5, -4), SpriteAlign.Method.Similarity);
        Assert.Equal(5, move!.Value.X);
        Assert.Equal(-4, move.Value.Y);
        Assert.True(move.Value.Percent is > 60 and < 100);
    }

    [Fact]
    public void SimilarityComparesDrawnColorsAcrossPalettesAndKinds()
    {
        var px = Noise(20, 20, 3);
        var pal = Palette();
        var indexed = Indexed(px, 20, 20, 0, 0, pal);
        var direct = new Sprite { Width = 20, Height = 20, Direct = indexed.ToBgra(pal), AxisX = 2, AxisY = 0, PixelsDirty = true };
        Assert.Equal(new SpriteAlign.Move(2, 0, 100), Align(indexed, direct, SpriteAlign.Method.Similarity));
    }

    [Fact]
    public void SimilarityReturnsNullWhenNoColorsAreClose()
    {
        // Index 1 draws (1,254,7) and index 200 draws (200,55,7): far apart wherever they overlap.
        var ones = Enumerable.Repeat((byte)1, 100).ToArray();
        var others = Enumerable.Repeat((byte)200, 100).ToArray();
        Assert.Null(Align(Indexed(ones, 10, 10, 0, 0), Indexed(others, 10, 10, 30, 0), SpriteAlign.Method.Similarity));
    }

    [Fact]
    public void SimilarityFindsSpritesFarApart()
    {
        // 100 px apart, no overlap where they are: the search still covers every overlapping move.
        var px = Noise(30, 30, 4);
        Assert.Equal(new SpriteAlign.Move(100, 0, 100), Align(Indexed(px, 30, 30, 0, 0), Indexed(px, 30, 30, 100, 0), SpriteAlign.Method.Similarity));
    }

    [Fact]
    public void SimilarityKeepsTheSpriteWhenItIsAlreadyAligned()
    {
        var px = new byte[16];
        Array.Fill(px, (byte)5); // a flat square: any move shrinks the overlap and grows the union
        Assert.Equal(new SpriteAlign.Move(0, 0, 100), Align(Indexed(px, 4, 4, 0, 0), Indexed(px, 4, 4, 0, 0), SpriteAlign.Method.Similarity));
    }

    /// <summary>A true-color ellipse of 12 px color blocks with a little noise, like a shaded character.</summary>
    private static Sprite Body(int w, int h, int axisX, int axisY, Action<byte[], Random>? redraw = null)
    {
        var random = new Random(1);
        var blocks = new Dictionary<(int, int), (byte R, byte G, byte B)>();
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double ex = (x - w / 2.0) / (w / 2.0), ey = (y - h / 2.0) / (h / 2.0);
                if (ex * ex + ey * ey >= 1) continue;
                if (!blocks.TryGetValue((x / 12, y / 12), out var c))
                    blocks[(x / 12, y / 12)] = c = ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                int at = (y * w + x) * 4;
                (px[at], px[at + 1], px[at + 2], px[at + 3]) = (c.B, c.G, (byte)Math.Clamp(c.R + random.Next(-6, 7), 0, 255), 255);
            }
        redraw?.Invoke(px, new Random(2));
        return new Sprite { Width = w, Height = h, Direct = px, AxisX = axisX, AxisY = axisY, PixelsDirty = true };
    }

    [Theory]
    [InlineData(13, -9)]
    [InlineData(-30, 25)]
    [InlineData(5, 31)]
    [InlineData(-1, 1)]
    [InlineData(-90, 60)]
    public void SimilarityFindsLargeMovesThroughTheShrunkenSearch(int moveX, int moveY)
    {
        const int w = 180, h = 240;
        // The target has an "arm" redrawn in random colors and draws moveX left and moveY up.
        void Arm(byte[] px, Random random)
        {
            for (int y = 30; y < 90; y++)
                for (int x = 100; x < w; x++)
                {
                    int at = (y * w + x) * 4;
                    if (px[at + 3] == 0 || random.NextDouble() >= 0.8) continue;
                    (px[at], px[at + 1], px[at + 2]) = ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                }
        }
        var move = Align(Body(w, h, 90, 240), Body(w, h, 90 + moveX, 240 + moveY, Arm), SpriteAlign.Method.Similarity);
        Assert.Equal(moveX, move!.Value.X);
        Assert.Equal(moveY, move.Value.Y);
    }

    [Fact]
    public void SimilarityKeepsThinLinesWhenShrinking()
    {
        var line = new byte[100 * 3];
        for (int x = 0; x < 100; x++) line[100 + x] = 9; // the middle row only
        var move = Align(Indexed(line, 100, 3, 0, 0), Indexed(line, 100, 3, -7, 0), SpriteAlign.Method.Similarity);
        Assert.Equal(new SpriteAlign.Move(-7, 0, 100), move);
    }

    [Fact]
    public void CenterOfMassRoundsHalvesTowardNoMove()
    {
        // One visible pixel each: reference at canvas x 0, target at x 1 -> exact move -1.
        var one = new byte[] { 1 };
        Assert.Equal(new SpriteAlign.Move(-1, 0, null), Align(Indexed(one, 1, 1, 0, 0), Indexed(one, 1, 1, -1, 0), SpriteAlign.Method.CenterOfMass));
        // Target two pixels wide at x 0-1 (mass 1.0) against reference mass 0.5: half a pixel, so no move.
        var two = new byte[] { 1, 1 };
        Assert.Equal(new SpriteAlign.Move(0, 0, null), Align(Indexed(one, 1, 1, 0, 0), Indexed(two, 2, 1, 0, 0), SpriteAlign.Method.CenterOfMass));
    }

    [Fact]
    public void BoxCenterIgnoresTransparentBorders()
    {
        var reference = new byte[] { 1, 1 };            // visible x 0-1 on the canvas
        var target = new byte[] { 0, 0, 0, 0, 1, 1 };    // visible x 4-5 with axis 0
        Assert.Equal(new SpriteAlign.Move(-4, 0, null), Align(Indexed(reference, 2, 1, 0, 0), Indexed(target, 6, 1, 0, 0), SpriteAlign.Method.BoxCenter));
    }

    [Fact]
    public void BottomCenterLinesUpTheBottomEdge()
    {
        var tall = Enumerable.Repeat((byte)1, 2 * 6).ToArray();  // 2×6, bottom at canvas y 0 (axis y 6)
        var square = Enumerable.Repeat((byte)1, 2 * 2).ToArray(); // 2×2, bottom at canvas y 2 (axis y 0)
        Assert.Equal(new SpriteAlign.Move(0, -2, null), Align(Indexed(tall, 2, 6, 0, 6), Indexed(square, 2, 2, 0, 0), SpriteAlign.Method.BottomCenter));
    }

    [Fact]
    public void FullyTransparentSpritesHaveNothingToAlign()
    {
        Assert.Null(SpriteAlign.Prepare(Indexed(new byte[9], 3, 3, 0, 0)));
    }
}
