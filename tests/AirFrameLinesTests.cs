using Air;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>Frame-bound lines (ClsnDefault, Loopstart, Interpolate) follow their frame, as in the text form.</summary>
public class AirFrameLinesTests
{
    private static readonly ClsnBox BoxA = new() { X1 = -1, Y1 = -1, X2 = 1, Y2 = 1 };
    private static readonly ClsnBox BoxB = new() { X1 = -2, Y1 = -2, X2 = 2, Y2 = 2 };

    /// <summary>Four frames; a Clsn2Default declared at 0 (A) and at 2 (B); Loopstart at 2; Interpolate Offset on 2.</summary>
    private static AnimationAction Sample()
    {
        var a = new AnimationAction();
        for (int i = 0; i < 4; i++) a.Elements.Add(new AnimationElement { Group = 0, Index = i, Duration = 5 });
        a.DefaultHurtSegments.Add(new DefaultSegment { At = 0, Boxes = [BoxA] });
        a.DefaultHurtSegments.Add(new DefaultSegment { At = 2, Boxes = [BoxB] });
        a.LoopStart = 2;
        a.InterpolateOffset.Add(2);
        a.RebakeInheritedBoxes();
        return a;
    }

    [Fact]
    public void InsertMovesLaterLinesDown()
    {
        var a = Sample();
        a.Elements.Insert(1, new AnimationElement());
        a.InsertFrameLines(1, 1);
        Assert.Equal(new[] { 0, 3 }, a.DefaultHurtSegments.Select(s => s.At));
        Assert.Equal(3, a.LoopStart);
        Assert.Equal(new[] { 3 }, a.InterpolateOffset);
    }

    [Fact]
    public void RemoveDeletesTheFramesOwnLines()
    {
        var a = Sample();
        a.Elements.RemoveAt(2);
        a.RemoveFrameLines(2);
        a.RebakeInheritedBoxes();
        Assert.Equal(new[] { 0 }, a.DefaultHurtSegments.Select(s => s.At));
        Assert.Equal(0, a.LoopStart);
        Assert.Empty(a.InterpolateOffset);
        Assert.Equal(new[] { BoxA }, a.Elements[2].Hurt); // now inherits the earlier default
    }

    [Fact]
    public void DuplicateCopiesTheLinesAndTakesLoopstart()
    {
        var a = Sample();
        a.Elements.Insert(3, new AnimationElement { Group = 0, Index = 2, Duration = 5 });
        a.InsertFrameLines(3, 1);
        a.CopyFrameLines(2, 3);
        Assert.Equal(new[] { 0, 2, 3 }, a.DefaultHurtSegments.Select(s => s.At));
        Assert.Equal(3, a.LoopStart);
        Assert.Equal(new[] { 2, 3 }, a.InterpolateOffset);
    }

    [Fact]
    public void MoveCarriesLinesAndInheritsAtTheNewSpot()
    {
        var a = Sample();
        (a.Elements[1], a.Elements[2]) = (a.Elements[2], a.Elements[1]);
        a.SwapFrameLines(1, 2);
        a.RebakeInheritedBoxes();
        Assert.Equal(new[] { 0, 1 }, a.DefaultHurtSegments.Select(s => s.At));
        Assert.Equal(1, a.LoopStart);
        Assert.Equal(new[] { 1 }, a.InterpolateOffset);
        Assert.Equal(new[] { BoxB }, a.Elements[2].Hurt); // the old frame 1 now sits under B
    }
}
