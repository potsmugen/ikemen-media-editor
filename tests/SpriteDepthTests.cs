using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

public class SpriteDepthTests
{
    private static readonly Rgba Red = new(255, 0, 0, 255);
    private static readonly Rgba Blue = new(0, 0, 255, 255);

    private static byte[] Bgra(params Rgba[] pixels)
    {
        var bgra = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            bgra[i * 4] = pixels[i].B; bgra[i * 4 + 1] = pixels[i].G;
            bgra[i * 4 + 2] = pixels[i].R; bgra[i * 4 + 3] = pixels[i].A;
        }
        return bgra;
    }

    [Fact]
    public void ToTrueColorUsesEachIndexsPaletteColorAndAlpha()
    {
        var palette = new Palette { Colors = [new Rgba(10, 20, 30, 0), Red, new Rgba(0, 255, 0, 128)] };
        var sprite = new Sprite { Width = 3, Height = 1, Indexed = [0, 1, 2] };

        var bgra = SpriteDepth.ToTrueColor(sprite, palette);

        // Index 0 is clear here because its palette alpha is 0, and a clear pixel keeps no color
        Assert.Equal(Bgra(new Rgba(0, 0, 0, 0), Red, new Rgba(0, 255, 0, 128)), bgra);
    }

    [Fact]
    public void ToTrueColorKeepsAnOpaqueIndexZeroOpaque()
    {
        var palette = new Palette { Colors = [new Rgba(10, 20, 30, 255), Red] };
        var sprite = new Sprite { Width = 2, Height = 1, Indexed = [0, 1] };

        Assert.Equal(Bgra(new Rgba(10, 20, 30, 255), Red), SpriteDepth.ToTrueColor(sprite, palette));
    }

    [Fact]
    public void ToIndexedKeepsColorsExactAndClearPixelsOnTheMask()
    {
        var pixels = Bgra(Red, new Rgba(5, 5, 5, 0), Blue, Blue);

        var (palette, reduced, indexes) = SpriteDepth.ToIndexed([pixels]);

        Assert.False(reduced);
        Assert.Equal(256, palette.Colors.Length);
        Assert.Equal(new Rgba(0, 0, 0, 0), palette.Colors[0]);
        Assert.Equal(new byte[] { 2, 0, 1, 1 }, indexes[0]); // most used color first
        Assert.Equal(Blue, palette.Colors[1]);
        Assert.Equal(Red, palette.Colors[2]);
    }

    [Fact]
    public void ToIndexedSharesOnePaletteAcrossSprites()
    {
        var (palette, _, indexes) = SpriteDepth.ToIndexed([Bgra(Red), Bgra(Blue, Blue)]);

        Assert.Equal(2, indexes.Length);
        Assert.Equal(Red, palette.Colors[indexes[0][0]]);
        Assert.Equal(Blue, palette.Colors[indexes[1][0]]);
    }

    [Fact]
    public void ToIndexedOfAFullyClearSpriteIsAllMask()
    {
        var (palette, reduced, indexes) = SpriteDepth.ToIndexed([Bgra(new Rgba(9, 9, 9, 0), new Rgba(0, 0, 0, 0))]);

        Assert.False(reduced);
        Assert.Equal(new Rgba(0, 0, 0, 0), palette.Colors[0]);
        Assert.Equal(new byte[] { 0, 0 }, indexes[0]);
    }

    [Fact]
    public void RoundTripThroughTrueColorRestoresTheSameLook()
    {
        var palette = new Palette { Colors = [new Rgba(0, 0, 0, 0), Red, Blue] };
        var sprite = new Sprite { Width = 4, Height = 1, Indexed = [0, 1, 2, 1] };

        var bgra = SpriteDepth.ToTrueColor(sprite, palette);
        var (back, _, indexes) = SpriteDepth.ToIndexed([bgra]);
        var again = new Sprite { Width = 4, Height = 1, Indexed = indexes[0] };

        Assert.Equal(bgra, SpriteDepth.ToTrueColor(again, back));
    }
}
