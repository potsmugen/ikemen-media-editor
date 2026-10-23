using System.Text;
namespace Air;

/// <summary>
/// A collision box, in the sprite's own coordinates: the origin is the sprite's
/// axis and y grows downward, so a box above the axis has negative y.
/// </summary>
public struct ClsnBox
{
    public int X1, Y1, X2, Y2;

    public ClsnBox(int x1, int y1, int x2, int y2) => (X1, Y1, X2, Y2) = (x1, y1, x2, y2);

    public int Left => Math.Min(X1, X2);
    public int Top => Math.Min(Y1, Y2);
    public int Width => Math.Abs(X2 - X1);
    public int Height => Math.Abs(Y2 - Y1);

    /// <summary>A copy with corners ordered left, top, right, bottom — the form
    /// the parser produces on load. Box editing can invert them (a grip dragged
    /// across the box's own edge commits long before the drag ends), so
    /// everything that writes a box into the model normalizes first.</summary>
    public readonly ClsnBox Normalized() =>
        new(Math.Min(X1, X2), Math.Min(Y1, Y2), Math.Max(X1, X2), Math.Max(Y1, Y2));

    /// <summary>The four corners as a Clsn box line writes them, comma and space separated.</summary>
    public override readonly string ToString() => $"{X1}, {Y1}, {X2}, {Y2}";
}

/// <summary>Which of the two box kinds a collision box is.</summary>
public enum ClsnKind
{
    /// <summary>Clsn1: the attacking box.</summary>
    Attack,

    /// <summary>Clsn2: the box that can be hit.</summary>
    Hurt,
}

/// <summary>
/// How an element is composited, the seventh field. The original text is kept
/// so a file round-trips exactly, since the format accepts several spellings of
/// the same thing and rewriting them all canonically would churn every file.
/// </summary>
/// <summary>An element's drawn values at one tick, after interpolation (AnimationAction.StateAt).</summary>
public readonly record struct DrawnState(double OffsetX, double OffsetY, double ScaleX, double ScaleY, double Angle, Blend Blend);

/// <summary>Which values interpolate at a tick (AnimationAction.LerpingAt).</summary>
[Flags]
public enum Lerping { None = 0, Offset = 1, Scale = 2, Angle = 4, Blend = 8 }

public readonly record struct Blend(BlendMode Mode, int Source, int Destination, string Text = "")
{
    public static readonly Blend None = new(BlendMode.None, 255, 0);

    /// <summary>Alpha written out as numbers (e.g. "AS255D255"), so it's kept even when it equals the default.</summary>
    public bool HasExplicitAlpha => Mode != BlendMode.None && Text.Any(char.IsAsciiDigit) && !Text.Trim().Equals("A1", StringComparison.OrdinalIgnoreCase);

    /// <summary>Renders the parameter the way an .air file writes it.</summary>
    public override string ToString()
    {
        if (Text.Length > 0) return Text;
        return Mode switch
        {
            BlendMode.None => "",
            BlendMode.Subtract => Source == 255 && Destination == 255 ? "S" : $"SS{Source}D{Destination}",
            BlendMode.SubAdd => Source == 255 && Destination == 255 ? "SA" : $"SAS{Source}D{Destination}",
            _ when Source == 255 && Destination == 255 => "A",
            _ when Source == 255 && Destination == 128 => "A1",
            _ => $"AS{Source}D{Destination}",
        };
    }

    /// <summary>Compact form for UI display only — same info as ToString() but
    /// without doubling the mode letter for the alpha variants (e.g. "S128D128",
    /// not "SS128D128"). Never used for file output; ToString() owns that.</summary>
    public string DisplayText() => HasExplicitAlpha ? Mode switch
    {
        BlendMode.Subtract => $"S{Source}D{Destination}",
        BlendMode.SubAdd => $"SA{Source}D{Destination}",
        BlendMode.Add => $"AS{Source}D{Destination}",
        _ => "",
    } : Mode switch
    {
        BlendMode.None => "",
        BlendMode.Subtract => Source == 255 && Destination == 255 ? "S" : $"S{Source}D{Destination}",
        BlendMode.SubAdd => Source == 255 && Destination == 255 ? "SA" : $"SA{Source}D{Destination}",
        _ when Source == 255 && Destination == 255 => "A",
        _ when Source == 255 && Destination == 128 => "A1",
        _ => $"AS{Source}D{Destination}",
    };
}

public enum BlendMode
{
    None,

    /// <summary>"A", "A1", or "AS___D___".</summary>
    Add,

    /// <summary>"S", or Ikemen's "SS___D___".</summary>
    Subtract,

    /// <summary>Ikemen's "SA" and "SAS___D___"; not in MUGEN.</summary>
    SubAdd,
}

/// <summary>
/// One frame of an action: which sprite to show, where, and for how long.
/// </summary>
public sealed class AnimationElement
{
    /// <summary>The sprite's group and image number in the .sff.</summary>
    public int Group;
    public int Index;

    /// <summary>Offset from the sprite's own axis, in pixels.</summary>
    public int OffsetX;
    public int OffsetY;

    /// <summary>
    /// How long to show this element, in game ticks; 60 ticks is a second at
    /// normal speed. -1 means show it indefinitely, and is only valid on the
    /// last element of an action.
    /// </summary>
    public int Duration;

    public bool FlipH;
    public bool FlipV;
    public Blend Blend = Blend.None;

    // Fields 8, 9 and 10. Ikemen extensions; MUGEN 1.1 reads scale but not angle,
    // and treats a blank as zero where Ikemen treats it as no change.
    public float ScaleX = 1;
    public float ScaleY = 1;
    public float Angle;

    public bool HasTransform => ScaleX != 1 || ScaleY != 1 || Angle != 0;

    /// <summary>Attack boxes in force for this element.</summary>
    public List<ClsnBox> Attack = [];

    /// <summary>Hurt boxes in force for this element.</summary>
    public List<ClsnBox> Hurt = [];

    /// <summary>True when the boxes were written for this element rather than inherited.</summary>
    public bool OwnAttackBoxes;
    public bool OwnHurtBoxes;

    public bool ShowsForever => Duration < 0;

    /// <summary>-1 as group or number is the normal way to show no sprite; never a missing one.</summary>
    public bool NoSprite => Group == -1 || Index == -1;

    public override string ToString() => $"{Group},{Index} {Duration}t";
}

/// <summary>
/// One Clsn1Default/Clsn2Default declaration: the boxes it set, and the
/// element index it governs from (inclusive) — the same "governs the very
/// next element, and everything after until superseded" rule AirParser
/// already documents for a bare Default line. An action can redeclare its
/// default partway through (different hurtboxes for a stand vs. crouch
/// stretch of one action, say); each redeclaration is its own segment here,
/// in file order, rather than overwriting the last — a single flat box list
/// could only ever represent the final one, silently reassigning every
/// earlier frame to it on the next save.
/// </summary>
public sealed class DefaultSegment
{
    public int At;
    public List<ClsnBox> Boxes = [];
}

/// <summary>A numbered sequence of elements, which the engine calls an action.</summary>
public sealed class AnimationAction
{
    public int Number;
    public List<AnimationElement> Elements = [];

    /// <summary>
    /// Index of the element the animation loops back to, from the Loopstart
    /// line. Zero when absent, which means loop from the beginning.
    /// </summary>
    public int LoopStart;

    // Clsn lint found when parsed, reported by the editor; saving writes the boxes as loaded, which fixes both.
    /// <summary>Box lines whose [n] didn't match their position (or had none).</summary>
    public int MisnumberedClsnLines;
    /// <summary>Declarations whose count didn't match the box lines under them (all lines are read).</summary>
    public int MismatchedClsnCounts;

    public bool HasClsnLint => MisnumberedClsnLines > 0 || MismatchedClsnCounts > 0;

    /// <summary>Comments inside the action that have no place of their own in the model (on a frame,
    /// Clsn or header line, or on their own line), in file order; written after the last frame.</summary>
    public List<string> TrailingComments = [];
    /// <summary>How many of those weren't already after the last frame, so saving moves them.</summary>
    public int MovedComments;

    /// <summary>Saving would write something the user didn't edit differently from the file.</summary>
    public bool ChangesOnSave => HasClsnLint || MovedComments > 0;

    /// <summary>
    /// The action's Clsn1Default / Clsn2Default segments, in file order (see
    /// DefaultSegment). A frame with OwnAttackBoxes or OwnHurtBoxes false
    /// uses whichever segment governs its own element index — AttackSegmentAt
    /// / HurtSegmentAt find it — instead of its own list. Editing a segment
    /// directly (rather than the frame going own) only makes sense from the
    /// one frame sitting at that segment's own At index; enforced by the
    /// editor's UI, not by this type.
    /// </summary>
    public List<DefaultSegment> DefaultAttackSegments = [];
    public List<DefaultSegment> DefaultHurtSegments = [];

    /// <summary>The segment governing a given element index: the last one whose
    /// At is at or before it, or null before any segment has started yet.</summary>
    /// <summary>Frames with boxes of their own of a kind while also declaring that kind's default
    /// (a Clsn2Default: and a Clsn2: above one frame). Invalid for the editor; kept as loaded.</summary>
    public List<int> FramesWithDefaultAndOwnClsn() => Enumerable.Range(0, Elements.Count).Where(i =>
        (Elements[i].OwnHurtBoxes && DefaultHurtSegments.Any(s => s.At == i))
        || (Elements[i].OwnAttackBoxes && DefaultAttackSegments.Any(s => s.At == i))).ToList();

    public DefaultSegment? AttackSegmentAt(int elementIndex) => SegmentAt(DefaultAttackSegments, elementIndex);
    public DefaultSegment? HurtSegmentAt(int elementIndex) => SegmentAt(DefaultHurtSegments, elementIndex);

    private static DefaultSegment? SegmentAt(List<DefaultSegment> segments, int elementIndex)
    {
        DefaultSegment? found = null;
        foreach (var s in segments)
            if (s.At <= elementIndex) found = s; else break; // segments are kept in At order
        return found;
    }

    /// <summary>
    /// Convenience for the common case — the default currently in force at
    /// the end of the action, i.e. the last segment's boxes, or empty if
    /// there isn't one. Right for appending a frame at the very end; anything
    /// position-sensitive (inserting mid-action) should go through
    /// AttackSegmentAt/HurtSegmentAt instead, since a mid-action insert isn't
    /// necessarily governed by the LAST segment.
    /// </summary>
    public List<ClsnBox> DefaultAttack => DefaultAttackSegments.Count > 0 ? DefaultAttackSegments[^1].Boxes : [];
    public List<ClsnBox> DefaultHurt => DefaultHurtSegments.Count > 0 ? DefaultHurtSegments[^1].Boxes : [];

    /// <summary>
    /// Shifts every segment at or after `at` by `delta` — call after
    /// inserting or removing elements so existing segments keep governing the
    /// same semantic frames, the same reason callers already adjust
    /// LoopStart on a remove. Frame edits should use the *FrameLines methods
    /// below, which also move Loopstart and the Interpolate markers.
    /// </summary>
    public void ShiftDefaultSegments(int at, int delta)
    {
        foreach (var s in DefaultAttackSegments) if (s.At >= at) s.At = Math.Max(0, s.At + delta);
        foreach (var s in DefaultHurtSegments) if (s.At >= at) s.At = Math.Max(0, s.At + delta);
    }

    // ---- frame-bound lines ------------------------------------------------
    // As in the text form, the lines above a frame belong to it: a ClsnDefault
    // it declares, its Interpolate markers and Loopstart. They follow the frame
    // on insert/remove/duplicate/move. LoopStart 0 is "no line" and never moves.

    private IEnumerable<SortedSet<int>> InterpolateSets => [InterpolateOffset, InterpolateScale, InterpolateAngle, InterpolateBlend];

    private static void ShiftSet(SortedSet<int> set, int at, int delta)
    {
        var moved = set.Where(i => i >= at).ToList();
        foreach (var i in moved) set.Remove(i);
        foreach (var i in moved) set.Add(i + delta);
    }

    /// <summary>`count` new frames were inserted at `at`: everything from there on moves down.</summary>
    public void InsertFrameLines(int at, int count)
    {
        ShiftDefaultSegments(at, count);
        if (LoopStart > 0 && LoopStart >= at) LoopStart += count;
        foreach (var set in InterpolateSets) ShiftSet(set, at, count);
    }

    /// <summary>Frame `at` was removed: its own lines go with it, later ones move up.</summary>
    public void RemoveFrameLines(int at)
    {
        DefaultAttackSegments.RemoveAll(s => s.At == at);
        DefaultHurtSegments.RemoveAll(s => s.At == at);
        ShiftDefaultSegments(at + 1, -1);
        if (LoopStart == at) LoopStart = 0;
        else if (LoopStart > at) LoopStart--;
        foreach (var set in InterpolateSets) { set.Remove(at); ShiftSet(set, at + 1, -1); }
    }

    /// <summary>Frame `to` is a fresh copy of `from` (already inserted): it gets `from`'s lines too, Loopstart included.</summary>
    public void CopyFrameLines(int from, int to)
    {
        static void Copy(List<DefaultSegment> segs, int from, int to)
        {
            if (segs.Find(s => s.At == from) is not { } s) return;
            segs.Add(new DefaultSegment { At = to, Boxes = [.. s.Boxes] });
            segs.Sort((a, b) => a.At.CompareTo(b.At));
        }
        Copy(DefaultAttackSegments, from, to);
        Copy(DefaultHurtSegments, from, to);
        if (LoopStart > 0 && LoopStart == from) LoopStart = to;
        foreach (var set in InterpolateSets) if (set.Contains(from)) set.Add(to);
    }

    /// <summary>Frames `a` and `b` swapped places: their lines swap with them.</summary>
    public void SwapFrameLines(int a, int b)
    {
        static void Swap(List<DefaultSegment> segs, int a, int b)
        {
            foreach (var s in segs) s.At = s.At == a ? b : s.At == b ? a : s.At;
            segs.Sort((x, y) => x.At.CompareTo(y.At));
        }
        Swap(DefaultAttackSegments, a, b);
        Swap(DefaultHurtSegments, a, b);
        if (LoopStart > 0) LoopStart = LoopStart == a ? b : LoopStart == b ? a : LoopStart;
        foreach (var set in InterpolateSets)
        {
            bool hasA = set.Remove(a), hasB = set.Remove(b);
            if (hasA) set.Add(b);
            if (hasB) set.Add(a);
        }
    }

    /// <summary>Frames without boxes of their own hold a copy of the default in force; refreshes those copies after the defaults moved.</summary>
    public void RebakeInheritedBoxes()
    {
        for (int i = 0; i < Elements.Count; i++)
        {
            var el = Elements[i];
            if (!el.OwnAttackBoxes) el.Attack = [.. AttackSegmentAt(i)?.Boxes ?? []];
            if (!el.OwnHurtBoxes) el.Hurt = [.. HurtSegmentAt(i)?.Boxes ?? []];
        }
    }

    /// <summary>
    /// The action's own one-line comment/name, if any. Only the single comment
    /// line sitting directly above [Begin Action] (no blank line in between)
    /// counts as the action's; anything further back — a file's header block,
    /// a "-----" section rule — is a different kind of comment and does not
    /// belong to any particular action. A List rather than a plain string
    /// because that is what AirWriter emits either way; in practice it holds
    /// zero or one entries.
    /// </summary>
    public List<string> Comments = [];

    /// <summary>
    /// Set by Ikemen's "Copy Action n", which makes this action a copy of
    /// another rather than listing frames of its own. -1 means the number could
    /// not be read, which the engine treats as a broken action.
    /// </summary>
    public int? CopyAction;

    /// <summary>
    /// Frame indices carrying Ikemen's interpolation hints, which smooth a value
    /// between this frame and the next. Not in MUGEN.
    /// </summary>
    public SortedSet<int> InterpolateOffset = [];
    public SortedSet<int> InterpolateScale = [];
    public SortedSet<int> InterpolateAngle = [];
    public SortedSet<int> InterpolateBlend = [];

    /// <summary>
    /// Total length in ticks, or null when the last element runs forever, which
    /// the format calls "no loop".
    /// </summary>
    public int? LoopTime =>
        Elements.Any(e => e.ShowsForever) ? null : Elements.Sum(e => e.Duration);

    /// <summary>
    /// The element showing at a given tick. Looping actions replay from the
    /// Loopstart line; an action that ends in a hold (-1) stays on its last
    /// element for every tick past the finite prefix.
    /// </summary>
    public AnimationElement? ElementAt(int tick)
    {
        if (Elements.Count == 0) return null;

        int total = 0;
        foreach (var e in Elements)
        {
            if (e.ShowsForever)
            {
                if (tick >= total) return e;
                break; // the tick lands inside the finite prefix; walk it below
            }
            total += e.Duration;
        }
        if (total <= 0) return Elements[0];

        // Past the end, replay from the loop point rather than the beginning.
        tick = WrapTick(tick);

        int at = 0;
        foreach (var e in Elements)
        {
            at += e.Duration;
            if (tick < at) return e;
        }
        return Elements[^1];
    }

    /// <summary>
    /// The same wrap ElementAt applies past the end — replay from the Loopstart
    /// line, not from the beginning — expressed as a tick rather than an
    /// element, so the playback playhead can sit on exactly the frame
    /// ElementAt picks. Ticks inside the timeline map to themselves; an action
    /// ending in a hold never reaches the wrap from ElementAt (the hold
    /// element returns first), and the player parks its playhead separately
    /// during the hold dwell.
    /// </summary>
    public int WrapTick(int tick)
    {
        int total = 0;
        foreach (var e in Elements)
        {
            if (e.ShowsForever) break;
            total += e.Duration;
        }
        if (total <= 0) return 0; // a degenerate timeline (all-zero durations) has nowhere to wrap to
        if (tick < total) return tick;

        int head = Elements.Take(LoopStart).Sum(e => e.Duration);
        int body = total - head;
        return body > 0 ? head + (tick - head) % body : 0;
    }

    /// <summary>Element index showing at a tick and how many ticks into it — the same walk as ElementAt.</summary>
    public (int Index, int Elapsed) PositionAt(int tick)
    {
        if (Elements.Count == 0) return (-1, 0);

        int total = 0;
        for (int i = 0; i < Elements.Count; i++)
        {
            if (Elements[i].ShowsForever)
            {
                if (tick >= total) return (i, tick - total);
                break;
            }
            total += Elements[i].Duration;
        }
        if (total <= 0) return (0, 0);

        tick = WrapTick(tick);
        int at = 0;
        for (int i = 0; i < Elements.Count; i++)
        {
            int d = Elements[i].Duration;
            if (tick < at + d) return (i, tick - at);
            at += d;
        }
        return (Elements.Count - 1, 0);
    }

    /// <summary>
    /// What's drawn at a tick, with Ikemen's Interpolate lines applied (anim.go
    /// UpdateInterpolation): while element d shows, if the next element — d+1, or
    /// LoopStart past the end — is marked, d's value moves toward the next one's by
    /// elapsed/duration. Blend only interpolates when d itself has a blend.
    /// </summary>
    public DrawnState StateAt(int tick)
    {
        var (i, elapsed) = PositionAt(tick);
        if (i < 0) return new DrawnState(0, 0, 1, 1, 0, Blend.None);
        var cur = Elements[i];
        var state = new DrawnState(cur.OffsetX, cur.OffsetY, cur.ScaleX, cur.ScaleY, cur.Angle, cur.Blend);
        if (cur.Duration <= 0) return state;

        int next = i + 1 < Elements.Count ? i + 1 : LoopStart;
        if (next < 0 || next >= Elements.Count) return state;
        var n = Elements[next];
        double t = (double)elapsed / cur.Duration;
        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        if (InterpolateOffset.Contains(next))
            state = state with { OffsetX = Lerp(cur.OffsetX, n.OffsetX, t), OffsetY = Lerp(cur.OffsetY, n.OffsetY, t) };
        if (InterpolateScale.Contains(next))
            state = state with { ScaleX = Lerp(cur.ScaleX, n.ScaleX, t), ScaleY = Lerp(cur.ScaleY, n.ScaleY, t) };
        if (InterpolateAngle.Contains(next))
            state = state with { Angle = Lerp(cur.Angle, n.Angle, t) };
        if (InterpolateBlend.Contains(next) && cur.Blend.Mode != BlendMode.None)
            state = state with
            {
                Blend = new Blend(cur.Blend.Mode,
                    (int)Math.Round(Lerp(cur.Blend.Source, n.Blend.Source, t)),
                    (int)Math.Round(Lerp(cur.Blend.Destination, n.Blend.Destination, t))),
            };
        return state;
    }

    /// <summary>Values that interpolate at this tick, by StateAt's rules.</summary>
    public Lerping LerpingAt(int tick)
    {
        var (i, _) = PositionAt(tick);
        if (i < 0 || Elements[i].Duration <= 0) return Lerping.None;
        int next = i + 1 < Elements.Count ? i + 1 : LoopStart;
        if (next < 0 || next >= Elements.Count) return Lerping.None;
        var cur = Elements[i];
        var result = Lerping.None;
        if (InterpolateOffset.Contains(next)) result |= Lerping.Offset;
        if (InterpolateScale.Contains(next)) result |= Lerping.Scale;
        if (InterpolateAngle.Contains(next)) result |= Lerping.Angle;
        if (InterpolateBlend.Contains(next) && cur.Blend.Mode != BlendMode.None) result |= Lerping.Blend;
        return result;
    }

    public bool HasInterpolation =>
        InterpolateOffset.Count + InterpolateScale.Count + InterpolateAngle.Count + InterpolateBlend.Count > 0;

    public override string ToString() => $"Action {Number} ({Elements.Count} frames)";
}

/// <summary>A parsed .air file.</summary>
/// <summary>Utf8Bom: UTF-8 with a byte-order mark. Legacy: not UTF-8; read with a guessed
/// older encoding (AirFile.LegacyEncodingName) and saved as UTF-8.</summary>
public enum AirEncoding { Utf8, Utf8Bom, Legacy }

public sealed class AirFile
{
    public List<AnimationAction> Actions = [];

    /// <summary>The comment and blank lines before the first action (bar its name line), exactly as written.</summary>
    public List<string> Header = [];

    /// <summary>How the file's bytes were read. Utf8Bom is saved with its mark; Legacy is saved as UTF-8.</summary>
    public AirEncoding TextEncoding = AirEncoding.Utf8;
    /// <summary>The guessed encoding of a Legacy file ("Windows-1252", "Shift-JIS"), for the warnings.</summary>
    public string? LegacyEncodingName;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public static AirFile Open(string path)
    {
        var (text, encoding, legacy) = Decode(File.ReadAllBytes(path));
        var air = AirParser.Parse(text);
        air.TextEncoding = encoding;
        air.LegacyEncodingName = legacy;
        return air;
    }

    public void Save(string path)
    {
        AtomicFile.WriteAllBytes(path, Encode(AirWriter.Write(this), TextEncoding));
        if (TextEncoding == AirEncoding.Legacy) (TextEncoding, LegacyEncodingName) = (AirEncoding.Utf8, null); // it's UTF-8 now
    }

    /// <summary>UTF-8 (with or without a byte-order mark) when the bytes are valid UTF-8. Otherwise an old
    /// encoding is guessed: Shift-JIS when the bytes are valid Shift-JIS and hold kana, else Windows-1252.</summary>
    public static (string Text, AirEncoding Encoding, string? LegacyName) Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Utf8Bom))
            return (new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3), AirEncoding.Utf8Bom, null);
        if (TryDecode(new UTF8Encoding(false, throwOnInvalidBytes: true), bytes) is { } utf8)
            return (utf8, AirEncoding.Utf8, null);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // code pages 932/1252 on .NET Core
        if (TryDecode(Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), bytes) is { } sjis
            && sjis.Any(IsJapanese))
            return (sjis, AirEncoding.Legacy, "Shift-JIS");
        return (Encoding.GetEncoding(1252).GetString(bytes), AirEncoding.Legacy, "Windows-1252");
    }

    public static byte[] Encode(string text, AirEncoding encoding) => encoding == AirEncoding.Utf8Bom
        ? [.. Utf8Bom, .. new UTF8Encoding(false).GetBytes(text)]
        : new UTF8Encoding(false).GetBytes(text); // Legacy files are saved as UTF-8

    private static string? TryDecode(Encoding encoding, byte[] bytes)
    {
        try { return encoding.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    /// <summary>Full-width hiragana or katakana. Not half-width katakana (single bytes 0xA1-0xDF, which
    /// Windows-1252 text like "¯" also produces) and not kanji alone (accented letters can pair into one).</summary>
    private static bool IsJapanese(char c) => c is >= '\u3040' and <= '\u30FF';

    public AnimationAction? Find(int number) => Actions.FirstOrDefault(a => a.Number == number);
}
