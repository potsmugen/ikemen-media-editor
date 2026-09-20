using Avalonia.Interactivity;
using Sff;

namespace Editor;

/// <summary>Turns and mirrors sprites: one edit across the whole selection, as one undo step.
/// Reuses the snapshot and axis-following from MainWindow.SpriteCrop.cs, so a linked sprite
/// keeps up with the one it borrows pixels from, exactly as it does for a crop.</summary>
public partial class MainWindow
{
    private delegate SpriteRotate.Result Transform(Sprite sprite);

    private async void OnRotateSprite(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_current is not { HasPixels: true } sprite) return;

            var selected = SelectedSpriteTargets();
            if (selected.Count == 1 && RefuseBorrowerEdit(sprite, "rotate")) return;

            // One true-color sprite in the selection is enough to make blending unavailable
            // for all of them, so the dialog is told about the whole set.
            bool indexed = (selected.Count > 1 ? selected : [sprite]).Any(s => s.Indexed is not null);
            if (await Dialogs.PromptSpriteRotate(this, indexed) is not { } choice) return;

            if (choice.Degrees == 0) { StatusText.Text = "Nothing to change \u2014 the angle is 0."; return; }

            var what = $"Turned {choice.Degrees:0.##}\u00b0 {(choice.Clockwise ? "right" : "left")}";
            await EditSelection("rotate", "Rotating", selected.Count > 1 ? selected : [sprite], s => ApplyTurn(s, choice), what);
        }
        catch (Exception ex)
        {
            await Failed("OnRotateSprite", ex);
        }
    }

    private async void OnFlipSprite(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_current is not { HasPixels: true } sprite) return;
            if (RefuseBorrowerEdit(sprite, "flip")) return;
            if (await Dialogs.PromptSpriteFlip(this) is not { } choice) return;

            var selected = SelectedSpriteTargets();
            string what = choice.Horizontal && choice.Vertical ? "Flipped both ways"
                : choice.Horizontal ? "Flipped left to right" : "Flipped top to bottom";
            await EditSelection("flip", "Flipping", selected.Count > 1 ? selected : [sprite],
                s => SpriteRotate.Flip(s, choice.Horizontal, choice.Vertical, SpriteRotate.Pivot.Sprite), what);
        }
        catch (Exception ex)
        {
            await Failed("OnFlipSprite", ex);
        }
    }

    /// <summary>
    /// Turns the sprite. A whole quarter turn goes through the exact path whatever the
    /// sampling, since all three agree there and only the exact one lands the axis on whole
    /// pixels instead of rounding it.
    /// </summary>
    private static SpriteRotate.Result ApplyTurn(Sprite sprite, Dialogs.SpriteRotateChoice choice)
    {
        double degrees = choice.Clockwise ? choice.Degrees : -choice.Degrees;
        if (Math.Abs(choice.Degrees % 90) < 1e-9)
        {
            int quarters = (int)Math.Round(choice.Degrees / 90) % 4;
            return SpriteRotate.Rotate(sprite, choice.Clockwise ? quarters : -quarters, choice.Pivot);
        }

        // The canvas is whatever fits this sprite's own turn, and the offset depends on the
        // direction as well as the angle: clockwise and anticlockwise need different offsets
        // for the same canvas, and using the wrong one pushes half the sprite off the edge.
        // Fitting here rather than in the dialog is also what lets a multi-selection of
        // different sizes each get their own.
        return SpriteRotate.RotateFree(sprite, degrees, SpriteRotate.Fit(sprite.Width, sprite.Height, degrees),
            choice.Interpolation, choice.Pivot);
    }

    /// <summary>
    /// Applies one transform across the selection as a single undo step. Linked sprites are
    /// skipped rather than refused, as in a multi-selection crop: they have no pixels of
    /// their own, and FollowTarget moves their axis instead.
    /// </summary>
    private async Task EditSelection(string label, string progress, List<Sprite> targets, Transform transform, string what)
    {
        if (!TryBeginBusy()) return;
        try { await EditSelectionCore(label, progress, targets, transform, what); }
        finally { EndBusy(); }
    }

    private async Task EditSelectionCore(string label, string progress, List<Sprite> targets, Transform transform, string what)
    {
        var selectedRows = SelectedRows();
        var changes = new List<CropChange>();
        var shifts = new List<AxisShift>();
        var linked = new List<Sprite>();
        int unchanged = 0;
        SpriteRotate.Result? first = null;

        for (int i = 0; i < targets.Count; i++)
        {
            var sprite = targets[i];
            await BusyStep(progress, i, targets.Count, $"{sprite.Group},{sprite.Index}");
            if (sprite.SharesWith is { } t)
            {
                linked.Add(sprite);
                CrashLog.Error(label, $"skipped: {sprite.Group},{sprite.Index} links to {t.Group},{t.Index}");
                continue;
            }
            var before = Snapshot(sprite);
            var result = transform(sprite);
            if (!result.Changed) { unchanged++; continue; }
            first ??= result;
            changes.Add(WithAfter(before));
            shifts.AddRange(FollowTarget(sprite, result.AxisX - before.BeforeAxisX, result.AxisY - before.BeforeAxisY));
        }

        if (changes.Count == 0)
        {
            StatusText.Text = linked.Count > 0
                ? $"Nothing to change \u2014 every selected sprite links to another sprite ({linked.Count} of {targets.Count})."
                : "Nothing to change.";
            return;
        }

        void ApplyAll(bool after)
        {
            foreach (var c in changes) ApplyCrop(c, after);
            ApplyShifts(shifts, after);
            AfterEdit();
        }
        bool many = targets.Count > 1;
        _undo.Record(many ? $"{label} {changes.Count} sprite(s)" : $"{label} sprite",
            () => ApplyAll(false), () => ApplyAll(true));
        MarkDirty();
        AfterEdit();
        if (many) ApplySpriteRowSelection(selectedRows, suppressEvents: true);

        StatusText.Text = many
            ? $"{what} on {changes.Count} of {targets.Count} sprite(s)"
                + (unchanged > 0 ? $"; {unchanged} unchanged." : "")
                + (linked.Count > 0 ? $"; skipped {linked.Count} linked sprite(s)." : "")
                + (shifts.Count > 0 ? $" {shifts.Count} linked sprite(s) followed." : ".")
            : $"{what}: canvas {first!.Value.Width}\u00d7{first!.Value.Height}, "
                + $"axis at {first!.Value.AxisX},{first!.Value.AxisY}."
                + (shifts.Count > 0 ? $" {shifts.Count} linked sprite(s) followed." : "");
    }
}
