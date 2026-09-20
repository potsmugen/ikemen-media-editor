using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>Quarter turns and mirrors: which pixel lands where, and where the axis goes.</summary>
public class SpriteRotateTests
{
    /// <summary>A 3×2 indexed sprite with six distinct pixels, so a moved one is visible:
    /// 1 2 3
    /// 4 5 6
    /// </summary>
    private static Sprite Indexed3x2(int axisX = 1, int axisY = 1) => new()
    {
        Group = 0, Index = 0, Width = 3, Height = 2, AxisX = axisX, AxisY = axisY,
        Indexed = [1, 2, 3, 4, 5, 6],
    };

    [Fact]
    public void QuarterRightSwapsTheCanvasAndMovesEachPixel()
    {
        var s = Indexed3x2();

        var r = SpriteRotate.Rotate(s, SpriteRotate.QuarterRight, SpriteRotate.Pivot.Sprite);

        Assert.True(r.Changed);
        Assert.Equal(2, s.Width);
        Assert.Equal(3, s.Height);
        // 4 1
        // 5 2
        // 6 3
        Assert.Equal(new byte[] { 4, 1, 5, 2, 6, 3 }, s.Indexed);
    }

    [Fact]
    public void QuarterLeftSwapsTheCanvasTheOtherWay()
    {
        var s = Indexed3x2();

        SpriteRotate.Rotate(s, SpriteRotate.QuarterLeft, SpriteRotate.Pivot.Sprite);

        // 3 6
        // 2 5
        // 1 4
        Assert.Equal(new byte[] { 3, 6, 2, 5, 1, 4 }, s.Indexed);
        Assert.Equal(2, s.Width);
        Assert.Equal(3, s.Height);
    }

    [Fact]
    public void FourQuarterRightsComeBack()
    {
        var s = Indexed3x2();

        for (int i = 0; i < 4; i++)
            SpriteRotate.Rotate(s, SpriteRotate.QuarterRight, SpriteRotate.Pivot.Axis);

        Assert.Equal(3, s.Width);
        Assert.Equal(2, s.Height);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, s.Indexed);
        Assert.Equal(1, s.AxisX);
        Assert.Equal(1, s.AxisY);
    }

    [Fact]
    public void HalfTurnReversesThePixelsAndKeepsTheCanvas()
    {
        var s = Indexed3x2();

        var r = SpriteRotate.Rotate(s, SpriteRotate.Half, SpriteRotate.Pivot.Sprite);

        Assert.Equal(new byte[] { 6, 5, 4, 3, 2, 1 }, s.Indexed);
        Assert.Equal(3, s.Width);
        Assert.Equal(2, s.Height);
        // Same canvas, so turning the sprite in place leaves the axis where it was.
        Assert.Equal(1, s.AxisX);
        Assert.Equal(1, s.AxisY);
        Assert.Equal(1, r.AxisX);
    }

    [Fact]
    public void AxisPivotPutsTheAxisOnWhicheverPixelItLanded()
    {
        var s = Indexed3x2(axisX: 1, axisY: 1);

        SpriteRotate.Rotate(s, SpriteRotate.QuarterRight, SpriteRotate.Pivot.Axis);

        // The pixel that was at (1,1) is now at (h-1-y, x) = (0,1).
        Assert.Equal(0, s.AxisX);
        Assert.Equal(1, s.AxisY);
    }

    [Fact]
    public void SpritePivotShiftsTheAxisByHalfTheSizeDifference()
    {
        var s = new Sprite
        {
            Group = 0, Index = 0, Width = 4, Height = 2, AxisX = 2, AxisY = 1,
            Indexed = [1, 2, 3, 4, 5, 6, 7, 8],
        };

        SpriteRotate.Rotate(s, SpriteRotate.QuarterRight, SpriteRotate.Pivot.Sprite);

        // 2×4 canvas, so the axis moves one left and one down to keep the sprite in place.
        Assert.Equal(2, s.Width);
        Assert.Equal(4, s.Height);
        Assert.Equal(1, s.AxisX);
        Assert.Equal(2, s.AxisY);
    }

    [Fact]
    public void NoneKeepsTheSameAxisCoordinates()
    {
        var s = Indexed3x2(axisX: 1, axisY: 1);

        SpriteRotate.Rotate(s, SpriteRotate.QuarterRight, SpriteRotate.Pivot.None);

        Assert.Equal(1, s.AxisX);
        Assert.Equal(1, s.AxisY);
    }

    [Fact]
    public void OddSizeDifferenceLeavesTheAxisWhereItIs()
    {
        // 4×3 turns into 3×4, so the halves don't land on a whole pixel. This is
        // SpriteCrop.Resize's integer division, so the odd half pixel goes up, not down.
        var s = new Sprite
        {
            Group = 0, Index = 0, Width = 4, Height = 3, AxisX = 2, AxisY = 1,
            Indexed = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12],
        };

        SpriteRotate.Rotate(s, SpriteRotate.QuarterRight, SpriteRotate.Pivot.Sprite);

        Assert.Equal(3, s.Width);
        Assert.Equal(4, s.Height);
        Assert.Equal(2, s.AxisX);
        Assert.Equal(1, s.AxisY);
    }

    [Fact]
    public void FlipHorizontalMirrorsWithoutChangingTheCanvas()
    {
        var s = Indexed3x2();

        var r = SpriteRotate.Flip(s, horizontal: true, vertical: false, SpriteRotate.Pivot.Sprite);

        Assert.Equal(new byte[] { 3, 2, 1, 6, 5, 4 }, s.Indexed);
        Assert.Equal(3, s.Width);
        Assert.Equal(2, s.Height);
        Assert.True(r.Changed);
    }

    [Fact]
    public void FlipVerticalAroundTheAxisMovesItToTheMirroredPixel()
    {
        var s = Indexed3x2(axisX: 0, axisY: 0);

        SpriteRotate.Flip(s, horizontal: false, vertical: true, SpriteRotate.Pivot.Axis);

        Assert.Equal(new byte[] { 4, 5, 6, 1, 2, 3 }, s.Indexed);
        Assert.Equal(0, s.AxisX);
        Assert.Equal(1, s.AxisY);
    }

    [Fact]
    public void TrueColorRotatesByBgraAndStaysTrueColor()
    {
        // Four distinguishable BGRA pixels in a 2×2.
        var s = new Sprite
        {
            Group = 0, Index = 0, Width = 2, Height = 2,
            Direct = [1, 2, 3, 4, 11, 12, 13, 14, 21, 22, 23, 24, 31, 32, 33, 34],
        };

        SpriteRotate.Rotate(s, SpriteRotate.QuarterRight, SpriteRotate.Pivot.Sprite);

        // (x,y) -> (1-y, x): the bottom-left pixel comes to the top-left.
        Assert.Equal(new byte[] { 21, 22, 23, 24, 1, 2, 3, 4, 31, 32, 33, 34, 11, 12, 13, 14 }, s.Direct);
        Assert.Null(s.Indexed);
    }

    [Fact]
    public void QuarterRightLeavesAnIndexedSpriteIndexed()
    {
        var s = Indexed3x2();

        SpriteRotate.Rotate(s, SpriteRotate.QuarterRight, SpriteRotate.Pivot.Sprite);

        Assert.NotNull(s.Indexed);
        Assert.Null(s.Direct);
        Assert.True(s.PixelsDirty);
    }

    [Fact]
    public void NoTurnAndNoFlipChangesNothing()
    {
        var s = Indexed3x2();

        Assert.False(SpriteRotate.Rotate(s, 0, SpriteRotate.Pivot.Sprite).Changed);
        Assert.False(SpriteRotate.Rotate(s, 4, SpriteRotate.Pivot.Sprite).Changed);
        Assert.False(SpriteRotate.Flip(s, false, false, SpriteRotate.Pivot.Sprite).Changed);

        Assert.Equal(3, s.Width);
        Assert.Equal(2, s.Height);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, s.Indexed);
    }

    [Fact]
    public void LinkedSpriteIsRefused()
    {
        var target = Indexed3x2();
        var borrower = new Sprite { Group = 0, Index = 1, SharesWith = target };

        Assert.Throws<InvalidOperationException>(
            () => SpriteRotate.Rotate(borrower, SpriteRotate.QuarterRight, SpriteRotate.Pivot.Sprite));
    }
}
