using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

public class PaletteMergeTests
{
    private static readonly Rgba Mask = new(0, 0, 0, 0);
    private static readonly Rgba Red = new(255, 0, 0, 255);
    private static readonly Rgba Green = new(0, 255, 0, 255);
    private static readonly Rgba Blue = new(0, 0, 255, 255);
    private static readonly Rgba White = new(255, 255, 255, 255);

    private static bool[] SkipAt(int length, params int[] indexes)
    {
        var skip = new bool[length];
        foreach (int i in indexes) skip[i] = true;
        return skip;
    }

    [Fact]
    public void KeepsEveryEntryAndIndexesWhereSlotsAreFree()
    {
        var a = new PaletteMerge.Source([Mask, Red, Green, White, White]);
        var b = new PaletteMerge.Source([Mask, White, White, White, Blue]);

        var r = PaletteMerge.Build([a, b]);

        Assert.Equal([Mask, Red, Green, White, Blue], r.Colors);
        Assert.Equal(3, r.Maps[1][1]); // white shares slot 3
        Assert.Equal(4, r.Maps[1][4]);
        Assert.Equal(4, r.UniqueColors);
        Assert.False(r.Reduced);
    }

    [Fact]
    public void CollidingColorsMoveToFreeSlotsMostUsedFirst()
    {
        var a = new PaletteMerge.Source([Mask, Red, Green]);
        var heavy = new long[256];
        heavy[2] = 50;
        var b = new PaletteMerge.Source([Mask, Blue, White], heavy);

        var r = PaletteMerge.Build([a, b]);

        Assert.Equal(Red, r.Colors[1]);
        Assert.Equal(Green, r.Colors[2]);
        Assert.Equal(3, r.Maps[1][2]); // white is used more, so it takes the first free slot
        Assert.Equal(4, r.Maps[1][1]);
        Assert.Equal(Blue, r.Colors[4]);
    }

    [Fact]
    public void SkippedPaddingIsLeftOut()
    {
        var a = new PaletteMerge.Source([Mask, Red, Green, Green], Skip: SkipAt(4, 2, 3));
        var b = new PaletteMerge.Source([Mask, Blue, Green, Green], Skip: SkipAt(4, 2, 3));

        var r = PaletteMerge.Build([a, b]);

        Assert.Equal(2, r.UniqueColors);
        Assert.Equal(2, r.Maps[1][1]); // blue takes the slot the padding left free
        Assert.Equal(Blue, r.Colors[2]);
    }

    [Fact]
    public void FillColorsTheFreeSlots()
    {
        var green = new Rgba(0, 255, 0, 255);
        var a = new PaletteMerge.Source([Mask, Red, green, green], Skip: SkipAt(4, 2, 3));

        var r = PaletteMerge.Build([a], fill: Blue);

        Assert.Equal([Mask, Red, Blue, Blue], r.Colors);
    }

    [Fact]
    public void MaskComesFromTheFirstPalette()
    {
        var a = new PaletteMerge.Source([Mask, Red]);
        var b = new PaletteMerge.Source([new Rgba(1, 2, 3, 0), Red]);

        var r = PaletteMerge.Build([a, b]);

        Assert.Equal(Mask, r.Colors[0]);
        Assert.Equal(0, r.Maps[1][0]);
    }

    [Fact]
    public void SizeIsTheLargestPaletteOrThePowerOfTwoThatFits()
    {
        var a = new PaletteMerge.Source([Mask, Red]);
        var b = new PaletteMerge.Source([Mask, Green, Blue]);

        Assert.Equal(4, PaletteMerge.Build([a, b]).Colors.Length);
    }

    [Fact]
    public void ColorsDifferingOnlyInAlphaStaySeparate()
    {
        var a = new PaletteMerge.Source([Mask, Red, new Rgba(255, 0, 0, 128)]);
        Assert.Equal(2, PaletteMerge.Build([a]).UniqueColors);
    }

    [Fact]
    public void ReducesOver256AndMapsEveryIndex()
    {
        Rgba[] Ramp(int channel)
        {
            var c = new Rgba[256];
            c[0] = Mask;
            for (int i = 1; i < 256; i++)
                c[i] = channel == 0 ? new Rgba((byte)i, 0, 0, 255) : new Rgba(0, (byte)i, 0, 255);
            return c;
        }
        var a = new PaletteMerge.Source(Ramp(0));
        var b = new PaletteMerge.Source(Ramp(1));

        var r = PaletteMerge.Build([a, b]);

        Assert.True(r.Reduced);
        Assert.Equal(510, r.UniqueColors);
        Assert.Equal(256, r.Colors.Length);
        foreach (var map in r.Maps)
            for (int i = 1; i < 256; i++) Assert.InRange(map[i], 1, 255);
        // Most reds keep their index (first palette wins its slots).
        int kept = Enumerable.Range(1, 255).Count(i => r.Maps[0][i] == i);
        Assert.True(kept > 50, $"only {kept} kept their index");
    }

    [Fact]
    public void RefusesWhenEverythingIsSkipped()
    {
        var a = new PaletteMerge.Source([Mask, Red], Skip: SkipAt(2, 1));
        Assert.Throws<InvalidOperationException>(() => PaletteMerge.Build([a]));
    }

    [Fact]
    public void GroupedReduceReportsEachInputsEntry()
    {
        var colors = new[] { Mask, Red, new Rgba(250, 0, 0, 255), Blue };
        var (reduced, groupOf) = Quantize.ReduceGrouped(colors, [0, 1, 1, 1], 2);

        Assert.Equal(3, reduced.Length);
        Assert.Equal(groupOf[1], groupOf[2]);
        Assert.NotEqual(groupOf[1], groupOf[3]);
        Assert.Equal(Blue, reduced[groupOf[3]]);
    }
}
