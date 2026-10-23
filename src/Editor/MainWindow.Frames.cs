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
    private void EditFrameList(string name, Func<int> change) =>
        EditFrameList(name, () => { int row = change(); return (row, new List<int> { row }); });

    /// <summary>As above; change() also returns the frames to leave selected in the table.</summary>
    private void EditFrameList(string name, Func<(int Row, List<int> Selected)> change)
    {
        if (_action is null) return;
        var action = _action;

        var before = action.Elements.ToList();
        int beforeLoop = action.LoopStart;
        int beforeRow = FrameList.SelectedIndex;
        var beforeSelected = SelectedFrameTargets();
        var beforeAttackSegs = CloneSegments(action.DefaultAttackSegments);
        var beforeHurtSegs = CloneSegments(action.DefaultHurtSegments);
        var beforeInterp = CloneInterpolation(action);

        // change() returns the wanted row itself, rather than setting
        // FrameList.SelectedIndex directly: ItemsSource still holds the old
        // list at this point, so an out-of-old-range index gets rejected.
        var (afterRow, afterSelected) = change();

        var after = action.Elements.ToList();
        int afterLoop = action.LoopStart;
        var afterAttackSegs = CloneSegments(action.DefaultAttackSegments);
        var afterHurtSegs = CloneSegments(action.DefaultHurtSegments);
        var afterInterp = CloneInterpolation(action);

        void Apply(List<AnimationElement> state, int loop, int row, List<int> selected,
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
            if (_action == action) RestoreFrameTableSelection(selected);
        }

        _undo.Record(name,
            () => Apply(before, beforeLoop, beforeRow, beforeSelected, beforeAttackSegs, beforeHurtSegs, beforeInterp),
            () => Apply(after, afterLoop, afterRow, afterSelected, afterAttackSegs, afterHurtSegs, afterInterp));
        MarkAirDirty();
        Apply(after, afterLoop, afterRow, afterSelected, afterAttackSegs, afterHurtSegs, afterInterp);
    }

    /// <summary>A deep copy of a frame: fields, boxes and comments, as copying its text would.</summary>
    private static AnimationElement CloneElement(AnimationElement source) => new()
    {
        Group = source.Group, Index = source.Index,
        OffsetX = source.OffsetX, OffsetY = source.OffsetY,
        Duration = source.Duration,
        FlipH = source.FlipH, FlipV = source.FlipV,
        Blend = source.Blend,
        ScaleX = source.ScaleX, ScaleY = source.ScaleY, Angle = source.Angle,
        Attack = [.. source.Attack], Hurt = [.. source.Hurt],
        OwnAttackBoxes = source.OwnAttackBoxes, OwnHurtBoxes = source.OwnHurtBoxes,
        Comments = [.. source.Comments], EndComment = source.EndComment,
    };

    private static SortedSet<int>[] CloneInterpolation(AnimationAction a) =>
        [new(a.InterpolateOffset), new(a.InterpolateScale), new(a.InterpolateAngle), new(a.InterpolateBlend)];

    /// <summary>Independent deep copy — segments are mutated in place by
    /// ShiftDefaultSegments, so a before/after undo snapshot needs its own
    /// objects, not just its own list wrapping the same ones.</summary>
    private static List<DefaultSegment> CloneSegments(List<DefaultSegment> segments) =>
        segments.Select(s => new DefaultSegment { At = s.At, Boxes = [.. s.Boxes] }).ToList();

    /// <summary>Where Insert and Insert Sprite Group add frames: after the last selected frame.</summary>
    private int FrameInsertPoint() => (SelectedFrameTargets() is { Count: > 0 } t ? t[^1] : -1) + 1;

    /// <summary>A frame owns boxes of its own or declares a default.</summary>
    private static bool HasOwnClsn(AnimationAction action, int at) =>
        action.Elements[at].OwnAttackBoxes || action.Elements[at].OwnHurtBoxes
        || action.DefaultAttackSegments.Any(s => s.At == at) || action.DefaultHurtSegments.Any(s => s.At == at);

    /// <summary>Dims each frame and Clsn button that can't act on the current selection.</summary>
    private void UpdateFrameButtons()
    {
        if (FrameInsertButton is null || AnimCanvas is null) return;
        var action = _action;
        var targets = SelectedFrameTargets();
        bool any = action is not null && targets.Count > 0, single = any && targets.Count == 1;
        int count = action?.Elements.Count ?? 0;
        List<AnimationElement> elements = any ? targets.Select(i => action!.Elements[i]).ToList() : [];

        FrameInsertButton.IsEnabled = action is not null;
        InsertSpriteGroupButton.IsEnabled = action is not null && _file is not null;
        FrameDuplicateButton.IsEnabled = any;
        FrameRemoveButton.IsEnabled = any && targets.Count < count;
        FrameUpButton.IsEnabled = any && targets[0] > 0;
        FrameDownButton.IsEnabled = any && targets[^1] < count - 1;
        FrameFlagsButton.IsEnabled = any;

        // Box-level buttons work on one frame: the canvas shows only the current one.
        bool boxSelected = any && AnimCanvas.SelectedBox >= 0 && AnimCanvas.SelectedBox < AnimCanvas.Boxes.Count;
        AddAttackBoxButton.IsEnabled = AddHurtBoxButton.IsEnabled = single;
        // These act on the boxes selected on the canvas, whatever the frame selection.
        DeleteBoxButton.IsEnabled = MakeDefaultButton.IsEnabled = boxSelected;
        CopyOnionClsnButton.IsEnabled = any && AnimOnionSource() is { } src && (src.Hurt.Count > 0 || src.Attack.Count > 0);
        AutoHurtBoxesButton.IsEnabled = elements.Any(el => !el.NoSprite);
        SortBoxesButton.IsEnabled = elements.Any(el => el.OwnAttackBoxes || el.OwnHurtBoxes);
        DeleteAllBoxesButton.IsEnabled = any && targets.Any(i => HasOwnClsn(action!, i));
    }

    private void OnInsertFrame(object? sender, RoutedEventArgs e)
    {
        if (_action is null) return;
        int at = FrameInsertPoint();
        var template = at > 0 && at <= _action.Elements.Count ? _action.Elements[at - 1] : null;

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

            int at = FrameInsertPoint();
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

    /// <summary>Copies the selected frames as one block after the last of them; the copies become the selection.</summary>
    private void OnDuplicateFrame(object? sender, RoutedEventArgs e)
    {
        if (_action is not { } action) return;
        var targets = SelectedFrameTargets();
        if (targets.Count == 0) return;
        int at = targets[^1] + 1;
        int current = at + targets.IndexOf(FrameList.SelectedIndex);

        EditFrameList(targets.Count == 1 ? "duplicate frame" : "duplicate frames", () =>
        {
            // A plain copy/paste of the frames' text block, the lines above each included.
            action.Elements.InsertRange(at, targets.Select(i => CloneElement(action.Elements[i])).ToList());
            action.InsertFrameLines(at, targets.Count);
            for (int i = 0; i < targets.Count; i++) action.CopyFrameLines(targets[i], at + i);
            return (current, Enumerable.Range(at, targets.Count).ToList());
        });
    }

    private void OnRemoveFrame(object? sender, RoutedEventArgs e)
    {
        if (_action is not { } action) return;
        var targets = SelectedFrameTargets();
        if (targets.Count == 0) return;
        if (targets.Count >= action.Elements.Count)
        {
            if (targets.Count > 1) StatusText.Text = "An action needs at least one frame.";
            return;
        }

        EditFrameList(targets.Count == 1 ? "remove frame" : "remove frames", () =>
        {
            // Their lines go with them (a ClsnDefault each declares, Loopstart, Interpolate), as in the text form.
            foreach (int at in targets.OrderDescending())
            {
                action.Elements.RemoveAt(at);
                action.RemoveFrameLines(at);
            }
            int row = Math.Min(targets[0], action.Elements.Count - 1);
            return (row, new List<int> { row });
        });
        if (targets.Count > 1) StatusText.Text = $"Removed {targets.Count} frames.";
    }

    private void OnMoveFrameUp(object? sender, RoutedEventArgs e) => MoveFrame(-1);
    private void OnMoveFrameDown(object? sender, RoutedEventArgs e) => MoveFrame(1);

    /// <summary>
    /// Moves the selected frames one row, lines above them included (text form). A frame
    /// without boxes of its own then inherits whatever default is in force at
    /// its new position, as it would if the text were moved. At either edge nothing moves.
    /// </summary>
    private void MoveFrame(int delta)
    {
        if (_action is not { } action) return;
        var targets = SelectedFrameTargets();
        if (targets.Count == 0 || targets[0] + delta < 0 || targets[^1] + delta >= action.Elements.Count) return;
        int current = FrameList.SelectedIndex + delta;

        EditFrameList(targets.Count == 1 ? "move frame" : "move frames", () =>
        {
            // Swap from the leading end so each frame steps into a row its neighbor just left.
            IEnumerable<int> order = delta < 0 ? targets : Enumerable.Reverse(targets);
            foreach (int at in order)
            {
                int to = at + delta;
                (action.Elements[at], action.Elements[to]) = (action.Elements[to], action.Elements[at]);
                action.SwapFrameLines(at, to);
            }
            return (current, targets.Select(i => i + delta).ToList());
        });
    }

    // ---- copy and paste (Ctrl+C / Ctrl+V) -----------------------------------

    /// <summary>A copied frame with the lines above it, as in the text form.</summary>
    private sealed record FrameClip(AnimationElement Element, List<ClsnBox>? AttackDefault, List<ClsnBox>? HurtDefault, bool[] Interpolate, bool LoopStart);

    private List<FrameClip>? _frameClipboard;

    private void CopyFrames()
    {
        if (_action is not { } action) return;
        var targets = SelectedFrameTargets();
        if (targets.Count == 0) return;
        _frameClipboard = targets.Select(i => new FrameClip(CloneElement(action.Elements[i]),
            action.DefaultAttackSegments.Find(s => s.At == i)?.Boxes.ToList(),
            action.DefaultHurtSegments.Find(s => s.At == i)?.Boxes.ToList(),
            [action.InterpolateOffset.Contains(i), action.InterpolateScale.Contains(i),
             action.InterpolateAngle.Contains(i), action.InterpolateBlend.Contains(i)],
            action.LoopStart > 0 && action.LoopStart == i)).ToList();
        StatusText.Text = targets.Count == 1 ? $"Copied frame {targets[0] + 1}." : $"Copied {targets.Count} frames.";
    }

    /// <summary>Inserts the copied frames and their lines after the current one, in any action, as pasted text would.</summary>
    private void PasteFrames()
    {
        if (_action is not { } action || _frameClipboard is not { Count: > 0 } clip) return;
        int at = Math.Clamp(FrameList.SelectedIndex + 1, 0, action.Elements.Count);

        EditFrameList(clip.Count == 1 ? "paste frame" : "paste frames", () =>
        {
            action.Elements.InsertRange(at, clip.Select(c => CloneElement(c.Element)).ToList());
            action.InsertFrameLines(at, clip.Count);
            for (int i = 0; i < clip.Count; i++)
            {
                int to = at + i;
                if (clip[i].AttackDefault is { } attack) action.DefaultAttackSegments.Add(new DefaultSegment { At = to, Boxes = [.. attack] });
                if (clip[i].HurtDefault is { } hurt) action.DefaultHurtSegments.Add(new DefaultSegment { At = to, Boxes = [.. hurt] });
                if (clip[i].LoopStart) action.LoopStart = to;
                if (clip[i].Interpolate[0]) action.InterpolateOffset.Add(to);
                if (clip[i].Interpolate[1]) action.InterpolateScale.Add(to);
                if (clip[i].Interpolate[2]) action.InterpolateAngle.Add(to);
                if (clip[i].Interpolate[3]) action.InterpolateBlend.Add(to);
            }
            action.DefaultAttackSegments.Sort((a, b) => a.At.CompareTo(b.At));
            action.DefaultHurtSegments.Sort((a, b) => a.At.CompareTo(b.At));
            return (at, Enumerable.Range(at, clip.Count).ToList());
        });
        StatusText.Text = clip.Count == 1 ? "Pasted 1 frame." : $"Pasted {clip.Count} frames.";
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

    /// <summary>Special Flags dialog: Loopstart and the Interpolate lines above the selected frames. OK = one undo.
    /// With several frames, Loopstart is dimmed and a mixed Interpolate flag left mixed changes nothing.</summary>
    private async void OnSpecialFlags(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_action is not { } action || CurrentElement is null) return;
            var targets = SelectedFrameTargets();
            bool multi = targets.Count > 1;
            int at = FrameList.SelectedIndex;
            bool? All(SortedSet<int> set)
            {
                int n = targets.Count(set.Contains);
                return n == 0 ? false : n == targets.Count ? true : null;
            }
            var initial = new Dialogs.SpecialFlags(!multi && at > 0 && action.LoopStart == at,
                All(action.InterpolateOffset), All(action.InterpolateScale),
                All(action.InterpolateAngle), All(action.InterpolateBlend));
            string heading = multi ? $"Flags for {targets.Count} frames:" : $"Flags for frame {at + 1}:";
            if (await Dialogs.PromptSpecialFlags(this, heading, initial, loopEnabled: !multi && at > 0) is not { } flags) return;
            if (flags == initial || _action != action || targets.Any(t => t >= action.Elements.Count)) return;

            int beforeLoop = action.LoopStart;
            var beforeInterp = CloneInterpolation(action);
            // Unticking Loopstart on the loop frame loops from the start again; elsewhere it leaves the loop alone.
            int afterLoop = multi ? beforeLoop : flags.LoopStart == true ? at : (beforeLoop == at ? 0 : beforeLoop);
            var afterInterp = CloneInterpolation(action);
            void Set(SortedSet<int> set, bool? on)
            {
                if (on is null) return; // mixed: each frame keeps its own
                foreach (int t in targets) { if (on.Value) set.Add(t); else set.Remove(t); }
            }
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
            StatusText.Text = multi ? $"Updated special flags on {targets.Count} frames." : $"Updated special flags on frame {at + 1}.";
        }
        catch (Exception ex)
        {
            await Failed("OnSpecialFlags", ex);
        }
    }
}
