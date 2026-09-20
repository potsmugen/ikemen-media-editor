using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

public class SpriteScaleTests
{
    private static Sprite Indexed(params byte[] pixels) => new()
    {
        Group = 0, Index = 0, Width = 2, Height = 2, AxisX = 1, AxisY = -1,
        Indexed = pixels, Palette = new Palette { Colors = [new Rgba(0, 0, 0, 0), new Rgba(255, 0, 0, 255)] },
    };

    private static Sprite Direct(params byte[] pixels) => new()
    {
        Group = 0, Index = 0, Width = 2, Height = 2, AxisX = 1, AxisY = 1, Direct = pixels,
    };

    [Fact]
    public void NearestScalesIndexedPixelsAndKeepsPalette()
    {
        var sprite = Indexed(1, 2, 3, 4);
        var palette = sprite.Palette;

        var result = SpriteScale.Resize(sprite, 4, 4, SpriteRotate.Interpolation.Nearest);

        Assert.Equal(new byte[] { 1, 1, 2, 2, 1, 1, 2, 2, 3, 3, 4, 4, 3, 3, 4, 4 }, sprite.Indexed);
        Assert.Same(palette, sprite.Palette);
        Assert.Equal((4, 4), (result.Width, result.Height));
        Assert.Equal((2, -2), (result.AxisX, result.AxisY));
    }

    [Fact]
    public void AreaDownscalingChoosesTheMostCoveredIndex()
    {
        var sprite = Indexed(1, 2, 2, 2);

        SpriteScale.Resize(sprite, 1, 1, SpriteRotate.Interpolation.Area);

        Assert.Equal(new byte[] { 2 }, sprite.Indexed);
    }

    [Fact]
    public void AreaTiesFavorSolidIndexOverTransparent()
    {
        var sprite = Indexed(0, 1, 0, 1);

        SpriteScale.Resize(sprite, 1, 1, SpriteRotate.Interpolation.Area);

        Assert.Equal(new byte[] { 1 }, sprite.Indexed);
    }

    [Fact]
    public void BilinearIsRejectedForIndexedSpritesBeforeChangingThem()
    {
        var sprite = Indexed(1, 2, 3, 4);
        var pixels = sprite.Indexed!.ToArray();

        Assert.Throws<NotSupportedException>(() => SpriteScale.Resize(sprite, 3, 3, SpriteRotate.Interpolation.Bilinear));
        Assert.Equal(pixels, sprite.Indexed);
        Assert.Equal((2, 2), (sprite.Width, sprite.Height));
    }

    [Fact]
    public void BilinearBlendsTrueColorWithAlphaPremultiplication()
    {
        var sprite = Direct(
            0, 0, 255, 0, 0, 0, 255, 255,
            0, 0, 255, 0, 0, 0, 255, 255);

        SpriteScale.Resize(sprite, 3, 2, SpriteRotate.Interpolation.Bilinear);

        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 128, 128, 0, 0, 255, 255 }, sprite.Direct!.Take(12));
    }

    [Fact]
    public void AreaPreservesUniformTrueColor()
    {
        var sprite = Direct(30, 20, 10, 255, 30, 20, 10, 255, 30, 20, 10, 255, 30, 20, 10, 255);

        SpriteScale.Resize(sprite, 1, 1, SpriteRotate.Interpolation.Area);

        Assert.Equal(new byte[] { 30, 20, 10, 255 }, sprite.Direct);
    }

    [Fact]
    public void ScalingAnUnchangedSizeDoesNothing()
    {
        var sprite = Indexed(1, 2, 3, 4);
        var pixels = sprite.Indexed;

        var result = SpriteScale.Resize(sprite, 2, 2, SpriteRotate.Interpolation.Nearest);

        Assert.False(result.Changed);
        Assert.Same(pixels, sprite.Indexed);
        Assert.False(sprite.PixelsDirty);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    [InlineData(65536, 2)]
    [InlineData(2, 65536)]
    public void InvalidDimensionsAreRejected(int width, int height)
    {
        var sprite = Indexed(1, 2, 3, 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => SpriteScale.Resize(sprite, width, height, SpriteRotate.Interpolation.Nearest));
    }

    [Fact]
    public void BorrowedSpriteIsRefused()
    {
        var target = Indexed(1, 2, 3, 4);
        var borrower = new Sprite { Group = 0, Index = 1, SharesWith = target };

        Assert.Throws<InvalidOperationException>(() => SpriteScale.Resize(borrower, 4, 4, SpriteRotate.Interpolation.Nearest));
    }
}
