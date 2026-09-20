using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

public class QuantizeTests
{
    private static Rgba[] WithMask(params Rgba[] colors) => [new Rgba(0, 0, 0, 0), .. colors];

    [Fact]
    public void KeepsTheMaskAndColorsThatAlreadyFit()
    {
        var reduced = Quantize.Reduce(WithMask(new Rgba(255, 0, 0, 255), new Rgba(0, 0, 255, 255)));

        Assert.Equal(3, reduced.Colors.Length);
        Assert.Equal(new Rgba(0, 0, 0, 0), reduced.Colors[0]);
        Assert.Contains(new Rgba(255, 0, 0, 255), reduced.Colors);
        Assert.Contains(new Rgba(0, 0, 255, 255), reduced.Colors);
    }

    [Fact]
    public void CapsAtTheRequestedNumberOfColors()
    {
        var colors = new Rgba[301];
        colors[0] = new Rgba(0, 0, 0, 0);
        for (int i = 1; i < colors.Length; i++)
        {
            int n = i - 1;
            colors[i] = new Rgba((byte)(n & 0xFF), (byte)(n >> 8), 0, 255);
        }

        var reduced = Quantize.Reduce(colors, 255);

        Assert.Equal(256, reduced.Colors.Length);
        Assert.Equal(new Rgba(0, 0, 0, 0), reduced.Colors[0]);
    }

    [Fact]
    public void AveragesTheColorsItCannotKeep()
    {
        var reduced = Quantize.Reduce(
            WithMask(new Rgba(0, 0, 0, 255), new Rgba(255, 255, 255, 255)), 1);

        Assert.Equal(2, reduced.Colors.Length);
        Assert.Equal(new Rgba(0, 0, 0, 0), reduced.Colors[0]);
        Assert.Equal(new Rgba(127, 127, 127, 255), reduced.Colors[1]);
    }

    [Fact]
    public void WeightedMedianCutKeepsFrequentColorsOverRareShades()
    {
        var colors = WithMask(
            new Rgba(0, 0, 0, 255),
            new Rgba(10, 10, 10, 255),
            new Rgba(255, 255, 255, 255));
        long[] weights = [0, 1, 1, 100];

        var reduced = Quantize.Reduce(colors, weights, 2);

        Assert.Equal(new Rgba(5, 5, 5, 255), reduced.Colors[1]);
        Assert.Equal(new Rgba(255, 255, 255, 255), reduced.Colors[2]);
    }

    [Fact]
    public void RefusesACapBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantize.Reduce(WithMask(), 0));
        Assert.Throws<ArgumentNullException>(() => Quantize.Reduce(null!, 4));
    }
}
