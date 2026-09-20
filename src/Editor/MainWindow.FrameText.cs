using Air;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Editor;

/// <summary>
/// The Frames panel's Text view: same panel space as Table, a toggle switches
/// which one is shown, and both read/write the same AnimationAction —
/// selection (the "current frame") stays in sync either way via
/// FrameList.SelectedIndex, which everything else (Boxes editor, canvas
/// preview, Table) already keys off. List view is retired (per instruction);
/// FrameList itself stays alive but permanently hidden, since it's still the
/// one piece of state "current frame" is tracked through everywhere else.
/// </summary>
public partial class MainWindow
{
    /// <summary>Each frame's character span in the text view's actual current content
    /// (not a re-serialized form — see ComputeLiveFrameSpans), parallel to _action.Elements
    /// right after a sync, but kept live via LiveRespan() while the text is hand-edited.</summary>
    private List<(int Start, int Length)> _frameSpans = [];

    private bool _frameTextActive;

    /// <summary>True while a sync is moving data one way, so the other direction's handler doesn't react to it.</summary>
    private bool _suppressFrameTextSync;

    /// <summary>The text last generated from the action, so EditFinished can tell "nothing was actually typed" from "something changed" with a plain string compare instead of deep-comparing parsed structures.</summary>
    private string? _lastSyncedFrameText;

    /// <summary>
    /// Restarted on every keystroke, fires once typing pauses — see
    /// LiveRespan(). 250ms is short enough that Boxes/canvas feel like
    /// they're following along, long enough not to rescan on every character.
    /// </summary>
    private readonly DispatcherTimer _respanTimer = new(); // Interval from settings.json TextViewRespanMilliseconds, set in WireFrameText

    private void WireFrameText()
    {
        FrameTextView.CaretMoved += OnFrameTextCaretMoved;
        FrameTextView.EditFinished += OnFrameTextEditFinished;
        FrameTextView.TextEdited += OnFrameTextEdited;
        _respanTimer.Interval = TimeSpan.FromMilliseconds(Settings.Checked(nameof(Settings.TextViewRespanMilliseconds),
            _settings.TextViewRespanMilliseconds, Settings.DefaultTextViewRespanMilliseconds, v => v > 0));
        _respanTimer.Tick += (_, _) => LiveRespan();
    }

    /// <summary>Table/Text are hand-managed as a radio group, same reason as the Paint Mode tool buttons — Avalonia's ToggleButton has no GroupName.</summary>
    private void OnSelectFrameView(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag } clicked) return;
        CrashLog.Trace($"OnSelectFrameView: {tag}");

        foreach (var b in new[] { FrameTableViewButton, FrameTextViewToggle })
            b.IsChecked = ReferenceEquals(b, clicked);

        _frameTextActive = tag == "Text";
        _frameTableActive = tag == "Table";
        FrameTablePanel.IsVisible = _frameTableActive;
        FrameTextView.IsVisible = _frameTextActive;

        if (_frameTextActive) SyncTextFromAction();
        if (_frameTableActive) RebuildFrameTable();
    }

    /// <summary>Regenerates the text view's content from the action and highlights the current frame. Called whenever the action or its elements change from anywhere (list edits, undo/redo, switching actions).</summary>
    private void SyncTextFromAction()
    {
        if (!_frameTextActive || _action is null) return;

        var (text, spans) = AirWriter.WriteActionWithSpans(_action);
        _frameSpans = [.. spans];

        _suppressFrameTextSync = true;
        FrameTextView.Text = text;
        _suppressFrameTextSync = false;
        _lastSyncedFrameText = text;

        HighlightCurrentFrameInText();
    }

    private void HighlightCurrentFrameInText()
    {
        int i = FrameList.SelectedIndex;
        if (i >= 0 && i < _frameSpans.Count) FrameTextView.SetHighlight(_frameSpans[i].Start, _frameSpans[i].Length, moveCaret: false);
        else FrameTextView.SetHighlight(-1, 0, moveCaret: false);
    }

    /// <summary>Caret moved in the text view — find which frame it's in and select that row, so Boxes/canvas stay in sync while just reading or clicking around, not only after a reparse.</summary>
    private void OnFrameTextCaretMoved(int caret)
    {
        if (_suppressFrameTextSync || _frameSpans.Count == 0) return;

        int frame = FrameAtCaret(caret);
        if (FrameList.SelectedIndex == frame) return;

        _suppressFrameTextSync = true; // selecting the row must not re-render the text out from under the caret
        FrameList.SelectedIndex = frame;
        _suppressFrameTextSync = false;
        Pause();
        ShowFrame();
    }

    /// <summary>Which frame row a caret position falls in, per the current _frameSpans. Shared by
    /// OnFrameTextCaretMoved and the commit path, so both agree on the same mapping.</summary>
    private int FrameAtCaret(int caret)
    {
        int frame = 0;
        for (int i = 0; i < _frameSpans.Count; i++)
        {
            if (caret < _frameSpans[i].Start) break;
            frame = i;
        }
        return frame;
    }

    /// <summary>
    /// Text changed — restart the respan debounce. Guarded on _suppressFrameTextSync
    /// so SyncTextFromAction's own programmatic Text set (which already computes
    /// fresh, correct spans) doesn't also schedule a redundant rescan.
    /// </summary>
    private void OnFrameTextEdited()
    {
        if (_suppressFrameTextSync) return;
        _respanTimer.Stop();
        _respanTimer.Start();
    }

    /// <summary>
    /// Fires once typing pauses. Rebuilds _frameSpans directly from whatever is
    /// actually in the text box right now — not a fresh AirWriter serialization,
    /// which would drift from the real, hand-edited text — then re-syncs the
    /// selected frame to wherever the caret ended up. Fixes the caret drift:
    /// previously _frameSpans only refreshed on commit/reparse, so mid-edit
    /// clicks (and the highlight band) could land on the wrong frame.
    /// </summary>
    private void LiveRespan()
    {
        _respanTimer.Stop();
        if (!_frameTextActive) return;

        _frameSpans = ComputeLiveFrameSpans(FrameTextView.Text);
        OnFrameTextCaretMoved(FrameTextView.CaretIndex);
    }

    /// <summary>
    /// A forgiving line scan mirroring AirParser's own frame-line recognition
    /// (skip [Begin Action]/Copy Action/Loopstart/Interpolate lines and Clsn
    /// declarations plus their box-continuation lines; anything else starting
    /// with a digit or '-' is a frame). Unlike AirParser this doesn't require a
    /// line to fully parse as a valid element — a line mid-edit (a trailing
    /// comma, a half-typed number) still counts as "this frame's line" rather
    /// than vanishing from the count, which matters while someone is still
    /// typing it. Spans are measured directly against the given text (not a
    /// re-serialized canonical form), so they land at the exact characters on
    /// screen and line up with FrameTextView.CaretIndex.
    /// </summary>
    private static List<(int Start, int Length)> ComputeLiveFrameSpans(string text)
    {
        var spans = new List<(int Start, int Length)>();
        int pos = 0;
        int clsnBoxesLeft = 0; // remaining "Clsn1[n] = ..." lines that continue a declaration, not frames

        foreach (var raw in text.Split('\n'))
        {
            int lineStart = pos;
            pos += raw.Length + 1; // +1 for the '\n' Split consumed; harmless on the final line

            int semi = raw.IndexOf(';');
            string code = (semi < 0 ? raw : raw[..semi]).Trim();
            if (code.Length == 0) continue;
            string lower = code.ToLowerInvariant();

            if (clsnBoxesLeft > 0)
            {
                if (lower.StartsWith("clsn", StringComparison.Ordinal)) clsnBoxesLeft--;
                continue;
            }

            if (code[0] == '[') continue;
            if (lower.StartsWith("copy action ", StringComparison.Ordinal)
                || lower.StartsWith("loopstart", StringComparison.Ordinal)
                || lower.StartsWith("interpolate ", StringComparison.Ordinal)) continue;
            if (lower.StartsWith("clsn", StringComparison.Ordinal))
            {
                int colon = lower.IndexOf(':');
                if (colon >= 0 && int.TryParse(lower[(colon + 1)..].Trim(), out int n) && n > 0) clsnBoxesLeft = n;
                continue;
            }
            if (code[0] is not ('-' or >= '0' and <= '9')) continue;

            int codeStart = lineStart + raw.IndexOf(code, StringComparison.Ordinal);
            spans.Add((codeStart, code.Length));
        }
        return spans;
    }

    /// <summary>Snapshot of everything a text reparse could plausibly change, for one clean undo entry.</summary>
    private readonly record struct ActionSnapshot(
        List<AnimationElement> Elements, int LoopStart, int? CopyAction, List<string> Comments, List<string> TrailingComments,
        List<DefaultSegment> DefaultAttackSegments, List<DefaultSegment> DefaultHurtSegments,
        SortedSet<int> InterpolateOffset, SortedSet<int> InterpolateScale,
        SortedSet<int> InterpolateAngle, SortedSet<int> InterpolateBlend);

    private static ActionSnapshot SnapshotAction(AnimationAction a) => new(
        a.Elements.ToList(), a.LoopStart, a.CopyAction, a.Comments.ToList(), a.TrailingComments.ToList(),
        CloneSegments(a.DefaultAttackSegments), CloneSegments(a.DefaultHurtSegments),
        new SortedSet<int>(a.InterpolateOffset), new SortedSet<int>(a.InterpolateScale),
        new SortedSet<int>(a.InterpolateAngle), new SortedSet<int>(a.InterpolateBlend));

    private void ApplyActionSnapshot(AnimationAction action, ActionSnapshot s, int row)
    {
        action.Elements = [.. s.Elements];
        action.LoopStart = s.LoopStart;
        action.CopyAction = s.CopyAction;
        action.Comments = [.. s.Comments];
        action.TrailingComments = [.. s.TrailingComments];
        action.DefaultAttackSegments = CloneSegments(s.DefaultAttackSegments);
        action.DefaultHurtSegments = CloneSegments(s.DefaultHurtSegments);
        action.InterpolateOffset = new SortedSet<int>(s.InterpolateOffset);
        action.InterpolateScale = new SortedSet<int>(s.InterpolateScale);
        action.InterpolateAngle = new SortedSet<int>(s.InterpolateAngle);
        action.InterpolateBlend = new SortedSet<int>(s.InterpolateBlend);

        RebuildActionList(); // the comment shown there may have changed
        RebuildFrameList();
        _suppressEvents = true;
        FrameList.SelectedIndex = Math.Clamp(row, -1, action.Elements.Count - 1);
        _suppressEvents = false;
        ShowFrame();
        SyncTextFromAction();
        RebuildFrameTable();
    }

    /// <summary>Text edited and lost focus — reparse and, if anything actually changed, apply as one undoable edit.</summary>
    private void OnFrameTextEditFinished()
    {
        CrashLog.Trace("OnFrameTextEditFinished: start");
        if (_action is null) return;
        _respanTimer.Stop(); // about to reparse for real; no need for the debounce to fire after
        if (FrameTextView.Text == _lastSyncedFrameText) return; // nothing was actually typed

        var parsed = AirParser.Parse(FrameTextView.Text).Actions.FirstOrDefault();
        if (parsed is null)
        {
            StatusText.Text = "Couldn't find a [Begin Action] line in that text — nothing applied.";
            SyncTextFromAction(); // put back the canonical text, discarding the unparseable edit
            return;
        }

        var action = _action;
        var before = SnapshotAction(action);
        var after = SnapshotAction(parsed);

        // Re-derived from the caret against a fresh live scan of the text about
        // to be reparsed, not trusted from FrameList.SelectedIndex or the
        // possibly-stale _frameSpans — focus can leave the text view before the
        // 250ms debounce ever gets a chance to fire.
        _frameSpans = ComputeLiveFrameSpans(FrameTextView.Text);
        int row = FrameAtCaret(FrameTextView.CaretIndex);
        _undo.Record("edit action as text",
            () => ApplyActionSnapshot(action, before, row),
            () => ApplyActionSnapshot(action, after, row));
        MarkAirDirty();
        ApplyActionSnapshot(action, after, row);
    }
}
