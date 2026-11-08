using Sff;

namespace Editor;

/// <summary>
/// Paint Mode's own undo history for the pending session: a linear list of session
/// snapshots (sprite pixels plus layers), with a position in it. Undo/Redo move the
/// position and restore that snapshot. Commit, Reset, Discard and leaving the sprite clear it;
/// Commit still records the whole session as one main undo step.
/// </summary>
public partial class MainWindow
{
    /// <summary>The sprite's pixels and the layers at one point in the session. Layers stay indexed-only.</summary>
    private sealed record PaintState(PaintPixels Pixels, bool Dirty, byte[]? Base, byte[]? Top, bool TopVisible, bool BottomVisible);

    private readonly List<PaintState> _paintStates = [];
    /// <summary>_paintStepNames[i] turns _paintStates[i] into _paintStates[i + 1].</summary>
    private readonly List<string> _paintStepNames = [];
    private int _paintStatePos = -1;
    private Sprite? _paintHistorySprite;

    private bool CanPaintUndo => _paintStatePos > 0 && ReferenceEquals(_paintHistorySprite, _current);
    private bool CanPaintRedo => _paintStatePos >= 0 && _paintStatePos < _paintStates.Count - 1 && ReferenceEquals(_paintHistorySprite, _current);

    /// <summary>The session differs from where it started: pixels changed, or a top layer exists.</summary>
    private bool PaintSessionHasChanges() => PendingPaintChanged() || _paintTopLayer is not null;

    private static byte[]? CloneOrNull(byte[]? b) => b is null ? null : (byte[])b.Clone();

    private PaintState? CapturePaintState() => _current is { } sprite && PaintPixels.From(sprite) is { } pixels
        ? new PaintState(pixels.Snapshot(), sprite.PixelsDirty, CloneOrNull(_paintBaseLayer), CloneOrNull(_paintTopLayer),
            _paintTopVisible, _paintBottomVisible)
        : null;

    private static bool Same(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    private static bool SameState(PaintState a, PaintState b) =>
        a.Pixels.Same(b.Pixels) && Same(a.Base, b.Base) && Same(a.Top, b.Top)
        && a.TopVisible == b.TopVisible && a.BottomVisible == b.BottomVisible;

    /// <summary>Call before a change: makes sure the history starts at the current state of this sprite.</summary>
    private void EnsurePaintHistory()
    {
        if (ReferenceEquals(_paintHistorySprite, _current) && _paintStatePos >= 0) return;
        ClearPaintHistory();
        if (CapturePaintState() is not { } state) return;
        _paintHistorySprite = _current;
        _paintStates.Add(state);
        _paintStatePos = 0;
    }

    /// <summary>Call after a change: records it as one step unless nothing changed. Drops any redo steps.</summary>
    private void PaintStep(string name)
    {
        if (_paintStatePos < 0 || !ReferenceEquals(_paintHistorySprite, _current) || CapturePaintState() is not { } state) return;
        if (SameState(state, _paintStates[_paintStatePos])) return;
        _paintStates.RemoveRange(_paintStatePos + 1, _paintStates.Count - _paintStatePos - 1);
        _paintStepNames.RemoveRange(_paintStatePos, _paintStepNames.Count - _paintStatePos);
        _paintStates.Add(state);
        _paintStepNames.Add(name);
        int limit = Settings.Checked(nameof(Settings.SpritesSection.PaintUndoLimit), _settings.Sprites.PaintUndoLimit,
            Settings.DefaultPaintUndoLimit, v => v is >= 1 and <= 10000);
        while (_paintStepNames.Count > limit) { _paintStates.RemoveAt(0); _paintStepNames.RemoveAt(0); }
        _paintStatePos = _paintStates.Count - 1;
        SyncPaintButtons();
        SyncUndoMenu();
    }

    private PaintState? CurrentPaintState() => _paintStatePos >= 0 ? _paintStates[_paintStatePos] : null;

    /// <summary>
    /// Folds the step that led to <paramref name="previous"/> into the latest one, when the latest step came straight after it.
    /// A Replace double-click uses it, so its first click's square isn't an undo step of its own
    /// </summary>
    private void MergePaintStepIntoPrevious(PaintState? previous)
    {
        int i = _paintStatePos - 1;
        if (previous is null || i < 1 || !ReferenceEquals(_paintStates[i], previous)) return;
        _paintStates.RemoveAt(i);
        _paintStepNames.RemoveAt(i - 1);
        _paintStatePos--;
        SyncPaintButtons();
        SyncUndoMenu();
    }

    private void ClearPaintHistory()
    {
        _paintStates.Clear();
        _paintStepNames.Clear();
        _paintStatePos = -1;
        _paintHistorySprite = null;
        SyncUndoMenu();
    }

    private void PaintUndo()
    {
        string name = _paintStepNames[_paintStatePos - 1];
        _paintStatePos--;
        var undone = RestorePaintState(_paintStates[_paintStatePos]);
        if (undone) StatusText.Text = $"Undid {name}.";
    }

    private void PaintRedo()
    {
        string name = _paintStepNames[_paintStatePos];
        _paintStatePos++;
        var redone = RestorePaintState(_paintStates[_paintStatePos]);
        if (redone) StatusText.Text = $"Redid {name}.";
    }

    /// <summary>Puts the sprite and layers back as the snapshot has them. The session stays open, so redo still works. False when the session dissolved instead, e.g. after a palette mapping converted the sprite.</summary>
    private bool RestorePaintState(PaintState state)
    {
        if (_current is not { } sprite) return true;
        if (!SessionMatchesSprite(state.Pixels, sprite))
        {
            // The sprite's pixels were replaced under the session; drop it instead of restoring.
            DissolvePaintSession();
            return false;
        }
        if (state.Pixels.TrueColor) sprite.Direct = (byte[])state.Pixels.Data.Clone();
        else sprite.Indexed = (byte[])state.Pixels.Data.Clone();
        sprite.PixelsDirty = state.Dirty;
        _paintBaseLayer = CloneOrNull(state.Base);
        _paintTopLayer = CloneOrNull(state.Top);
        _paintTopVisible = state.TopVisible;
        _paintBottomVisible = state.BottomVisible;
        _paintOnTop = _paintOnTop && _paintTopLayer is not null;
        _floatingPaste = null;
        SpriteCanvas.SetSelection(null);
        SpriteCanvas.PaintTarget = ActivePaintLayerPixels();
        SpriteCanvas.Show(sprite, ActivePalette(), _showMask);
        SyncPaintLayerControls();
        SyncPaintButtons();
        SyncDirtyFromUndo();
        ResetPlaybackCaches();
        SyncUndoMenu();
        return true;
    }

    /// <summary>Commit and Reset only while the session has changes; undoing back to the start greys them out.</summary>
    private void SyncPaintButtons() =>
        PaintCommitButton.IsEnabled = PaintResetButton.IsEnabled = _paintBaseline is not null && PaintSessionHasChanges();

    /// <summary>Edit > Undo/Redo inside a paint session. False when the main history should handle it.</summary>
    private bool TryPaintUndo()
    {
        if (CanPaintUndo) { PaintUndo(); return true; }
        if (_paintBaseline is null) return false;
        if (PaintSessionHasChanges())
        {
            StatusText.Text = "Older paint steps were dropped (settings.json Sprites.PaintUndoLimit). Reset drops the rest.";
            return true;
        }
        DropPendingPaint(); // back at the start: close the empty session, then undo the main history
        return false;
    }

    private bool TryPaintRedo()
    {
        if (CanPaintRedo) { PaintRedo(); return true; }
        if (_paintBaseline is null) return false;
        if (PaintSessionHasChanges())
        {
            StatusText.Text = "Commit or Reset the paint changes first.";
            return true;
        }
        DropPendingPaint();
        return false;
    }

    /// <summary>Edit menu labels for the paint history, or null to show the main history's.</summary>
    private (bool Enabled, string Header)? PaintUndoMenu() =>
        CanPaintUndo ? (true, $"_Undo {_paintStepNames[_paintStatePos - 1]}")
        : _paintBaseline is not null && PaintSessionHasChanges() ? (false, "_Undo") : null;

    private (bool Enabled, string Header)? PaintRedoMenu() =>
        CanPaintRedo ? (true, $"_Redo {_paintStepNames[_paintStatePos]}")
        : _paintBaseline is not null && PaintSessionHasChanges() ? (false, "_Redo") : null;
}
