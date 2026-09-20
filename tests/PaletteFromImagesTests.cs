using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

public class PaletteFromImagesTests
{
    private static byte[] Image(params Rgba[] pixels)
    {
        var bgra = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            bgra[i * 4] = pixels[i].B; bgra[i * 4 + 1] = pixels[i].G;
            bgra[i * 4 + 2] = pixels[i].R; bgra[i * 4 + 3] = pixels[i].A;
        }
        return PngExport.Encode(bgra, pixels.Length, 1);
    }

    private static readonly Rgba Red = new(255, 0, 0, 255);
    private static readonly Rgba Blue = new(0, 0, 255, 255);
    private static readonly Rgba Green = new(0, 255, 0, 255);

    [Fact]
    public void OrdersColorsByCountAcrossImagesAndPadsTo256()
    {
        var builder = new PaletteFromImages();
        builder.Add(Image(Red, Blue, Blue));
        builder.Add(Image(Green, Blue, Green, Green));

        var (palette, reduced) = builder.Build();

        Assert.False(reduced);
        Assert.Equal(256, palette.Colors.Length);
        Assert.Equal(new Rgba(0, 0, 0, 0), palette.Colors[0]);
        Assert.Equal(Blue, palette.Colors[1]);
        Assert.Equal(Green, palette.Colors[2]);
        Assert.Equal(Red, palette.Colors[3]);
        Assert.Equal(new Rgba(0, 0, 0, 255), palette.Colors[4]);
    }

    [Fact]
    public void EqualCountsKeepFirstSeenOrder()
    {
        var builder = new PaletteFromImages();
        builder.Add(Image(Green, Red));

        var (palette, _) = builder.Build();

        Assert.Equal(Green, palette.Colors[1]);
        Assert.Equal(Red, palette.Colors[2]);
    }

    [Fact]
    public void SkipsTransparentPixels()
    {
        var builder = new PaletteFromImages();
        builder.Add(Image(new Rgba(9, 9, 9, 0), Red));

        Assert.Equal(1, builder.ColorCount);
        Assert.Equal(Red, builder.Build().Palette.Colors[1]);
    }

    [Fact]
    public void ReducesOver255Colors()
    {
        var pixels = new Rgba[300];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = new Rgba((byte)(i & 0xFF), (byte)(i >> 8), 7, 255);
        var builder = new PaletteFromImages();
        builder.Add(Image(pixels));

        var (palette, reduced) = builder.Build();

        Assert.True(reduced);
        Assert.Equal(256, palette.Colors.Length);
        Assert.Equal(new Rgba(0, 0, 0, 0), palette.Colors[0]);
    }

    [Fact]
    public void RefusesImagesWithNoVisibleColors()
    {
        var builder = new PaletteFromImages();
        builder.Add(Image(new Rgba(0, 0, 0, 0)));

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }
}
