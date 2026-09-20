using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

public class PaletteDepthTests
{
    private static Rgba Color(byte value) => new(value, value, value, 255);

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void RecognizesEverySupportedPaletteSize(int colors)
    {
        var palette = new Palette { Colors = Enumerable.Range(0, colors).Select(i => Color((byte)i)).ToArray() };
        var file = new SffFile { Version = SffVersions.V2, Palettes = [palette] };

        Assert.Contains(colors, PaletteDepth.SupportedColorCounts);
        Assert.False(PaletteDepth.CanConvert(file, palette, colors));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(31, false)]
    public void ShrinkingRequiresEveryUsingSpriteIndexToFit(int index, bool expected)
    {
        var palette = new Palette { Colors = Enumerable.Range(0, 256).Select(i => Color((byte)i)).ToArray() };
        var sprite = new Sprite { Width = 1, Height = 1, Indexed = [(byte)index], Palette = palette };
        var file = new SffFile { Version = SffVersions.V2, Palettes = [palette], Sprites = [sprite] };

        Assert.Equal(expected, PaletteDepth.CanConvert(file, palette, 2));
    }

    [Fact]
    public void AUsedIndexAboveTargetDepthDisablesConversion()
    {
        var palette = new Palette { Colors = Enumerable.Range(0, 256).Select(i => Color((byte)i)).ToArray() };
        var sprite = new Sprite { Width = 1, Height = 1, Indexed = [32], Palette = palette };
        var file = new SffFile { Version = SffVersions.V2, Palettes = [palette], Sprites = [sprite] };

        Assert.False(PaletteDepth.CanConvert(file, palette, 32));
        Assert.True(PaletteDepth.CanConvert(file, palette, 64));
    }

    [Fact]
    public void AnyLargerSupportedDepthCanBeSelectedWithoutRemapping()
    {
        var palette = new Palette { Colors = Enumerable.Range(0, 64).Select(i => Color((byte)i)).ToArray() };
        var file = new SffFile { Version = SffVersions.V2, Palettes = [palette] };

        Assert.True(PaletteDepth.CanConvert(file, palette, 128));
    }

    [Fact]
    public void FiveBitConversionRefusesUndecodedSpritesUsingThePalette()
    {
        var palette = new Palette { Colors = Enumerable.Range(0, 256).Select(i => Color((byte)i)).ToArray() };
        var sprite = new Sprite { Width = 1, Height = 1, Palette = palette, DecodeError = "bad pixel data" };
        var file = new SffFile { Version = SffVersions.V2, Palettes = [palette], Sprites = [sprite] };

        Assert.False(PaletteDepth.CanConvert(file, palette, 32));
    }

    [Fact]
    public void FiveBitConversionPreservesTheFirst32Entries()
    {
        var palette = new Palette { Colors = Enumerable.Range(0, 256).Select(i => Color((byte)i)).ToArray() };

        var converted = PaletteDepth.Convert(palette, 32);

        Assert.Equal(palette.Colors[..32], converted);
    }

    [Fact]
    public void EightBitConversionPadsWithOpaqueBlack()
    {
        var colors = Enumerable.Range(0, 32).Select(i => Color((byte)i)).ToArray();
        var palette = new Palette { Colors = colors };

        var converted = PaletteDepth.Convert(palette, 256);

        Assert.Equal(colors, converted[..32]);
        Assert.All(converted[32..], color => Assert.Equal(new Rgba(0, 0, 0, 255), color));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 2)]
    [InlineData(8, 3)]
    [InlineData(16, 4)]
    [InlineData(32, 5)]
    [InlineData(64, 6)]
    [InlineData(128, 7)]
    [InlineData(256, 8)]
    public void SaveRoundTripKeepsEverySupportedIndexedDepth(int colors, int depth)
    {
        var palette = new Palette { Group = 1, Index = 1, Colors = Enumerable.Range(0, colors).Select(i => Color((byte)i)).ToArray() };
        var sprite = new Sprite { Group = 0, Index = 0, Width = 2, Height = 1, Indexed = [0, 1], Palette = palette, PixelsDirty = true };
        var file = new SffFile { Version = SffVersions.V2, Palettes = [palette], Sprites = [sprite] };
        string path = Path.Combine(Path.GetTempPath(), $"palette-depth-{Guid.NewGuid():N}.sff");
        try
        {
            file.Save(path);
            var reopened = SffFile.Open(path);

            Assert.Equal(depth, reopened.Sprites[0].SrcDepth);
            Assert.Equal(sprite.Indexed, reopened.Sprites[0].Indexed);
            Assert.Equal(colors, reopened.Palettes[0].Colors.Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OnlyIndependentEmbeddedV2PalettesCanChangeDepth()
    {
        var colors = new Rgba[32];
        var palette = new Palette { Colors = colors };
        var file = new SffFile { Version = SffVersions.V2, Palettes = [palette] };

        Assert.True(PaletteDepth.CanConvert(file, palette, 256));

        palette.LinksTo = new Palette { Colors = colors };
        Assert.False(PaletteDepth.CanConvert(file, palette, 256));
        palette.LinksTo = null;
        palette.Source = PaletteSource.External;
        Assert.False(PaletteDepth.CanConvert(file, palette, 256));
        palette.Source = PaletteSource.Embedded;
        file.Version = SffVersions.V1;
        Assert.False(PaletteDepth.CanConvert(file, palette, 256));
    }
}
