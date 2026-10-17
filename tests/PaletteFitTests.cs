using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

public class PaletteFitTests
{
    private static Rgba C(byte r, byte g, byte b) => new(r, g, b, 255);

    private static Palette Pal(params Rgba[] colors) => new() { Colors = colors };

    private static Sprite Indexed(Palette pal, params byte[] pixels) =>
        new() { Width = pixels.Length, Height = 1, Indexed = pixels, Palette = pal };

    private static Sprite Direct(params Rgba[] pixels) => new()
    {
        Width = pixels.Length, Height = 1,
        Direct = pixels.SelectMany(p => new[] { p.B, p.G, p.R, p.A }).ToArray(),
    };

    [Fact]
    public void KeepModeComparesUsedIndexesAndIgnoresTheMask()
    {
        var own = Pal(C(255, 0, 255), C(1, 2, 3), C(4, 5, 6));
        var fit = new PaletteFit([Indexed(own, 0, 1, 2)]);

        Assert.Equal(1, fit.Matches(Pal(C(0, 0, 0), C(1, 2, 3), C(4, 5, 6), C(9, 9, 9)), keepIndexes: true));
        Assert.Equal(0, fit.Matches(Pal(C(0, 0, 0), C(4, 5, 6), C(1, 2, 3)), keepIndexes: true));
        Assert.Equal(0, fit.Matches(Pal(C(0, 0, 0), C(1, 2, 3)), keepIndexes: true));
    }

    [Fact]
    public void NearestModeNeedsEveryOpaqueColorOutsideIndexZero()
    {
        var fit = new PaletteFit([Direct(C(1, 2, 3), new Rgba(7, 7, 7, 0), C(4, 5, 6))]);

        Assert.Equal(1, fit.Matches(Pal(C(0, 0, 0), C(4, 5, 6), C(1, 2, 3)), keepIndexes: false));
        Assert.Equal(0, fit.Matches(Pal(C(1, 2, 3), C(4, 5, 6)), keepIndexes: false));
    }

    [Fact]
    public void AlphaMustMatchToo()
    {
        var own = Pal(C(0, 0, 0), C(1, 2, 3));
        var see = Pal(C(0, 0, 0), new Rgba(1, 2, 3, 0));

        Assert.Equal(0, new PaletteFit([Indexed(own, 1)]).Matches(see, keepIndexes: true));
        Assert.Equal(0, new PaletteFit([Direct(C(1, 2, 3))]).Matches(see, keepIndexes: false));
    }

    [Fact]
    public void CountsMatchesAcrossTheBatch()
    {
        var target = Pal(C(0, 0, 0), C(1, 2, 3));
        var fit = new PaletteFit([Direct(C(1, 2, 3)), Direct(C(9, 9, 9)), null]);

        Assert.Equal(3, fit.Count);
        Assert.Equal(1, fit.Matches(target, keepIndexes: false));
    }
}
