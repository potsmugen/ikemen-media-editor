using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>Auto Clsn2's rectangle cover: full coverage, budget, wide preference, mask handling.</summary>
public class SpriteCoverTests
{
    // '.' = mask color, '#' = opaque.
    private static Sprite MakeSprite(string[] rows, int axisX = 0, int axisY = 0)
    {
        int w = rows[0].Length, h = rows.Length;
        var indexed = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                indexed[y * w + x] = rows[y][x] == '#' ? (byte)1 : (byte)Sprite.TransparentIndex;
        return new Sprite { Width = w, Height = h, AxisX = axisX, AxisY = axisY, Indexed = indexed, PixelsDirty = true };
    }

    private static Sprite MakeDirectSprite(byte[] alpha, int w, int h)
    {
        var direct = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
            direct[i * 4 + 3] = alpha[i]; // BGRA: alpha is the 4th byte
        return new Sprite { Width = w, Height = h, Direct = direct, PixelsDirty = true };
    }

    private static bool CoversPixel(Sprite s, SpriteCover.Rect b, int px, int py)
    {
        int ax = px - s.AxisX, ay = py - s.AxisY;
        return ax >= b.X1 && ax <= b.X2 && ay >= b.Y1 && ay <= b.Y2;
    }

    private static void AssertOpaqueCovered(Sprite s, List<SpriteCover.Rect> boxes)
    {
        for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++)
                if (s.Indexed![y * s.Width + x] != Sprite.TransparentIndex)
                    Assert.True(boxes.Any(b => CoversPixel(s, b, x, y)), $"opaque pixel {x},{y} uncovered");
    }

    /// <summary>For gap-separated parts, boxes must stay disjoint. Seam-adjacent parts are allowed — indeed required — to share an edge (see AdjacentBoxesShareTheirEdge).</summary>
    private static void AssertNoOverlap(List<SpriteCover.Rect> boxes)
    {
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
                Assert.False(boxes[i].X1 <= boxes[j].X2 && boxes[j].X1 <= boxes[i].X2 &&
                             boxes[i].Y1 <= boxes[j].Y2 && boxes[j].Y1 <= boxes[i].Y2,
                             $"{boxes[i]} overlaps {boxes[j]}");
    }

    [Fact]
    public void SolidSpriteOneBox()
    {
        var s = MakeSprite(["###", "###", "###"], axisX: 2, axisY: 1);
        var boxes = SpriteCover.Cover(s, 1);
        var b = Assert.Single(boxes);
        // Pixel (0,0) sits at axis (-2,-1); the box is the whole sprite.
        Assert.Equal((-2, -1, 0, 1), (b.X1, b.Y1, b.X2, b.Y2));
    }

    [Fact]
    public void BudgetIsExactWhenReachable()
    {
        // An L: one box cannot cover it, two can.
        var s = MakeSprite(["#..", "#..", "###"]);
        Assert.Single(SpriteCover.Cover(s, 1));
        var two = SpriteCover.Cover(s, 2);
        Assert.Equal(2, two.Count);
        AssertOpaqueCovered(s, two);
        var ordered = two.OrderBy(b => b.Y1).ToList();
        Assert.Equal(ordered[0].Y2, ordered[1].Y1); // the seam is shared, not gapped
    }

    [Fact]
    public void SolidSpriteSplitsIntoEqualBands()
    {
        // No transparency: equal strips with shared seams, not 1px slivers.
        var s = MakeSprite(["####", "####", "####", "####", "####", "####"]);
        var boxes = SpriteCover.Cover(s, 3, CoverLayout.Stacked);
        Assert.Equal(new[] { (0, 0, 3, 1), (0, 1, 3, 3), (0, 3, 3, 5) }, boxes.Select(b => (b.X1, b.Y1, b.X2, b.Y2)));
        AssertOpaqueCovered(s, boxes);
    }

    [Fact]
    public void SolidSpriteLayoutPicksStripDirection()
    {
        var wide = MakeSprite(["######", "######"]);
        // Side by side: columns. Auto: along the longer side, so columns for a wide sprite.
        foreach (var layout in new[] { CoverLayout.SideBySide, CoverLayout.Auto })
            Assert.Equal(new[] { (0, 0, 2, 1), (2, 0, 5, 1) },
                SpriteCover.Cover(wide, 2, layout).Select(b => (b.X1, b.Y1, b.X2, b.Y2)));
        // Square: Auto uses bands.
        var square = MakeSprite(["##", "##"]);
        Assert.All(SpriteCover.Cover(square, 2, CoverLayout.Auto), b => Assert.Equal((0, 1), (b.X1, b.X2)));
    }

    [Fact]
    public void SolidSpriteSplitCapsAtOnePerRow()
    {
        var s = MakeSprite(["##", "##"]);
        var boxes = SpriteCover.Cover(s, 100, CoverLayout.Stacked);
        Assert.Equal(new[] { (0, 0, 1, 0), (0, 0, 1, 1) }, boxes.Select(b => (b.X1, b.Y1, b.X2, b.Y2)));
    }

    [Fact]
    public void SolidDirectSpriteSplitsEvenly()
    {
        // A 32-bit sprite with no transparent pixel, as from a PNG without alpha.
        var s = MakeDirectSprite(new byte[] { 255, 255, 255, 255, 255, 255 }, 2, 3);
        var boxes = SpriteCover.Cover(s, 3, CoverLayout.Stacked);
        Assert.Equal(new[] { (0, 0, 1, 0), (0, 0, 1, 1), (0, 1, 1, 2) }, boxes.Select(b => (b.X1, b.Y1, b.X2, b.Y2)));
    }

    [Fact]
    public void SolidSpriteTurned90SplitsTheTurnedRectangle()
    {
        // 4 wide, 2 tall, turned 90 degrees: 2 wide, 4 tall, so Auto makes bands.
        var s = MakeSprite(["####", "####"]);
        var boxes = SpriteCover.CoverTransformed(s, 2, CoverLayout.Auto, false, false, 1, 1, 90);
        Assert.Equal(2, boxes.Count);
        Assert.All(boxes, b => Assert.Equal(1, b.X2 - b.X1));
        Assert.Equal(boxes[0].Y2, boxes[1].Y1);
    }

    [Fact]
    public void SplitToRequestedCount()
    {
        // One transparent pixel keeps this on the regular cover (see SolidSpriteSplitsIntoEqualBands).
        var s = MakeSprite(["####", "####", "####", "###."]);
        var boxes = SpriteCover.Cover(s, 5, CoverLayout.Auto);
        Assert.InRange(boxes.Count, 1, 5);
        AssertOpaqueCovered(s, boxes);
    }

    [Fact]
    public void MaskPixelsNeedNoCover()
    {
        var s = MakeSprite(["#.#", ".#.", "#.#"]);
        var boxes = SpriteCover.Cover(s, 10, CoverLayout.Auto);
        AssertOpaqueCovered(s, boxes);
        AssertNoOverlap(boxes);
    }

    [Fact]
    public void FullyMaskedSpriteYieldsNothing()
    {
        var s = MakeSprite(["...", "..."]);
        Assert.Empty(SpriteCover.Cover(s, 8));
    }

    [Fact]
    public void DirectSpriteIgnoresTransparentAlpha()
    {
        // 3x1: opaque, transparent, opaque.
        var s = MakeDirectSprite([255, 0, 255], 3, 1);
        // Auto, not the default Stacked: only a vertical cut can skip the
        // transparent pixel in a single row.
        var boxes = SpriteCover.Cover(s, 4, CoverLayout.Auto);
        Assert.Equal(2, boxes.Count);
        AssertNoOverlap(boxes);
        // The transparent middle pixel must not be inside either box.
        Assert.DoesNotContain(boxes, b => CoversPixel(s, b, 1, 0));
    }

    [Fact]
    public void AlphaBelowThresholdIsTransparent()
    {
        // 3x1: opaque, faint (alpha 20), opaque. Default threshold 1: the faint pixel counts, so the row is solid.
        var s = MakeDirectSprite(new byte[] { 255, 20, 255 }, 3, 1);
        Assert.Contains(SpriteCover.Cover(s, 4, CoverLayout.Auto), b => CoversPixel(s, b, 1, 0));
        // Threshold 32: the faint pixel is transparent and left uncovered.
        var boxes = SpriteCover.Cover(s, 4, CoverLayout.Auto, alphaThreshold: 32);
        Assert.Equal(2, boxes.Count);
        Assert.DoesNotContain(boxes, b => CoversPixel(s, b, 1, 0));
    }

    [Fact]
    public void DegenerateSpriteYieldsNothing()
    {
        Assert.Empty(SpriteCover.Cover(new Sprite { Width = 0, Height = 0 }, 8));
        Assert.Empty(SpriteCover.Cover(new Sprite { Width = 4, Height = 3 }, 8));
    }

    [Fact]
    public void EmptyRowSeparatesParts()
    {
        var s = MakeSprite(["#..", "#..", "...", "###"]);
        var boxes = SpriteCover.Cover(s, 2);
        Assert.Equal(2, boxes.Count);
        Assert.Contains(boxes, b => (b.X1, b.Y1, b.X2, b.Y2) == (0, 0, 0, 1));
        Assert.Contains(boxes, b => (b.X1, b.Y1, b.X2, b.Y2) == (0, 3, 2, 3));
    }

    [Fact]
    public void MergesWhenTooManyParts()
    {
        // Three single-pixel parts on a diagonal, separated by empty rows.
        var s = MakeSprite(["#..", "...", ".#.", "...", "..#"]);
        var two = SpriteCover.Cover(s, 2);
        Assert.Equal(2, two.Count);
        AssertOpaqueCovered(s, two);
        AssertNoOverlap(two);
        var one = SpriteCover.Cover(s, 1);
        var b = Assert.Single(one);
        Assert.Equal((0, 0, 2, 4), (b.X1, b.Y1, b.X2, b.Y2));
    }

    [Fact]
    public void BandCoversGapBetweenLegs()
    {
        // Two leg columns with a gap: one band box must span the gap.
        var s = MakeSprite(["#.#", "#.#"]);
        var boxes = SpriteCover.Cover(s, 1);
        var b = Assert.Single(boxes);
        Assert.Equal((0, 0, 2, 1), (b.X1, b.Y1, b.X2, b.Y2));
    }

    [Fact]
    public void LayoutDecidesTheCut()
    {
        // A wide cut and a tall cut are both available; the layout decides.
        var s = MakeSprite(["..##", "..##", "####", "..##", "..#."]);
        var wide = SpriteCover.Cover(s, 2, CoverLayout.Stacked);
        Assert.Equal(2, wide.Count);
        Assert.Contains(wide, b => (b.X1, b.Y1, b.X2, b.Y2) == (2, 0, 3, 1));
        Assert.Contains(wide, b => (b.X1, b.Y1, b.X2, b.Y2) == (0, 1, 3, 4)); // seam shared with the box above

        var tall = SpriteCover.Cover(s, 2, CoverLayout.Auto);
        Assert.Equal(2, tall.Count);
        Assert.Contains(tall, b => (b.X1, b.Y1, b.X2, b.Y2) == (0, 2, 1, 2));
        Assert.Contains(tall, b => (b.X1, b.Y1, b.X2, b.Y2) == (1, 0, 3, 4)); // seam shared with the box to the left
    }

    [Fact]
    public void SideBySideOnlyCutsColumns()
    {
        // Two figures side by side: Side by side splits at the empty column, Stacked can't.
        var s = MakeSprite(["##.#", "##.#", "##.#", "#..#"]);
        var cols = SpriteCover.Cover(s, 2, CoverLayout.SideBySide);
        Assert.Equal(2, cols.Count);
        Assert.Contains(cols, b => (b.X1, b.Y1, b.X2, b.Y2) == (0, 0, 1, 3));
        Assert.Contains(cols, b => (b.X1, b.Y1, b.X2, b.Y2) == (3, 0, 3, 3));

        foreach (var b in SpriteCover.Cover(s, 2, CoverLayout.Stacked))
            Assert.Equal((0, 3), (b.X1, b.X2)); // every box a full-width band
    }
}
