using Avalonia.Interactivity;
using Sff;

namespace Editor;

public partial class MainWindow
{
    private async void OnResizeSpritePixels(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_current is not { HasPixels: true } sprite) return;
            var selected = SelectedSpriteTargets();
            var targets = selected.Count > 1 ? selected : [sprite];
            bool indexed = targets.Any(s => s.Indexed is not null);
            int initialPercent = Settings.Checked(nameof(Settings.SpriteScalePercent), _settings.SpriteScalePercent,
                Settings.DefaultSpriteScalePercent, v => v >= 1);
            int minPercent = Settings.Checked(nameof(Settings.SpriteScaleMinPercent), _settings.SpriteScaleMinPercent,
                Settings.DefaultSpriteScaleMinPercent, v => v is >= 1 && v <= Settings.DefaultSpriteScaleMaxPercent);
            int maxPercent = Settings.Checked(nameof(Settings.SpriteScaleMaxPercent), _settings.SpriteScaleMaxPercent,
                Settings.DefaultSpriteScaleMaxPercent, v => v >= minPercent);
            initialPercent = Settings.Checked(nameof(Settings.SpriteScalePercent), initialPercent,
                Settings.DefaultSpriteScalePercent, v => v >= minPercent && v <= maxPercent);
            if (targets.Count == 1 && RefuseBorrowerEdit(sprite, "resize")) return;
            var choice = await Dialogs.PromptSpriteScale(this,
                targets.Count == 1 ? sprite.Width : null, targets.Count == 1 ? sprite.Height : null,
                indexed, initialPercent, minPercent, maxPercent);
            if (choice is null) return;
            await ScaleSelection("resize", "Resizing", targets, choice);
        }
        catch (Exception ex)
        {
            await Failed("OnResizeSpritePixels", ex);
        }
    }

    private async Task ScaleSelection(string label, string progress, List<Sprite> targets, SpriteScaleChoices choice)
    {
        if (!TryBeginBusy()) return;
        try { await ScaleSelectionCore(label, progress, targets, choice); }
        finally { EndBusy(); }
    }

    private async Task ScaleSelectionCore(string label, string progress, List<Sprite> targets, SpriteScaleChoices choice)
    {
        var selectedRows = SelectedRows();
        var changes = new List<CropChange>();
        var shifts = new List<AxisShift>();
        var linked = new List<Sprite>();
        var outOfRange = new List<Sprite>();
        int unchanged = 0;

        for (int i = 0; i < targets.Count; i++)
        {
            var sprite = targets[i];
            await BusyStep(progress, i, targets.Count, $"{sprite.Group},{sprite.Index}");
            if (sprite.SharesWith is { } target)
            {
                linked.Add(sprite);
                CrashLog.Error(label, $"skipped: {sprite.Group},{sprite.Index} links to {target.Group},{target.Index}");
                continue;
            }
            int width = choice.Percentage
                ? ScaleDimension(sprite.Width, choice.Width)
                : (int)choice.Width;
            int height = choice.Percentage
                ? ScaleDimension(sprite.Height, choice.Height)
                : (int)choice.Height;
            if (width is < 1 or > 65535 || height is < 1 or > 65535)
            {
                outOfRange.Add(sprite);
                continue;
            }

            var before = Snapshot(sprite);
            var result = SpriteScale.Resize(sprite, width, height, choice.Interpolation);
            if (!result.Changed) { unchanged++; continue; }
            changes.Add(WithAfter(before));
            shifts.AddRange(FollowTarget(sprite, result.AxisX - before.BeforeAxisX, result.AxisY - before.BeforeAxisY));
        }

        if (changes.Count == 0)
        {
            StatusText.Text = outOfRange.Count > 0
                ? "Nothing to resize — the output size is outside the 1 to 65535 range for every selected sprite."
                : linked.Count > 0
                    ? $"Nothing to resize — every selected sprite links to another sprite ({linked.Count} of {targets.Count})."
                    : "Nothing to resize — every selected sprite is already that size.";
            return;
        }

        void ApplyAll(bool after)
        {
            foreach (var change in changes) ApplyCrop(change, after);
            ApplyShifts(shifts, after);
            AfterEdit();
        }
        _undo.Record($"resize {changes.Count} sprite(s)", () => ApplyAll(false), () => ApplyAll(true));
        MarkDirty();
        AfterEdit();
        if (targets.Count > 1) ApplySpriteRowSelection(selectedRows, suppressEvents: true);
        StatusText.Text = $"Resized {changes.Count} of {targets.Count} sprite(s)"
            + (unchanged > 0 ? $"; {unchanged} already that size." : "")
            + (outOfRange.Count > 0 ? $"; {outOfRange.Count} outside the 1 to 65535 range." : "")
            + (linked.Count > 0 ? $"; skipped {linked.Count} linked sprite(s)." : "")
            + (shifts.Count > 0 ? $" {shifts.Count} linked sprite(s) followed." : ".");
    }

    private static int ScaleDimension(int dimension, double percent) =>
        (int)Math.Round(dimension * percent / 100, MidpointRounding.AwayFromZero);
}
