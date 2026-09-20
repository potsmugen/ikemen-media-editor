using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// Free-angle rotation: the resampling path. Quarter turns have their own tests; these
/// pin the fit placement, the inverse mapping, the two interpolations and the axis.
/// </summary>
public class SpriteRotateFreeTests
{
    private static Sprite Indexed3x2() => new()
    {
        Group = 0, Index = 0, Width = 3, Height = 2, AxisX = 1, AxisY = 1,
        Indexed = [1, 2, 3, 4, 5, 6],
    };

    private static Sprite Direct2x2(byte b, byte g, byte r, byte a) => new()
    {
        Group = 0, Index = 0, Width = 2, Height = 2,
        Direct = [b, g, r, a, b, g, r, a, b, g, r, a, b, g, r, a],
    };

    [Fact]
    public void FitAtNoTurnIsTheSpriteItself()
    {
        Assert.Equal(new SpriteRotate.Placement(3, 2, 0, 0), SpriteRotate.Fit(3, 2, 0));
    }

    [Fact]
    public void FitAtAQuarterTurnMatchesTheCanvasSwap()
    {
        // The exact quarter turn maps (x,y) -> (h-1-y, x), which is a clockwise turn
        // with an offset of (h-1, 0). So the fit placement has to come out the same.
        Assert.Equal(new SpriteRotate.Placement(2, 3, 1, 0), SpriteRotate.Fit(3, 2, 90));
    }

    [Fact]
    public void FitGrowsTheCanvasForAnOffAngle()
    {
        // 45° of a 4×4 needs more room on both sides than the sprite had.
        var fit = SpriteRotate.Fit(4, 4, 45);

        Assert.Equal(5, fit.Width);
        Assert.Equal(5, fit.Height);
        Assert.Equal(2, fit.OffsetX);
        Assert.Equal(0, fit.OffsetY);
    }

    [Fact]
    public void FitIsAlwaysAtLeastOnePixel()
    {
        Assert.Equal(1, SpriteRotate.Fit(1, 1, 30).Width);
    }

    [Fact]
    public void AQuarterTurnThroughTheFreePathLandsTheSamePixels()
    {
        var s = Indexed3x2();
        var fit = SpriteRotate.Fit(s.Width, s.Height, 90);

        SpriteRotate.RotateFree(s, 90, fit, SpriteRotate.Interpolation.Area);

        // Same as the exact QuarterRight test: 4 1 / 5 2 / 6 3.
        Assert.Equal(2, s.Width);
        Assert.Equal(3, s.Height);
        Assert.Equal(new byte[] { 4, 1, 5, 2, 6, 3 }, s.Indexed);
        // The axis does not match the exact path here, and cannot: it works out on a
        // fraction (1.5) where the integer path lands on 1.
        Assert.Equal(1, s.AxisX);
        Assert.Equal(2, s.AxisY);
    }

    [Fact]
    public void NoTurnIsAnIdentity()
    {
        var s = Indexed3x2();
        var before = (byte[])[.. s.Indexed!];

        SpriteRotate.RotateFree(s, 0, new SpriteRotate.Placement(3, 2, 0, 0),
            SpriteRotate.Interpolation.Area);

        Assert.Equal(before, s.Indexed);
        Assert.Equal(3, s.Width);
        Assert.Equal(2, s.Height);
    }

    [Fact]
    public void FitGivesDifferentOffsetsForTheTwoDirections()
    {
        // The same canvas, the same size, but clockwise and anticlockwise need different
        // offsets. Using one for the other pushes half the sprite off the edge.
        Assert.Equal(new SpriteRotate.Placement(2, 3, 1, 0), SpriteRotate.Fit(3, 2, 90));
        Assert.Equal(new SpriteRotate.Placement(2, 3, 0, 2), SpriteRotate.Fit(3, 2, -90));
    }

    [Fact]
    public void ALeftTurnKeepsEveryPixel()
    {
        var s = Indexed3x2();
        var fit = SpriteRotate.Fit(3, 2, -90);

        SpriteRotate.RotateFree(s, -90, fit, SpriteRotate.Interpolation.Area);

        // Same as the exact QuarterLeft test: 3 6 / 2 5 / 1 4.
        Assert.Equal(2, s.Width);
        Assert.Equal(3, s.Height);
        Assert.Equal(new byte[] { 3, 6, 2, 5, 1, 4 }, s.Indexed);
    }

    [Fact]
    public void PivotNoneKeepsTheSameAxisCoordinates()
    {
        var s = Indexed3x2();

        var r = SpriteRotate.RotateFree(s, 30, SpriteRotate.Fit(3, 2, 30),
            SpriteRotate.Interpolation.Area, SpriteRotate.Pivot.None);

        Assert.Equal(1, r.AxisX);
        Assert.Equal(1, r.AxisY);
    }

    [Fact]
    public void PivotAxisMovesTheAxisToWhereItsPointWent()
    {
        var s = Indexed3x2();
        var place = SpriteRotate.Fit(3, 2, 90);

        // The axis point (1,1) turns to (h-1-y, x) = (0,1); ToX/ToY already carry the fit
        // offset, so that is the destination coordinate the axis has to take.
        var r = SpriteRotate.RotateFree(s, 90, place,
            SpriteRotate.Interpolation.Area, SpriteRotate.Pivot.Axis);

        Assert.Equal(0, r.AxisX);
        Assert.Equal(1, r.AxisY);
    }

    [Fact]
    public void AnIndexedSpriteCanUseNearest()
    {
        var s = Indexed3x2();

        // Allowed, and palette-exact like any other sampler here — it just drops the source
        // pixels its single samples miss, which is why Area is the default.
        SpriteRotate.RotateFree(s, 30, SpriteRotate.Fit(3, 2, 30), SpriteRotate.Interpolation.Nearest);

        foreach (var i in s.Indexed!)
            Assert.Contains(i, new byte[] { 0, 1, 2, 3, 4, 5, 6 });
    }

    [Fact]
    public void AnIndexedSpriteRefusesBilinear()
    {
        var s = Indexed3x2();
        var fit = SpriteRotate.Fit(3, 2, 30);

        Assert.Throws<NotSupportedException>(() =>
            SpriteRotate.RotateFree(s, 30, fit, SpriteRotate.Interpolation.Bilinear));
    }

    [Fact]
    public void AreaAtAQuarterTurnIsLossless()
    {
        var s = Indexed3x2();
        var fit = SpriteRotate.Fit(3, 2, 90);

        SpriteRotate.RotateFree(s, 90, fit, SpriteRotate.Interpolation.Area);

        // Every destination pixel lands exactly on a source pixel, so one index takes the
        // whole weight and the result is the exact QuarterRight answer: 4 1 / 5 2 / 6 3.
        Assert.Equal(new byte[] { 4, 1, 5, 2, 6, 3 }, s.Indexed);
        Assert.Equal(2, s.Width);
        Assert.Equal(3, s.Height);
    }

    [Fact]
    public void AreaHandlesASpriteTooBigForAStackallocPerPixel()
    {
        // 2000 pixels is past the ~1MB the main thread's stack holds at 1KB per pixel,
        // which is where an earlier stackalloc-per-pixel version of this died outright.
        var s = new Sprite
        {
            Group = 0, Index = 0, Width = 2000, Height = 1, AxisX = 1000, AxisY = 0,
            Indexed = [.. Enumerable.Range(0, 2000).Select(i => (byte)(i % 200 + 1))],
        };

        var fit = SpriteRotate.Fit(2000, 1, 30);
        var r = SpriteRotate.RotateFree(s, 30, fit, SpriteRotate.Interpolation.Area);

        Assert.True(r.Changed);
        Assert.Equal(fit.Width, s.Width);
        Assert.Equal(fit.Height, s.Height);
        // 1.7 million destination pixels: at 1KB of stack each, the old per-pixel
        // stackalloc asked for 1.7GB.
        Assert.Equal(fit.Width * fit.Height, s.Indexed!.Length);
    }

    [Fact]
    public void AreaNeverInventsAnIndex()
    {
        // 1 2
        // 3 4
        // 5 6
        var s = new Sprite
        {
            Group = 0, Index = 0, Width = 2, Height = 3,
            Indexed = [1, 2, 3, 4, 5, 6],
        };

        SpriteRotate.RotateFree(s, 30, SpriteRotate.Fit(2, 3, 30), SpriteRotate.Interpolation.Area);

        // A blend would invent colors; taking the heaviest index cannot.
        foreach (var i in s.Indexed!)
            Assert.Contains(i, new byte[] { 0, 1, 2, 3, 4, 5, 6 });
    }

    [Fact]
    public void AnIndexedThinLineSurvivesAnOffAngleTurn()
    {
        // A one-pixel line is the detail that point sampling deletes. Area weighs every
        // source pixel the turn covers, so the line comes back at least as long as it went in.
        var s = new Sprite
        {
            Group = 0, Index = 0, Width = 9, Height = 9, AxisX = 4, AxisY = 4,
            Indexed = [.. Enumerable.Range(0, 81).Select(i => i % 9 == 4 ? (byte)1 : (byte)0)],
        };

        SpriteRotate.RotateFree(s, 30, SpriteRotate.Fit(9, 9, 30), SpriteRotate.Interpolation.Area);

        int kept = s.Indexed!.Count(i => i == 1);
        Assert.True(kept >= 9, $"the 9-pixel line came back as {kept} pixels");
    }

    [Fact]
    public void AreaKeepsMoreOfAThinLineThanNearest()
    {
        // Indexed, at 45° — the worst case, where point sampling's single samples miss the
        // line most often. Both samplings are legal here.
        static Sprite Line() => new()
        {
            Group = 0, Index = 0, Width = 9, Height = 9, AxisX = 4, AxisY = 4,
            Indexed = [.. Enumerable.Range(0, 81).Select(i => i % 9 == 4 ? (byte)1 : (byte)0)],
        };
        int Count(Sprite s) => s.Indexed!.Count(i => i == 1);

        var area = Line();
        SpriteRotate.RotateFree(area, 45, SpriteRotate.Fit(9, 9, 45), SpriteRotate.Interpolation.Area);
        var nearest = Line();
        SpriteRotate.RotateFree(nearest, 45, SpriteRotate.Fit(9, 9, 45), SpriteRotate.Interpolation.Nearest);

        Assert.True(Count(area) > Count(nearest),
            $"Area kept {Count(area)} line pixels, nearest kept {Count(nearest)}");
    }

    [Fact]
    public void AreaKeepsAUniformOpaqueColor()
    {
        var s = Direct2x2(30, 20, 10, 255);
        var fit = SpriteRotate.Fit(2, 2, 45);

        SpriteRotate.RotateFree(s, 45, fit, SpriteRotate.Interpolation.Area);

        int opaque = 0;
        for (int i = 0; i < s.Direct!.Length; i += 4)
        {
            var pixel = s.Direct.AsSpan(i, 4).ToArray();
            if (pixel[3] == 0) continue;
            Assert.Equal(new byte[] { 30, 20, 10, 255 }, pixel);
            opaque++;
        }
        Assert.True(opaque > 0, "the turn covered none of the sprite");
    }

    [Fact]
    public void BilinearAtNoTurnIsAlsoAnIdentity()
    {
        var s = Direct2x2(3, 5, 7, 255);
        var before = (byte[])[.. s.Direct!];

        SpriteRotate.RotateFree(s, 0, new SpriteRotate.Placement(2, 2, 0, 0),
            SpriteRotate.Interpolation.Bilinear);

        Assert.Equal(before, s.Direct);
    }

    [Fact]
    public void BilinearKeepsAUniformOpaqueColor()
    {
        var s = Direct2x2(30, 20, 10, 255);
        var fit = SpriteRotate.Fit(2, 2, 45);

        SpriteRotate.RotateFree(s, 45, fit, SpriteRotate.Interpolation.Bilinear);

        // Blending four copies of one opaque color has to give that color back, and the
        // corners the turn does not reach stay fully transparent. Anything else means the
        // alpha weighting or the unpremultiply is wrong.
        int opaque = 0;
        for (int i = 0; i < s.Direct!.Length; i += 4)
        {
            var pixel = s.Direct.AsSpan(i, 4).ToArray();
            if (pixel[3] == 0) continue;
            Assert.Equal(new byte[] { 30, 20, 10, 255 }, pixel);
            opaque++;
        }
        Assert.True(opaque > 0, "the turn covered none of the sprite");
    }

    [Fact]
    public void NearestLeavesUncoveredPixelsTransparent()
    {
        var s = Direct2x2(30, 20, 10, 255);
        var fit = SpriteRotate.Fit(2, 2, 45);

        SpriteRotate.RotateFree(s, 45, fit, SpriteRotate.Interpolation.Nearest);

        int transparent = 0;
        for (int i = 3; i < s.Direct!.Length; i += 4)
            if (s.Direct[i] == 0) transparent++;
        Assert.True(transparent > 0, "a 45° turn of a square should leave corners uncovered");
    }

    [Fact]
    public void AnImpossibleCanvasIsRefused()
    {
        var s = Indexed3x2();

        Assert.Throws<ArgumentOutOfRangeException>(() => SpriteRotate.RotateFree(s, 30,
            new SpriteRotate.Placement(0, 4, 0, 0), SpriteRotate.Interpolation.Nearest));
    }

    [Fact]
    public void LinkedSpriteIsRefused()
    {
        var target = Indexed3x2();
        var borrower = new Sprite { Group = 0, Index = 1, SharesWith = target };

        Assert.Throws<InvalidOperationException>(() => SpriteRotate.RotateFree(borrower, 30,
            SpriteRotate.Fit(3, 2, 30), SpriteRotate.Interpolation.Nearest));
    }
}
