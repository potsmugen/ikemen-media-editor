using Air;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Editor;

/// <summary>
/// Editing the frames of an action: timing, the sprite each one names, offsets,
/// flip and blend, plus inserting, removing and reordering them.
/// </summary>
public partial class MainWindow
{
    /// <summary>Everything about a frame that the panel can change.</summary>
    private readonly record struct FrameState(
        int Group, int Index, int OffsetX, int OffsetY, int Duration,
        bool FlipH, bool FlipV, Blend Blend, float ScaleX, float ScaleY, float Angle)
    {
        public static FrameState Of(AnimationElement e) =>
            new(e.Group, e.Index, e.OffsetX, e.OffsetY, e.Duration, e.FlipH, e.FlipV, e.Blend,
                e.ScaleX, e.ScaleY, e.Angle);

        public void ApplyTo(AnimationElement e)
        {
            e.Group = Group;
            e.Index = Index;
            e.OffsetX = OffsetX;
            e.OffsetY = OffsetY;
            e.Duration = Duration;
            e.FlipH = FlipH;
            e.FlipV = FlipV;
            e.Blend = Blend;
            e.ScaleX = ScaleX;
            e.ScaleY = ScaleY;
            e.Angle = Angle;
        }
    }

    /// <summary>Applies a change to the selected frame and records how to undo it.</summary>
    private void EditFrame(string name, FrameState after, string? mergeKey = null)
    {
        if (_action is null || CurrentElement is not { } element) return;

        var before = FrameState.Of(element);
        if (before == after) return;

        void Apply(FrameState state)
        {
            state.ApplyTo(element);
            RebuildFrameList();
            ShowFrame();
            SyncTextFromAction();
            RefreshFrameTableRow(FrameList.SelectedIndex);
        }

        _undo.Do(name, () => Apply(after), () => Apply(before), mergeKey);
        MarkAirDirty();
    }

    // ---- adding, removing and reordering ----------------------------------

    /// <summary>
    /// Changes the frame list itself. Loop start is captured too, since removing
    /// or reordering frames can move where the loop should be — and so are the
    /// default segments, since an insert/remove shifts which element index each
    /// one governs from (see AnimationAction.ShiftDefaultSegments).
    /// </summary>
    private void EditFrameList(string name, Func<int> change)
    {
        if (_action is null) return;
        var action = _action;

        var before = action.Elements.ToList();
        int beforeLoop = action.LoopStart;
        int beforeRow = FrameList.SelectedIndex;
        var beforeAttackSegs = CloneSegments(action.DefaultAttackSegments);
        var beforeHurtSegs = CloneSegments(action.DefaultHurtSegments);
        var beforeInterp = CloneInterpolation(action);

        // change() returns the wanted row itself, rather than setting
        // FrameList.SelectedIndex directly: ItemsSource still holds the old
        // list at this point, so an out-of-old-range index gets rejected.
        int afterRow = change();

        var after = action.Elements.ToList();
        int afterLoop = action.LoopStart;
        var afterAttackSegs = CloneSegments(action.DefaultAttackSegments);
        var afterHurtSegs = CloneSegments(action.DefaultHurtSegments);
        var afterInterp = CloneInterpolation(action);

        void Apply(List<AnimationElement> state, int loop, int row,
            List<DefaultSegment> attackSegs, List<DefaultSegment> hurtSegs, SortedSet<int>[] interp)
        {
            action.Elements = [.. state];
            action.LoopStart = Math.Clamp(loop, 0, Math.Max(0, state.Count - 1));
            action.DefaultAttackSegments = CloneSegments(attackSegs);
            action.DefaultHurtSegments = CloneSegments(hurtSegs);
            action.InterpolateOffset = new SortedSet<int>(interp[0]);
            action.InterpolateScale = new SortedSet<int>(interp[1]);
            action.InterpolateAngle = new SortedSet<int>(interp[2]);
            action.InterpolateBlend = new SortedSet<int>(interp[3]);
            action.RebakeInheritedBoxes(); // inherited copies follow wherever the defaults now are
            RebuildFrameList();
            _suppressEvents = true;
            FrameList.SelectedIndex = Math.Clamp(row, -1, state.Count - 1);
            _suppressEvents = false;
            ShowFrame();
            SyncTextFromAction();
            RebuildFrameTable();
        }

        _undo.Record(name,
            () => Apply(before, beforeLoop, beforeRow, beforeAttackSegs, beforeHurtSegs, beforeInterp),
            () => Apply(after, afterLoop, afterRow, afterAttackSegs, afterHurtSegs, afterInterp));
        MarkAirDirty();
        Apply(after, afterLoop, afterRow, afterAttackSegs, afterHurtSegs, afterInterp);
    }

    private static SortedSet<int>[] CloneInterpolation(AnimationAction a) =>
        [new(a.InterpolateOffset), new(a.InterpolateScale), new(a.InterpolateAngle), new(a.InterpolateBlend)];

    /// <summary>Independent deep copy — segments are mutated in place by
    /// ShiftDefaultSegments, so a before/after undo snapshot needs its own
    /// objects, not just its own list wrapping the same ones.</summary>
    private static List<DefaultSegment> CloneSegments(List<DefaultSegment> segments) =>
        segments.Select(s => new DefaultSegment { At = s.At, Boxes = [.. s.Boxes] }).ToList();

    private void OnInsertFrame(object? sender, RoutedEventArgs e)
    {
        if (_action is null) return;
        int at = FrameList.SelectedIndex + 1;
        var template = CurrentElement;

        EditFrameList("insert frame", () =>
        {
            var element = new AnimationElement
            {
                Group = template?.Group ?? 0,
                Index = (template?.Index ?? -1) + 1,
                Duration = template?.Duration ?? 10,
                Attack = [.. template?.Attack ?? []],
                Hurt = [.. template?.Hurt ?? []],
                OwnAttackBoxes = template?.OwnAttackBoxes ?? false,
                OwnHurtBoxes = template?.OwnHurtBoxes ?? false,
            };
            int insertAt = Math.Clamp(at, 0, _action.Elements.Count);
            _action.Elements.Insert(insertAt, element);
            _action.InsertFrameLines(insertAt, 1);
            return insertAt;
        });
    }

    /// <summary>
    /// Bulk version of Insert: every sprite sharing one group number becomes
    /// its own frame, in index order, right after the current one. Duration
    /// defaults to 1 tick each — a starting point for a quick flip-through,
    /// not a guess at the real animation timing.
    /// </summary>
    private async void OnInsertSpriteGroup(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_action is null) { StatusText.Text = "Open or create an action first."; return; }
            if (_file is null) { StatusText.Text = "No sprite file loaded to insert from."; return; }

            var text = await Dialogs.PromptText(this, "Insert Sprite Group",
                "Sprite group number — every sprite in that group is appended as its own frame, in index order.", "");
            if (text is null) return;
            if (!int.TryParse(text.Trim(), out int group))
            {
                StatusText.Text = "Couldn't parse that as a group number.";
                return;
            }

            var sprites = _file.Sprites.Where(s => s.Group == group).OrderBy(s => s.Index).ToList();
            if (sprites.Count == 0)
            {
                StatusText.Text = $"No sprites found in group {group}.";
                return;
            }

            int at = FrameList.SelectedIndex + 1;
            EditFrameList($"insert sprite group {group}", () =>
            {
                // Attack/Hurt hold a baked copy of the action's current default even
                // when not "own" (see BoxesFor/Timeline, which read them directly
                // rather than looking up a segment themselves) — OnInsertFrame keeps
                // this invariant by copying its template frame's boxes; these new
                // sprites have no template, so each gets a copy of whatever segment
                // already governed the insertion point, captured once, before any of
                // them go in — they're a contiguous run with no default of their own,
                // so they should all agree on the same inherited one.
                var attackDefault = _action!.AttackSegmentAt(at)?.Boxes ?? [];
                var hurtDefault = _action.HurtSegmentAt(at)?.Boxes ?? [];
                int insertAt = at;
                foreach (var sprite in sprites)
                {
                    _action.Elements.Insert(insertAt, new AnimationElement
                    {
                        Group = sprite.Group, Index = sprite.Index, Duration = 1,
                        Attack = [.. attackDefault], Hurt = [.. hurtDefault],
                    });
                    insertAt++;
                }
                _action.InsertFrameLines(at, sprites.Count);
                return insertAt - 1;
            });
            StatusText.Text = $"Inserted {sprites.Count} frame(s) from group {group}.";
        }
        catch (Exception ex)
        {
            await Failed("OnInsertSpriteGroup", ex);
        }
    }

    private void OnDuplicateFrame(object? sender, RoutedEventArgs e)
    {
        if (_action is null || CurrentElement is not { } source) return;
        int at = FrameList.SelectedIndex + 1;

        EditFrameList("duplicate frame", () =>
        {
            var copy = new AnimationElement
            {
                Group = source.Group, Index = source.Index,
                OffsetX = source.OffsetX, OffsetY = source.OffsetY,
                Duration = source.Duration,
                FlipH = source.FlipH, FlipV = source.FlipV,
                Blend = source.Blend,
                ScaleX = source.ScaleX, ScaleY = source.ScaleY, Angle = source.Angle,
                Attack = [.. source.Attack], Hurt = [.. source.Hurt],
                OwnAttackBoxes = source.OwnAttackBoxes, OwnHurtBoxes = source.OwnHurtBoxes,
            };
            // A plain copy/paste of the frame's text block, the lines above it included.
            _action.Elements.Insert(at, copy);
            _action.InsertFrameLines(at, 1);
            _action.CopyFrameLines(at - 1, at);
            return at;
        });
    }

    private void OnRemoveFrame(object? sender, RoutedEventArgs e)
    {
        if (_action is null || CurrentElement is null || _action.Elements.Count <= 1) return;
        int at = FrameList.SelectedIndex;

        EditFrameList("remove frame", () =>
        {
            // Its lines go with it (a ClsnDefault it declares, Loopstart, Interpolate), as in the text form.
            _action.Elements.RemoveAt(at);
            _action.RemoveFrameLines(at);
            return Math.Min(at, _action.Elements.Count - 1);
        });
    }

    private void OnMoveFrameUp(object? sender, RoutedEventArgs e) => MoveFrame(-1);
    private void OnMoveFrameDown(object? sender, RoutedEventArgs e) => MoveFrame(1);

    /// <summary>
    /// Swaps two adjacent frames, lines above them included (text form). A frame
    /// without boxes of its own then inherits whatever default is in force at
    /// its new position, as it would if the text were moved.
    /// </summary>
    private void MoveFrame(int delta)
    {
        if (_action is null) return;
        var action = _action;
        int at = FrameList.SelectedIndex;
        int to = at + delta;
        if (at < 0 || to < 0 || to >= action.Elements.Count) return;

        EditFrameList("move frame", () =>
        {
            (action.Elements[at], action.Elements[to]) = (action.Elements[to], action.Elements[at]);
            action.SwapFrameLines(at, to);
            return to;
        });
    }

    /// <summary>
    /// Sets the Loopstart line to this frame, which is where the action replays
    /// from once it reaches the end. Clicking the already-looping frame clears
    /// it instead — a Loopstart at frame 0 is the no-line case AirWriter treats
    /// as absent — so the button toggles rather than no-op.
    /// </summary>
    private void OnSetLoopStart(object? sender, RoutedEventArgs e)
    {
        if (_action is null || FrameList.SelectedIndex < 0) return;
        int at = FrameList.SelectedIndex;

        var action = _action;
        int before = action.LoopStart;
        if (before == 0 && at == 0) return; // no line set, clicked frame 0: nothing to toggle
        at = at == before ? 0 : at;

        void Apply(int loop)
        {
            action.LoopStart = loop;
            RebuildFrameList();
            ShowFrame();
            SyncTextFromAction();
            RebuildFrameTable();
        }

        _undo.Record("loop start", () => Apply(before), () => Apply(at));
        Apply(at);
        MarkAirDirty();
        StatusText.Text = at == 0 ? "The action loops from the beginning; no Loopstart line is written."
            : before == 0 ? $"The action now loops from frame {at + 1}."
            : $"The action now loops from frame {at + 1} (was frame {before + 1}).";
    }

    /// <summary>Special Flags dialog: Loopstart and the Interpolate lines above the selected frame. OK = one undo.</summary>
    private async void OnSpecialFlags(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_action is not { } action || CurrentElement is null) return;
            int at = FrameList.SelectedIndex;
            var initial = new Dialogs.SpecialFlags(at > 0 && action.LoopStart == at,
                action.InterpolateOffset.Contains(at), action.InterpolateScale.Contains(at),
                action.InterpolateAngle.Contains(at), action.InterpolateBlend.Contains(at));
            if (await Dialogs.PromptSpecialFlags(this, at + 1, initial, loopEnabled: at > 0) is not { } flags) return;
            if (flags == initial || _action != action || at >= action.Elements.Count) return;

            int beforeLoop = action.LoopStart;
            var beforeInterp = CloneInterpolation(action);
            // Unticking Loopstart on the loop frame loops from the start again; elsewhere it leaves the loop alone.
            int afterLoop = flags.LoopStart ? at : (beforeLoop == at ? 0 : beforeLoop);
            var afterInterp = CloneInterpolation(action);
            void Set(SortedSet<int> set, bool on) { if (on) set.Add(at); else set.Remove(at); }
            Set(afterInterp[0], flags.Offset);
            Set(afterInterp[1], flags.Scale);
            Set(afterInterp[2], flags.Angle);
            Set(afterInterp[3], flags.Blend);

            void Apply(int loop, SortedSet<int>[] interp)
            {
                action.LoopStart = loop;
                action.InterpolateOffset = new SortedSet<int>(interp[0]);
                action.InterpolateScale = new SortedSet<int>(interp[1]);
                action.InterpolateAngle = new SortedSet<int>(interp[2]);
                action.InterpolateBlend = new SortedSet<int>(interp[3]);
                if (_action != action) return;
                RebuildFrameList();
                ShowFrame();
                SyncTextFromAction();
                RebuildFrameTable();
            }
            _undo.Do("special flags", () => Apply(afterLoop, afterInterp), () => Apply(beforeLoop, beforeInterp));
            MarkAirDirty();
            StatusText.Text = $"Updated special flags on frame {at + 1}.";
        }
        catch (Exception ex)
        {
            await Failed("OnSpecialFlags", ex);
        }
    }
}
