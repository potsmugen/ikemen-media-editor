using Editor;
using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>The pixel wrapper Paint Mode edits: indexed bytes or true-color BGRA.</summary>
public class PaintPixelsTests
{
    [Fact]
    public void FromWrapsTheSpritesOwnArray()
    {
        var s = new Sprite { Width = 2, Height = 1, Indexed = [1, 2] };

        var p = PaintPixels.From(s)!;
        p.Erase(0);

        Assert.False(p.TrueColor);
        Assert.Equal(new byte[] { 0, 2 }, s.Indexed);
    }

    [Fact]
    public void FromPrefersTrueColorAndIsNullWhenUndecoded()
    {
        var s = new Sprite { Width = 1, Height = 1, Direct = [1, 2, 3, 255] };
        Assert.True(PaintPixels.From(s)!.TrueColor);
        Assert.Null(PaintPixels.From(new Sprite { Width = 1, Height = 1 }));
    }

    [Fact]
    public void EraseZeroesAllFourBytesOfATrueColorPixel()
    {
        var p = new PaintPixels([1, 2, 3, 255, 9, 8, 7, 6], true);

        p.Erase(1);

        Assert.Equal(new byte[] { 1, 2, 3, 255, 0, 0, 0, 0 }, p.Data);
    }

    [Fact]
    public void IsClearedIgnoresAlphaOnlyTransparency()
    {
        var p = new PaintPixels([5, 5, 5, 0, 0, 0, 0, 0], true);

        Assert.False(p.IsCleared(0)); // alpha 0 but RGB left: the eraser would still change it
        Assert.True(p.IsCleared(1));
        Assert.True(new PaintPixels([0, 3], false).IsCleared(0));
        Assert.False(new PaintPixels([0, 3], false).IsCleared(1));
    }

    [Fact]
    public void SameComparesBytesAndKind()
    {
        var a = new PaintPixels([0, 0, 0, 0], true);

        Assert.True(a.Same(a.Snapshot()));
        Assert.False(a.Same(new PaintPixels([0, 0, 0, 0], false)));
        Assert.False(a.Same(new PaintPixels([0, 0, 0, 1], true)));
    }

    [Fact]
    public void SnapshotIsIndependentOfTheOriginal()
    {
        var a = new PaintPixels([1, 2], false);
        var snap = a.Snapshot();

        a.Erase(0);

        Assert.Equal(new byte[] { 1, 2 }, snap.Data);
    }

    [Fact]
    public void FillReplacesOnlyTheConnectedRegion()
    {
        // 1 1 2
        // 2 1 1   (the 2 at the left of row 2 is not connected to the 2 at the top right)
        byte[] d = [1, 1, 2, 2, 1, 1];

        bool changed = PaintPixels.FloodFill(d, 3, 2, 1, 0, 0, [7]);

        Assert.True(changed);
        Assert.Equal(new byte[] { 7, 7, 2, 2, 7, 7 }, d);
    }

    [Fact]
    public void FillWithTheSameColorChangesNothing()
    {
        byte[] d = [1, 1, 2];

        Assert.False(PaintPixels.FloodFill(d, 3, 1, 1, 0, 0, [1]));
        Assert.Equal(new byte[] { 1, 1, 2 }, d);
    }

    [Fact]
    public void FillMasksTrueColorByAllFourBytes()
    {
        // Two pixels differ only in alpha, so the wand must not join them.
        byte[] d = [9, 9, 9, 255, 9, 9, 9, 255, 9, 9, 9, 0];
        var mask = new bool[3];

        PaintPixels.FloodFill(d, 3, 1, 4, 0, 0, null, mask);

        Assert.Equal(new[] { true, true, false }, mask);
    }
}
