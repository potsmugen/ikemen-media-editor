using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

public class GifImportTests
{
    private static byte[] Pixels(int width, int height, params Rgba[] colors)
    {
        var bgra = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            var c = colors[i % colors.Length];
            bgra[i * 4] = c.B; bgra[i * 4 + 1] = c.G; bgra[i * 4 + 2] = c.R; bgra[i * 4 + 3] = c.A;
        }
        return bgra;
    }

    private static GifImport.DecodedFrame Frame(params Rgba[] colors) =>
        new(colors.Length, 1, Pixels(colors.Length, 1, colors), 5);

    /// <summary>count distinct colors, spread over two channels so none collide.</summary>
    private static GifImport.DecodedFrame Ramp(int count)
    {
        var bgra = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            bgra[i * 4 + 2] = (byte)(i & 0xFF);
            bgra[i * 4 + 1] = (byte)(i >> 8);
            bgra[i * 4 + 3] = 255;
        }
        return new GifImport.DecodedFrame(count, 1, bgra, 5);
    }

    [Fact]
    public void FramesShareOnePalette()
    {
        var frames = new List<GifImport.DecodedFrame>
        {
            Frame(new Rgba(255, 0, 0, 255), new Rgba(255, 0, 0, 255)),
            Frame(new Rgba(0, 0, 255, 255), new Rgba(0, 0, 255, 255)),
        };
        var colors = GifImport.SharedColors(frames);
        Assert.Equal(2, colors.ColorCount);

        var sprites = GifImport.ToSprites(frames, ImportPlan.Own(), colors.ToPalette(), out var reports);

        Assert.Equal(2, sprites.Count);
        Assert.Same(sprites[0].Palette, sprites[1].Palette); // one instance, not two equal ones
        Assert.Equal(256, sprites[0].Palette!.Colors.Length);
        Assert.Equal(new Rgba(0, 0, 0, 255), sprites[0].Palette!.Colors[3]);
        Assert.All(sprites[0].Indexed!, i => Assert.Equal(1, i));
        Assert.All(sprites[1].Indexed!, i => Assert.Equal(2, i));
        Assert.All(reports, r => Assert.True(r.Lossless));
    }

    [Fact]
    public void UnionFollowsPlayOrder()
    {
        var frames = new List<GifImport.DecodedFrame>
        {
            Frame(new Rgba(1, 0, 0, 255), new Rgba(2, 0, 0, 255)),
            Frame(new Rgba(2, 0, 0, 255), new Rgba(3, 0, 0, 255)),
        };

        var colors = GifImport.SharedColors(frames).Colors;

        Assert.Equal(4, colors.Length); // the mask, then three colors
        Assert.Equal(new Rgba(1, 0, 0, 255), colors[1]);
        Assert.Equal(new Rgba(2, 0, 0, 255), colors[2]);
        Assert.Equal(new Rgba(3, 0, 0, 255), colors[3]);
    }

    [Fact]
    public void FramesWithReorderedTablesShareTheSameColors()
    {
        // GIF's 256 is per color table, and a frame's local table is often a
        // reordered duplicate of the global one.
        var frames = new List<GifImport.DecodedFrame>
        {
            Frame(new Rgba(255, 0, 0, 255), new Rgba(0, 255, 0, 255)),
            Frame(new Rgba(0, 255, 0, 255), new Rgba(255, 0, 0, 255)),
        };

        Assert.Equal(2, GifImport.SharedColors(frames).ColorCount);
    }

    [Fact]
    public void TransparentPixelsAreNotCountedAsColors()
    {
        var frames = new List<GifImport.DecodedFrame>
        {
            Frame(new Rgba(255, 0, 0, 255), new Rgba(0, 0, 0, 0)),
        };

        var colors = GifImport.SharedColors(frames);
        Assert.Equal(1, colors.ColorCount);

        var sprite = GifImport.ToSprites(frames, ImportPlan.Own(), colors.ToPalette(), out _)[0];
        Assert.Equal(1, sprite.Indexed![0]);
        Assert.Equal(Sprite.TransparentIndex, sprite.Indexed[1]);
    }

    [Fact]
    public void FramesNeedingTooManyColorsAreReported()
    {
        var frames = new List<GifImport.DecodedFrame> { Ramp(300) };

        var colors = GifImport.SharedColors(frames);

        Assert.Equal(300, colors.ColorCount);
        Assert.True(colors.Overflows);
        Assert.Throws<NotSupportedException>(colors.ToPalette);
    }

    [Fact]
    public void SharedPaletteReductionWeightsColorsByPixelFrequency()
    {
        var red = new Rgba(255, 0, 0, 255);
        var blue = new Rgba(0, 0, 255, 255);
        var colors = GifImport.SharedColors([Frame(red, red, red, red, blue)]);

        var reduced = colors.Reduced(1);

        Assert.Equal(new Rgba(204, 0, 51, 255), reduced.Colors[1]);
    }

    [Fact]
    public void BuildingWithoutAPaletteRefusesFramesThatOverflow()
    {
        var frames = new List<GifImport.DecodedFrame> { Ramp(300) };

        Assert.Throws<NotSupportedException>(() => GifImport.ToSprites(frames, ImportPlan.Own(), out _));
    }

    [Fact]
    public void AReducedPaletteFitsAndEveryFrameStillBuilds()
    {
        var frames = new List<GifImport.DecodedFrame> { Ramp(300), Ramp(300) };
        var reduced = GifImport.SharedColors(frames).Reduced();

        Assert.Equal(GifImport.MaxSharedColors + 1, reduced.Colors.Length);

        var sprites = GifImport.ToSprites(frames, ImportPlan.Own(), reduced, out _);
        Assert.Equal(2, sprites.Count);
        Assert.All(sprites, s => Assert.All(s.Indexed!, i => Assert.True(i < reduced.Colors.Length)));
    }

    [Fact]
    public void OneFrameStillGetsItsOwnPalette()
    {
        var frames = new List<GifImport.DecodedFrame> { Frame(new Rgba(255, 0, 0, 255)) };

        var sprites = GifImport.ToSprites(frames, ImportPlan.Own(), out var reports);

        Assert.Single(sprites);
        Assert.Equal(256, sprites[0].Palette!.Colors.Length);
        Assert.Equal(new Rgba(0, 0, 0, 255), sprites[0].Palette!.Colors[2]);
        Assert.True(reports[0].Lossless);
    }

    [Fact]
    public void TrueColorFramesIgnoreTheSharedPalette()
    {
        var frames = new List<GifImport.DecodedFrame> { Frame(new Rgba(255, 0, 0, 255)) };

        var sprites = GifImport.ToSprites(frames, ImportPlan.TrueColor32(), out _);

        Assert.Null(sprites[0].Palette);
        Assert.Null(sprites[0].Indexed);
        Assert.Equal(4, sprites[0].Direct!.Length);
    }
}
