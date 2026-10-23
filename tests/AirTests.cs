using Air;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// AIR is text and fully documented, so these check the parser against the
/// examples in Elecbyte's own specification.
/// </summary>
public class AirTests
{
    private const string StandingExample = """
        ; Standing Animation
        [Begin Action 000]
        Clsn2Default: 2
         Clsn2[0] = -10,  0, 10,-79
         Clsn2[1] =  -4,-92,  6,-79
        0,1, 0,0, 7
        0,2, 0,0, 7
        Loopstart
        0,3, 0,0, 7
        0,4, 0,0, 50
        0,5, 0,0, 7
        0,6, 0,0, 7
        0,7, 0,0, 7
        0,8, 0,0, 60
        """;

    [Fact]
    public void FindsEmbeddedActionsInsideAStageDefinition()
    {
        var definition = """
            [Info]
            name = "Bone The Cook"
            [BGdef]
            spr = BoneTheCook.sff
            [BG Flames]
            type = anim
            actionno = 21
            [Begin Action 1]
            0,5, 0,0, 45, , AS256D256
            [Begin Action 21]
            2,0, 0,0, 12, , AS128D256, 1, 1
            [Begin Action 9000]
            9000,1, 0,0, 1
            """;

        var file = AirParser.Parse(definition);

        Assert.Equal([1, 21, 9000], file.Actions.Select(a => a.Number));
        Assert.Equal(3, file.Actions.Sum(a => a.Elements.Count));
        Assert.Equal(BlendMode.Add, file.Actions[0].Elements[0].Blend.Mode);
    }

    [Fact]
    public void DefinitionSectionsEndThePreviousEmbeddedAction()
    {
        var file = AirParser.Parse("""
            [Begin Action 1]
            0,0, 0,0, 3
            ; background section comment
            [BG 0, action layer]
            ; background comment, not an action comment
            12,4, 0,0, 5
            [Begin Action 2]
            0,1, 0,0, 4
            """);

        Assert.Equal([1, 2], file.Actions.Select(a => a.Number));
        Assert.Single(file.Actions[0].Elements);
        Assert.Empty(file.Actions[0].TrailingComments);
        Assert.Single(file.Actions[1].Elements);
    }

    [Fact]
    public void ParsesTheSpecificationsOwnExample()
    {
        var file = AirParser.Parse(StandingExample);
        var action = Assert.Single(file.Actions);

        Assert.Equal(0, action.Number);
        Assert.Equal(8, action.Elements.Count);
        Assert.Equal(2, action.LoopStart);          // Loopstart sat before the third element
        Assert.Equal(152, action.LoopTime);         // the sum the spec works through
        Assert.Equal(2, action.DefaultHurt.Count);

        var first = action.Elements[0];
        Assert.Equal(0, first.Group);
        Assert.Equal(1, first.Index);
        Assert.Equal(7, first.Duration);
    }

    [Fact]
    public void DefaultBoxesApplyToEveryElement()
    {
        var action = AirParser.Parse(StandingExample).Actions[0];
        foreach (var e in action.Elements)
        {
            Assert.Equal(2, e.Hurt.Count);
            Assert.Empty(e.Attack);
            Assert.False(e.OwnHurtBoxes);   // inherited, not written per element
        }
        // The spec writes the box bottom-up (-10, 0, 10, -79); the parser orders
        // corners top-left first, as BoxCornersAreOrdered pins.
        Assert.Equal(new ClsnBox(-10, -79, 10, 0), action.Elements[0].Hurt[0]);
    }

    [Fact]
    public void PerElementBoxesOverrideTheDefault()
    {
        var file = AirParser.Parse("""
            [Begin Action 200]
            Clsn2Default: 1
             Clsn2[0] = -10,0,10,-79
            0,0, 0,0, 5
            Clsn1: 1
             Clsn1[0] = 10,-60,40,-40
            0,1, 0,0, 5
            0,2, 0,0, 5
            """);
        var a = file.Actions[0];

        Assert.Empty(a.Elements[0].Attack);
        Assert.Single(a.Elements[1].Attack);        // only the element it was declared for
        Assert.True(a.Elements[1].OwnAttackBoxes);
        Assert.Empty(a.Elements[2].Attack);
        Assert.All(a.Elements, e => Assert.Single(e.Hurt)); // the default persists throughout
    }

    [Theory]
    [InlineData("H", true, false)]
    [InlineData("V", false, true)]
    [InlineData("VH", true, true)]
    public void ReadsFlipParameters(string flip, bool h, bool v)
    {
        var e = AirParser.Parse($"[Begin Action 1]\n15,1, 0,0, 5, {flip}").Actions[0].Elements[0];
        Assert.Equal(h, e.FlipH);
        Assert.Equal(v, e.FlipV);
    }

    [Theory]
    [InlineData("A", BlendMode.Add, 255, 255)]
    [InlineData("S", BlendMode.Subtract, 255, 255)]
    [InlineData("A1", BlendMode.Add, 255, 128)]
    [InlineData("AS64D192", BlendMode.Add, 64, 192)]
    [InlineData("as128", BlendMode.Add, 128, 0)]          // a bare AS defaults its destination to 0
    [InlineData("SA", BlendMode.SubAdd, 255, 255)]        // Ikemen only
    [InlineData("SAS32D64", BlendMode.SubAdd, 32, 64)]
    [InlineData("SS128D200", BlendMode.Subtract, 128, 200)]
    [InlineData("AS256D256", BlendMode.Add, 256, 256)]      // alpha range is 0-256
    [InlineData("AS300D999", BlendMode.Add, 256, 256)]
    public void ReadsBlendParameters(string text, BlendMode mode, int src, int dst)
    {
        var b = AirParser.ParseBlend(text);
        Assert.Equal(mode, b.Mode);
        Assert.Equal(src, b.Source);
        Assert.Equal(dst, b.Destination);
    }

    [Fact]
    public void BlendWithTheFlipFieldOmitted()
    {
        // The spec's example: "15,4, 0,0, 5, ,A" leaves the flip field empty.
        var e = AirParser.Parse("[Begin Action 1]\n15,4, 0,0, 5, ,A").Actions[0].Elements[0];
        Assert.False(e.FlipH);
        Assert.False(e.FlipV);
        Assert.Equal(BlendMode.Add, e.Blend.Mode);
    }

    [Fact]
    public void MinusOneDurationMeansNoLoop()
    {
        var a = AirParser.Parse("[Begin Action 190]\n0,0, 0,0, 10\n0,1, 0,0, -1").Actions[0];
        Assert.Null(a.LoopTime);
        Assert.True(a.Elements[1].ShowsForever);
    }

    [Fact]
    public void ElementAtWalksTheTimelineAndLoops()
    {
        var a = AirParser.Parse(StandingExample).Actions[0];

        Assert.Equal(1, a.ElementAt(0)!.Index);    // first element, 7 ticks
        Assert.Equal(2, a.ElementAt(7)!.Index);
        Assert.Equal(3, a.ElementAt(20)!.Index);   // three 7-tick frames cover ticks 0..20
        Assert.Equal(4, a.ElementAt(21)!.Index);   // the 50-tick frame starts at tick 21
        Assert.Equal(3, a.ElementAt(152)!.Index);  // wrapped to the Loopstart element
    }

    [Fact]
    public void ElementAtWalksUpToAHoldEndingActionsLastFrame()
    {
        // The old ElementWithin walked from the prefix's total length instead
        // of zero, so every tick before the hold resolved to the first element.
        var a = AirParser.Parse("[Begin Action 1]\n0,0, 0,0, 10\n0,1, 0,0, 5\n0,2, 0,0, -1").Actions[0];

        Assert.Equal(0, a.ElementAt(0)!.Index);
        Assert.Equal(0, a.ElementAt(9)!.Index);
        Assert.Equal(1, a.ElementAt(10)!.Index);   // second element, not a stuck first one
        Assert.Equal(1, a.ElementAt(14)!.Index);
        Assert.Equal(2, a.ElementAt(15)!.Index);   // the hold frame
        Assert.Equal(2, a.ElementAt(999)!.Index);
    }

    [Fact]
    public void FileHeaderIsNotMistakenForTheFirstActionsComment()
    {
        // The exact shape that was misattributing a whole file's header block,
        // plus a decorative separator line, as Action 0's own comment.
        var file = AirParser.Parse("""
            ; KFM's animation data
            ; see docs/air.txt for more information

            ;---------------------------------------------------------------------------
            ; Standing Animation
            [Begin Action 0]
            0,0, 0,0, 5
            """);

        // The header is kept line for line, blank one and decorative rule included;
        // only the action's own name line (the one right above) is not header text.
        Assert.Equal(new[] { "; KFM's animation data", "; see docs/air.txt for more information", "", ";---------------------------------------------------------------------------" }, file.Header);

        var action = file.Actions[0];
        Assert.Single(action.Comments);
        Assert.Equal("Standing Animation", action.Comments[0]);
    }

    [Fact]
    public void OnlyTheCommentDirectlyAboveAnActionBelongsToIt()
    {
        var file = AirParser.Parse("""
            ; not this one, a blank line separates it

            [Begin Action 1]
            0,0, 0,0, 5
            """);
        Assert.Empty(file.Actions[0].Comments);
        Assert.Equal(new[] { "; not this one, a blank line separates it", "" }, file.Header); // it's not inside an action, so it's header text, blank line included
    }

    [Fact]
    public void OnlyTheLineDirectlyAboveTheHeaderIsTheName()
    {
        // Reaching back past the line right above [Begin Action] to pick up an
        // earlier comment is the bug this guards against. That holds whatever the
        // nearer line says, so no line is ever skipped in favor of an older one.
        var file = AirParser.Parse("""
            ; the real name, but not adjacent
            ;----------------------------------
            [Begin Action 1]
            0,0, 0,0, 5
            """);
        Assert.Equal(new[] { "----------------------------------" }, file.Actions[0].Comments); // the nearer line wins
    }

    [Fact]
    public void MultipleAdjacentCommentLinesKeepOnlyTheLastOne()
    {
        var file = AirParser.Parse("""
            ; older note
            ; the real one
            [Begin Action 1]
            0,0, 0,0, 5
            """);
        Assert.Single(file.Actions[0].Comments);
        Assert.Equal("the real one", file.Actions[0].Comments[0]);
    }

    [Fact]
    public void CommentsDoNotBreakParsing()
    {
        var a = AirParser.Parse("""
            ; a file comment
            [Begin Action 7]  ; trailing comment
            0,0, 0,0, 5 ; and here
            ; a whole line
            0,1, 0,0, 5
            """).Actions[0];
        Assert.Equal(7, a.Number);
        Assert.Equal(2, a.Elements.Count);
    }

    [Fact]
    public void ReadsScaleAndAngle()
    {
        var e = AirParser.Parse("[Begin Action 1]\n0,0, 0,0, 5, , , 2.5, 0.5, 90").Actions[0].Elements[0];
        Assert.Equal(2.5f, e.ScaleX);
        Assert.Equal(0.5f, e.ScaleY);
        Assert.Equal(90f, e.Angle);
        Assert.True(e.HasTransform);
    }

    [Fact]
    public void ExplicitDefaultAlphaIsKeptNotCollapsed()
    {
        var typed = new Blend(BlendMode.Add, 255, 255, "AS255D255");
        Assert.Equal("AS255D255", typed.ToString());
        Assert.Equal("AS255D255", typed.DisplayText());
        Assert.Equal("S255D255", new Blend(BlendMode.Subtract, 255, 255, "SS255D255").DisplayText());
        Assert.Equal("A", new Blend(BlendMode.Add, 255, 255).DisplayText());
        Assert.Equal("A1", AirParser.ParseBlend("A1").DisplayText());
    }

    [Fact]
    public void InterpolationMovesTowardTheMarkedNextFrame()
    {
        // Ikemen anim.go: while frame d shows, a mark on d+1 lerps d toward it over d's time.
        var action = AirParser.Parse("""
            [Begin Action 0]
            0,0, 0,0, 10, , AS100D100, 1, 1, 0
            Interpolate Offset
            Interpolate Blend
            Interpolate Angle
            0,1, 10,20, 10, , AS200D50, 1, 1, 90
            """).Actions[0];

        Assert.Equal((0.0, 0.0), (action.StateAt(0).OffsetX, action.StateAt(0).OffsetY));
        var mid = action.StateAt(5);
        Assert.Equal((5.0, 10.0, 45.0), (mid.OffsetX, mid.OffsetY, mid.Angle));
        Assert.Equal((150, 75), (mid.Blend.Source, mid.Blend.Destination));
        Assert.Equal(1.0, mid.ScaleX); // scale isn't marked

        // Frame 1's next is LoopStart (0), which isn't marked: no interpolation.
        var last = action.StateAt(15);
        Assert.Equal((10.0, 20.0), (last.OffsetX, last.OffsetY));
    }

    [Fact]
    public void LerpingAtListsMarkedValues()
    {
        // Scale is marked and counts even though both frames match; angle isn't marked.
        var action = AirParser.Parse("""
            [Begin Action 0]
            0,0, 0,0, 10, , AS100D100, 2, 2, 0
            Interpolate Offset
            Interpolate Scale
            Interpolate Blend
            0,1, 10,20, 10, , AS200D50, 2, 2, 90
            """).Actions[0];

        Assert.Equal(Lerping.Offset | Lerping.Scale | Lerping.Blend, action.LerpingAt(5));
        Assert.Equal(Lerping.None, action.LerpingAt(15)); // next is unmarked LoopStart
    }

    [Fact]
    public void ParsesLoneCrLineEndings()
    {
        var file = AirParser.Parse("[Begin Action 1]\r0,0, 0,0, 5\r0,1, 1,2, 6");

        var action = Assert.Single(file.Actions);
        Assert.Equal(2, action.Elements.Count);
        Assert.Equal((0, 0, 5), (action.Elements[0].Group, action.Elements[0].Index, action.Elements[0].Duration));
        Assert.Equal((0, 1, 6), (action.Elements[1].Group, action.Elements[1].Index, action.Elements[1].Duration));
    }

    [Fact]
    public void ReadsCopyActionAndInterpolationHints()
    {
        var file = AirParser.Parse("""
            [Begin Action 300]
            Interpolate Offset
            0,0, 0,0, 5
            Interpolate Scale
            0,1, 0,0, 5
            [Begin Action 301]
            Copy Action 300
            """);

        Assert.Contains(0, file.Actions[0].InterpolateOffset);
        Assert.Contains(1, file.Actions[0].InterpolateScale);
        Assert.Equal(300, file.Actions[1].CopyAction);
        Assert.Empty(file.Actions[1].Elements);
    }

    [Fact]
    public void DefaultDeclarationSetsTheActionsDefault()
    {
        var a = AirParser.Parse("""
            [Begin Action 1]
            Clsn2Default: 1
             Clsn2[0] = -10,0,10,-79
            0,0, 0,0, 5
            0,1, 0,0, 5
            """).Actions[0];
        Assert.Single(a.DefaultHurt);
        Assert.False(a.Elements[0].OwnHurtBoxes);
        Assert.False(a.Elements[1].OwnHurtBoxes);
    }

    [Fact]
    public void ClsnCountOfZeroClearsTheBoxes()
    {
        var a = AirParser.Parse("""
            [Begin Action 1]
            Clsn2Default: 1
             Clsn2[0] = -10,0,10,-79
            0,0, 0,0, 5
            Clsn2: 0
            0,1, 0,0, 5
            0,2, 0,0, 5
            """).Actions[0];

        Assert.Single(a.Elements[0].Hurt);
        Assert.Empty(a.Elements[1].Hurt);   // explicitly cleared for that frame
        Assert.Single(a.Elements[2].Hurt);  // and back to the default afterwards
    }

    [Fact]
    public void ClsnCountOfZeroSurvivesARoundTrip()
    {
        var original = AirParser.Parse("""
            [Begin Action 1]
            Clsn2Default: 1
             Clsn2[0] = -10,0,10,-79
            0,0, 0,0, 5
            Clsn2: 0
            0,1, 0,0, 5
            0,2, 0,0, 5
            """);
        var b = AirParser.Parse(AirWriter.Write(original)).Actions[0];

        Assert.True(b.Elements[1].OwnHurtBoxes);
        Assert.Empty(b.Elements[1].Hurt);   // still "no boxes", not the default
        Assert.Single(b.Elements[2].Hurt);
    }

    [Fact]
    public void MultipleDefaultDeclarationsEachGovernTheirOwnStretch()
    {
        // Ikemen/MUGEN lets an action redeclare Clsn2Default partway through —
        // different hurtboxes for a stand vs. crouch stretch of one action,
        // say. Each redeclaration is its own segment (DefaultSegment), not a
        // single overwritten field.
        var a = AirParser.Parse("""
            [Begin Action 1]
            Clsn2Default: 1
             Clsn2[0] = -10,0,10,-79
            0,0, 0,0, 5
            0,1, 0,0, 5
            Clsn2Default: 1
             Clsn2[0] = -20,0,20,-79
            0,2, 0,0, 5
            0,3, 0,0, 5
            """).Actions[0];

        Assert.Equal(new ClsnBox(-10, -79, 10, 0), a.Elements[0].Hurt[0]);
        Assert.Equal(new ClsnBox(-10, -79, 10, 0), a.Elements[1].Hurt[0]);
        Assert.Equal(new ClsnBox(-20, -79, 20, 0), a.Elements[2].Hurt[0]);
        Assert.Equal(new ClsnBox(-20, -79, 20, 0), a.Elements[3].Hurt[0]);
        Assert.All(a.Elements, e => Assert.False(e.OwnHurtBoxes));
    }

    [Fact]
    public void MultiSegmentDefaultsRoundTripThroughTheWriter()
    {
        // The bug this guards against: a single flat DefaultHurt field could
        // only ever remember the LAST Clsn2Default in the file, so re-saving
        // a multi-segment action without touching anything used to silently
        // reassign the earlier frames to the later segment's boxes.
        var original = AirParser.Parse("""
            [Begin Action 1]
            Clsn2Default: 1
             Clsn2[0] = -10,0,10,-79
            0,0, 0,0, 5
            0,1, 0,0, 5
            Clsn2Default: 1
             Clsn2[0] = -20,0,20,-79
            0,2, 0,0, 5
            0,3, 0,0, 5
            """);
        var reparsed = AirParser.Parse(AirWriter.Write(original));
        var (a, b) = (original.Actions[0], reparsed.Actions[0]);

        for (int i = 0; i < a.Elements.Count; i++)
            Assert.Equal(a.Elements[i].Hurt, b.Elements[i].Hurt);
    }

    [Fact]
    public void MidActionDefaultResetToZeroRoundTrips()
    {
        // An explicit "Clsn2Default: 0" partway through means something
        // different from no declaration at all — it resets a still-inheriting
        // frame back to no boxes. Dropping it on write (the old zero-count
        // skip) would silently leave the earlier segment's boxes in force on
        // the next load.
        var original = AirParser.Parse("""
            [Begin Action 1]
            Clsn2Default: 1
             Clsn2[0] = -10,0,10,-79
            0,0, 0,0, 5
            Clsn2Default: 0
            0,1, 0,0, 5
            """);
        var reparsed = AirParser.Parse(AirWriter.Write(original));
        var b = reparsed.Actions[0];

        Assert.Single(b.Elements[0].Hurt);
        Assert.Empty(b.Elements[1].Hurt);          // the mid-action reset survives the round trip
        Assert.False(b.Elements[1].OwnHurtBoxes);  // still inherited, just from an empty segment
    }

    [Fact]
    public void TrailingCommentsSurviveTextRoundTrip()
    {
        var original = AirParser.Parse("[Begin Action 1]\n0,0, 0,0, 5\n; trailing");
        var reparsed = AirParser.Parse(AirWriter.Write(original));

        Assert.Equal(new[] { " trailing" }, reparsed.Actions[0].TrailingComments);
    }

    [Fact]
    public void SegmentAtFindsTheGoverningSegment()
    {
        var a = AirParser.Parse("""
            [Begin Action 1]
            Clsn2Default: 1
             Clsn2[0] = -10,0,10,-79
            0,0, 0,0, 5
            0,1, 0,0, 5
            Clsn2Default: 1
             Clsn2[0] = -20,0,20,-79
            0,2, 0,0, 5
            """).Actions[0];

        Assert.Equal(0, a.HurtSegmentAt(0)!.At);
        Assert.Equal(0, a.HurtSegmentAt(1)!.At);
        Assert.Equal(2, a.HurtSegmentAt(2)!.At);
        Assert.Null(a.AttackSegmentAt(0)); // no Clsn1Default anywhere in this action
    }

    [Fact]
    public void BoxCornersAreOrdered()
    {
        // The spec's own example writes the box bottom-up.
        var box = AirParser.Parse("[Begin Action 1]\nClsn2: 1\n Clsn2[0] = -10, 0, 10, -79\n0,0, 0,0, 5")
            .Actions[0].Elements[0].Hurt[0];
        Assert.Equal(-79, box.Y1);
        Assert.Equal(0, box.Y2);
    }

    [Fact]
    public void NormalizedOrdersTheCorners()
    {
        // The shape box editing produces: a grip dragged across the box's own
        // edge commits inverted corners (CommitBoxes/ApplyOwnState build the
        // model box straight from the overlay). Normalizing must match what
        // the parser produces on load for the same box.
        var inverted = new ClsnBox(10, 0, -10, -79).Normalized();
        Assert.Equal(new ClsnBox(-10, -79, 10, 0), inverted);
        Assert.Equal((-10, -79, 10, 0), (inverted.X1, inverted.Y1, inverted.X2, inverted.Y2));
    }

    [Fact]
    public void LoopStartPastTheEndIsIgnored()
    {
        var a = AirParser.Parse("[Begin Action 1]\n0,0, 0,0, 5\nLoopstart").Actions[0];
        Assert.Equal(0, a.LoopStart);
    }

    [Fact]
    public void WrapTickAgreesWithElementAt()
    {
        // The standing example loops from element index 2 (7+7 past the start).
        // Wherever ElementAt's element begins, the playhead must sit — the old
        // playhead maths modded over the whole length and landed at tick 0 on
        // every wrap from a Loopstart past the beginning, while the frame
        // jumped to the Loopstart element.
        var a = AirParser.Parse(StandingExample).Actions[0];
        Assert.Equal(2, a.LoopStart);

        int total = a.Elements.Sum(e => e.Duration); // 152
        for (int tick = 0; tick < total * 2 + 5; tick++)
        {
            var e = a.ElementAt(tick)!;
            int head = a.Elements.Take(a.LoopStart).Sum(x => x.Duration);
            int at = (int)a.Elements.TakeWhile(x => x != e).Sum(x => x.Duration);
            Assert.True(at <= a.WrapTick(tick) && a.WrapTick(tick) < at + Math.Max(e.Duration, 1),
                $"tick {tick}: element {e.Index} starts at {at}, WrapTick gave {a.WrapTick(tick)}");
        }
    }

    [Fact]
    public void RoundTripsThroughTheWriter()
    {
        var original = AirParser.Parse(StandingExample);
        var reparsed = AirParser.Parse(AirWriter.Write(original));

        Assert.Equal(original.Actions.Count, reparsed.Actions.Count);
        var (a, b) = (original.Actions[0], reparsed.Actions[0]);
        Assert.Equal(a.Number, b.Number);
        Assert.Equal(a.LoopStart, b.LoopStart);
        Assert.Equal(a.LoopTime, b.LoopTime);
        Assert.Equal(a.DefaultHurt.Count, b.DefaultHurt.Count);
        for (int i = 0; i < a.Elements.Count; i++)
        {
            Assert.Equal(a.Elements[i].Group, b.Elements[i].Group);
            Assert.Equal(a.Elements[i].Index, b.Elements[i].Index);
            Assert.Equal(a.Elements[i].Duration, b.Elements[i].Duration);
            Assert.Equal(a.Elements[i].Hurt.Count, b.Elements[i].Hurt.Count);
        }
    }

    [Fact]
    public void OwnBoxesRoundTripSeparatelyFromTheDefault()
    {
        var original = AirParser.Parse("""
            [Begin Action 1]
            Clsn2Default: 1
             Clsn2[0] = -10,0,10,-79
            0,0, 0,0, 5
            Clsn2: 2
             Clsn2[0] = -5,0,5,-40
             Clsn2[1] = -5,-40,5,-79
            0,1, 0,0, 5
            0,2, 0,0, 5
            """);
        var reparsed = AirParser.Parse(AirWriter.Write(original));
        var b = reparsed.Actions[0];

        Assert.False(b.Elements[0].OwnHurtBoxes);
        Assert.Single(b.Elements[0].Hurt);
        Assert.True(b.Elements[1].OwnHurtBoxes);
        Assert.Equal(2, b.Elements[1].Hurt.Count);
        Assert.False(b.Elements[2].OwnHurtBoxes); // back to the default after the override
    }

    [Fact]
    public void CustomOverridesWinOverBuiltIn()
    {
        ReservedActions.Custom = new Dictionary<int, string> { [0] = "My Idle" };
        try
        {
            Assert.Equal("My Idle", ReservedActions.Name(0));
        }
        finally
        {
            ReservedActions.Custom = new Dictionary<int, string>(); // don't leak into other tests
        }
    }

    [Fact]
    public void ReservedNumbersAreNamed()
    {
        Assert.Equal("Standing", ReservedActions.Name(0));
        Assert.Equal("Walking Forward", ReservedActions.Name(20));
        Assert.Null(ReservedActions.Name(950)); // no generic guess for an unnamed number
    }

    [Fact]
    public void ClsnIndexesAreIgnoredButCounted()
    {
        var air = AirParser.Parse("""
            [Begin Action 0]
            Clsn2: 2
             Clsn2[1] = -54, -32, 40, 0
             Clsn2[1] = -5, -3, 4, 0
            1100,1, 0,0, 2
            [Begin Action 1]
            Clsn2: 1
             Clsn2[0] = 1, 2, 3, 4
            0,0, 0,0, 1
            """);
        Assert.Equal(2, air.Actions[0].Elements[0].Hurt.Count); // both boxes kept, in file order
        Assert.Equal(-54, air.Actions[0].Elements[0].Hurt[0].X1);
        Assert.Equal(1, air.Actions[0].MisnumberedClsnLines); // only the first [1] is off
        Assert.Equal(0, air.Actions[1].MisnumberedClsnLines);
        Assert.Contains("Clsn2[0] = -54, -32, 40, 0", AirWriter.WriteAction(air.Actions[0])); // renumbered on write
    }

    [Fact]
    public void ClsnBoxLinesAreAllReadWhateverTheCount()
    {
        var air = AirParser.Parse("""
            [Begin Action 0]
            Clsn2: 1
             Clsn2[0] = 1, 2, 3, 4
             Clsn2[1] = 5, 6, 7, 8
            0,0, 0,0, 1
            Clsn2: 3
             Clsn2[0] = 1, 1, 2, 2
            0,1, 0,0, 1
            """);
        var a = air.Actions[0];
        Assert.Equal(2, a.Elements[0].Hurt.Count);  // both lines, though it says 1
        Assert.Single(a.Elements[1].Hurt);          // one line, though it says 3
        Assert.Equal(2, a.MismatchedClsnCounts);
        Assert.Contains("Clsn2: 2", AirWriter.WriteAction(a));
    }

    [Fact]
    public void CommentsStayWithTheirFrame()
    {
        const string text = """
            ; header
            ; ------

            ; more header
            ; Stand
            [Begin Action 0] ; idle
            Clsn2: 1 ; body
             Clsn2[0] = 1, 2, 3, 4 ; torso
            0,0, 0,0, 5 ; first
            ; between
            0,1, 0,0, 5
            ; tail

            ; Walk
            [Begin Action 20]
            20,0, 0,0, 5
            """;
        var air = AirParser.Parse(text);
        var stand = air.Actions[0];
        Assert.Equal(new[] { "Stand" }, stand.Comments);
        Assert.Equal(new[] { "; header", "; ------", "", "; more header" }, air.Header); // line for line, blank one included
        Assert.Equal(" idle", stand.HeaderComment);                                    // as written after the ;
        Assert.Equal(new[] { " body", " torso" }, stand.Elements[0].Comments);         // off the Clsn lines, onto their own
        Assert.Equal(" first", stand.Elements[0].EndComment);
        Assert.Equal(new[] { " between" }, stand.Elements[1].Comments);
        Assert.Equal(new[] { " tail" }, stand.TrailingComments);
        Assert.Equal(2, stand.MovedComments);                                          // only the two Clsn line ones
        Assert.Equal(new[] { "Walk" }, air.Actions[1].Comments);  // the name, not the previous action's comment
        Assert.Empty(air.Actions[1].TrailingComments);

        var again = AirParser.Parse(AirWriter.Write(air));
        var stand2 = again.Actions[0];
        Assert.Equal(" idle", stand2.HeaderComment);
        Assert.Equal(stand.Elements[0].Comments, stand2.Elements[0].Comments);
        Assert.StartsWith("; body", AirWriter.WriteAction(stand2).Split('\n')[2]); // above the Clsn lines
        Assert.Equal(" first", stand2.Elements[0].EndComment);
        Assert.Equal(new[] { " between" }, stand2.Elements[1].Comments);
        Assert.Equal(new[] { " tail" }, stand2.TrailingComments);
        Assert.Equal(0, stand2.MovedComments);  // stable after one save
        Assert.Equal(new[] { "Walk" }, again.Actions[1].Comments);
        Assert.Equal(air.Header, again.Header);
    }

    [Fact]
    public void CommentedOutLinesRoundTripInPlace()
    {
        const string text = """
            ; Jab
            [Begin Action 200] ; jab
            Clsn2Default: 1
             Clsn2[0] = -10, -80, 10, 0
            200,0, 0,0, 3
            ;200,1, 0,0, 3
            ;Clsn1[1] = 0, 0, 1, 1
            Clsn1: 1
             Clsn1[0] = 10, -70, 40, -60
            200,2, 0,0, 4 ; hit
            ;200,3, 0,0, 4
            ; recovery
            """;
        var air = AirParser.Parse(text);
        var a = air.Actions[0];
        Assert.Equal(0, a.MovedComments);
        Assert.Equal(new[] { "200,1, 0,0, 3", "Clsn1[1] = 0, 0, 1, 1" }, a.Elements[1].Comments); // above the frame's Clsn lines
        Assert.Equal(text.Replace("\r\n", "\n") + "\n\n", AirWriter.Write(air).Replace("\r\n", "\n"));
    }

    [Fact]
    public void CommentsMoveAndGoWithTheirFrame()
    {
        var a = AirParser.Parse("""
            [Begin Action 1]
            0,0, 0,0, 5
            ;0,9, 0,0, 5
            0,1, 0,0, 5 ; second
            0,2, 0,0, 5
            """).Actions[0];

        // Moving the commented frame's owner to the end takes the comments along.
        var second = a.Elements[1];
        a.Elements.RemoveAt(1);
        a.Elements.Add(second);
        Assert.Equal("""
            [Begin Action 1]
            0,0, 0,0, 5
            0,2, 0,0, 5
            ;0,9, 0,0, 5
            0,1, 0,0, 5 ; second
            """.Replace("\r\n", "\n") + "\n\n", AirWriter.WriteAction(a).Replace("\r\n", "\n"));

        // Removing it removes them.
        a.Elements.RemoveAt(2);
        Assert.DoesNotContain(";", AirWriter.WriteAction(a));
    }

    [Fact]
    public void HeaderCommentsAreWrittenBackAsTheyWere()
    {
        const string text = """
            ; _______________________________________
            ;| Edmond Honda by Phantom.of.the.Server |
            ; ¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯¯
            ;
            [Begin Action 0]
            0,0, 0,0, 5
            """;
        var air = AirParser.Parse(text);
        Assert.Equal([""], air.Actions[0].Comments); // the empty ";" right above the header is an empty name
        Assert.Equal(3, air.Header.Count);            // the ¯ line included; the empty one is the action's name now
        var written = AirWriter.Write(air);
        Assert.StartsWith(string.Join(Environment.NewLine, text.Split('\n').Take(5).Select(l => l.TrimEnd('\r'))), written); // exactly as before
    }

    [Fact]
    public void AnEmptySemicolonAboveTheHeaderIsAnEmptyActionName()
    {
        // A bare ";" between an action's last frame and the next header is the
        // next action's (empty) name, so saving leaves it where the file had it
        // instead of dropping it below the blank line as a stray comment.
        const string text = """
            [Begin Action 4999]
            5000,0, 0,0, 4

            ;
            [Begin Action 5000]
            Clsn2Default: 2
             Clsn2[0] = -22,-80,22,0
             Clsn2[1] = -6,-91,16,-71
            5000,0, 0,0, 4
            5000,10, 0,0, 4

            """;

        var air = AirParser.Parse(text);
        Assert.Equal([""], air.Actions[1].Comments);              // the name, not the previous action's comment
        Assert.Empty(air.Actions[0].TrailingComments);
        Assert.Equal(0, air.Actions[1].MovedComments);            // nothing moved, so nothing to warn about
        var written = AirWriter.Write(air);
        var expected = string.Join(Environment.NewLine,
            "[Begin Action 4999]", "5000,0, 0,0, 4", "", ";", "[Begin Action 5000]");
        Assert.Contains(expected, written); // the ";" still sits above the header, not below the blank line
    }

    [Fact]
    public void OldEncodingsAreGuessedAndSavedAsUtf8()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        // Windows-1252 "¯" (0xAF) is also a valid Shift-JIS half-width katakana byte; it must still read as 1252.
        var w1252 = AirFile.Decode(System.Text.Encoding.GetEncoding(1252).GetBytes("; \u00AF\u00AF caf\u00E9"));
        Assert.Equal((AirEncoding.Legacy, "Windows-1252"), (w1252.Encoding, w1252.LegacyName));
        Assert.Equal("; \u00AF\u00AF caf\u00E9", w1252.Text);

        var sjis = AirFile.Decode(System.Text.Encoding.GetEncoding(932).GetBytes("; \u3042\u3044\u3046 \u6F22\u5B57"));
        Assert.Equal("Shift-JIS", sjis.LegacyName);
        Assert.Equal("; \u3042\u3044\u3046 \u6F22\u5B57", sjis.Text);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(sjis.Text), AirFile.Encode(sjis.Text, sjis.Encoding)); // saved as UTF-8

        byte[] withBom = [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("; hi")];
        Assert.Equal(AirEncoding.Utf8Bom, AirFile.Decode(withBom).Encoding);
        Assert.Equal(withBom, AirFile.Encode("; hi", AirEncoding.Utf8Bom));
    }

    [Fact]
    public void FramesWithBothADefaultAndOwnClsnAreFound()
    {
        var action = AirParser.Parse("""
            [Begin Action 0]
            Clsn2Default: 1
             Clsn2[0] = -10, -60, 10, 0
            Clsn2: 1
             Clsn2[0] = -20, -70, 20, 0
            0,0, 0,0, 5
            0,1, 0,0, 5
            """).Actions[0];
        Assert.Equal([0], action.FramesWithDefaultAndOwnClsn());
        Assert.False(action.Elements[1].OwnHurtBoxes); // the next frame just inherits
    }
}
