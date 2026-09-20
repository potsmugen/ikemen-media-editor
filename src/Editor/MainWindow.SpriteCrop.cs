using Avalonia.Interactivity;
using Sff;

namespace Editor;

/// <summary>Crop to visible box, for one sprite or a selection. Each sprite crops to its own tightest box.</summary>
public partial class MainWindow
{
    /// <summary>A snapshot of one sprite's pixel-relevant fields, before and after a crop, for undo.</summary>
    private readonly record struct CropChange(
        Sprite Sprite,
        byte[]? BeforeIndexed, byte[]? BeforeDirect, int BeforeWidth, int BeforeHeight, int BeforeAxisX, int BeforeAxisY, bool BeforeDirty,
        byte[]? AfterIndexed, byte[]? AfterDirect, int AfterWidth, int AfterHeight, int AfterAxisX, int AfterAxisY);

    private static CropChange Snapshot(Sprite sprite) => new(sprite,
        sprite.Indexed?.ToArray(), sprite.Direct?.ToArray(), sprite.Width, sprite.Height, sprite.AxisX, sprite.AxisY, sprite.PixelsDirty,
        null, null, 0, 0, 0, 0);

    private static CropChange WithAfter(CropChange c) => c with
    {
        AfterIndexed = c.Sprite.Indexed?.ToArray(), AfterDirect = c.Sprite.Direct?.ToArray(),
        AfterWidth = c.Sprite.Width, AfterHeight = c.Sprite.Height, AfterAxisX = c.Sprite.AxisX, AfterAxisY = c.Sprite.AxisY,
    };

    private static void ApplyCrop(CropChange c, bool after)
    {
        c.Sprite.Indexed = (after ? c.AfterIndexed : c.BeforeIndexed)?.ToArray();
        c.Sprite.Direct = (after ? c.AfterDirect : c.BeforeDirect)?.ToArray();
        c.Sprite.Width = after ? c.AfterWidth : c.BeforeWidth;
        c.Sprite.Height = after ? c.AfterHeight : c.BeforeHeight;
        c.Sprite.AxisX = after ? c.AfterAxisX : c.BeforeAxisX;
        c.Sprite.AxisY = after ? c.AfterAxisY : c.BeforeAxisY;
        c.Sprite.PixelsDirty = after || c.BeforeDirty;
    }

    /// <summary>An axis move on a sprite that borrows a cropped/resized sprite's pixels, for undo.</summary>
    private readonly record struct AxisShift(Sprite Sprite, int BeforeX, int BeforeY, int AfterX, int AfterY);

    /// <summary>
    /// Moves the axis of every sprite borrowing target's pixels by (dx, dy), the
    /// way the target's own axis moved, so nothing shifts on screen and no link
    /// is lost (sprmaker -c does the same).
    /// </summary>
    private List<AxisShift> FollowTarget(Sprite target, int dx, int dy)
    {
        var shifts = new List<AxisShift>();
        if (_file is null || (dx == 0 && dy == 0)) return shifts;
        foreach (var s in _file.Sprites)
        {
            if (!ReferenceEquals(s.SharesWith, target)) continue;
            int bx = s.AxisX, by = s.AxisY;
            s.AxisX += dx;
            s.AxisY += dy;
            shifts.Add(new AxisShift(s, bx, by, s.AxisX, s.AxisY));
        }
        return shifts;
    }

    private static void ApplyShifts(List<AxisShift> shifts, bool after)
    {
        foreach (var x in shifts)
            (x.Sprite.AxisX, x.Sprite.AxisY) = after ? (x.AfterX, x.AfterY) : (x.BeforeX, x.BeforeY);
    }

    private async void OnResizeSpriteCanvas(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_current is not { HasPixels: true } sprite) return;

            // Several rows share one dialog answer; a single row keeps the original
            // path, with its own current canvas in the dialog.
            var selected = SelectedSpriteTargets();
            if (selected.Count > 1) { await ResizeSelectedCanvases(selected); return; }

            if (RefuseBorrowerEdit(sprite, "resize")) return;
            var choice = await Dialogs.PromptSpriteResize(this, sprite.Width, sprite.Height);
            if (choice is null) return;

            int newWidth = choice.Relative ? sprite.Width + choice.Width : choice.Width;
            int newHeight = choice.Relative ? sprite.Height + choice.Height : choice.Height;
            if (newWidth <= 0 || newHeight <= 0 || newWidth > 65535 || newHeight > 65535)
            {
                StatusText.Text = "The resized canvas must be between 1 and 65535 pixels in each dimension.";
                return;
            }
            var before = Snapshot(sprite);
            var result = SpriteCrop.Resize(sprite, newWidth, newHeight);
            if (!result.Changed)
            {
                StatusText.Text = "The canvas already has those dimensions.";
                return;
            }
            var change = WithAfter(before);
            var shifts = FollowTarget(sprite, result.OffsetX, result.OffsetY);
            _undo.Record("resize sprite canvas",
                () => { ApplyCrop(change, false); ApplyShifts(shifts, false); AfterEdit(); },
                () => { ApplyCrop(change, true); ApplyShifts(shifts, true); AfterEdit(); });
            MarkDirty();
            AfterEdit();
            StatusText.Text = $"Resized canvas to {newWidth}×{newHeight}."
                + (shifts.Count > 0 ? $" {shifts.Count} linked sprite(s) followed." : "");
        }
        catch (Exception ex)
        {
            await Failed("OnResizeSpriteCanvas", ex);
        }
    }

    /// <summary>
    /// Resizes every selected sprite's canvas from one dialog answer, as a single undo step.
    /// SpriteCrop.Resize pads or crops around each sprite's own center, so a relative change
    /// is measured against each sprite's own size. Linked sprites are skipped, as in a
    /// multi-selection crop.
    /// </summary>
    private async Task ResizeSelectedCanvases(List<Sprite> selected)
    {
        var choice = await Dialogs.PromptSpriteResize(this, null, null);
        if (choice is null || _file is null) return;

        if (!TryBeginBusy()) return;
        try { await ResizeSelectedCanvasesCore(selected, choice); }
        finally { EndBusy(); }
    }

    private async Task ResizeSelectedCanvasesCore(List<Sprite> selected, SpriteResizeChoices choice)
    {
        var selectedRows = SelectedRows();
        var changes = new List<CropChange>();
        var shifts = new List<AxisShift>();
        var linked = new List<Sprite>();
        var outOfRange = new List<Sprite>();
        int unchanged = 0;

        for (int i = 0; i < selected.Count; i++)
        {
            var sprite = selected[i];
            await BusyStep("Resizing", i, selected.Count, $"{sprite.Group},{sprite.Index}");
            if (sprite.SharesWith is { } t)
            {
                linked.Add(sprite);
                CrashLog.Error("resize", $"skipped: {sprite.Group},{sprite.Index} links to {t.Group},{t.Index}");
                continue;
            }
            int newWidth = choice.Relative ? sprite.Width + choice.Width : choice.Width;
            int newHeight = choice.Relative ? sprite.Height + choice.Height : choice.Height;
            if (newWidth < 1 || newHeight < 1 || newWidth > 65535 || newHeight > 65535) { outOfRange.Add(sprite); continue; }

            var before = Snapshot(sprite);
            var result = SpriteCrop.Resize(sprite, newWidth, newHeight);
            if (!result.Changed) { unchanged++; continue; }
            changes.Add(WithAfter(before));
            shifts.AddRange(FollowTarget(sprite, result.OffsetX, result.OffsetY));
        }

        if (changes.Count == 0)
        {
            StatusText.Text = outOfRange.Count > 0
                ? "Nothing to resize — that size is outside the 1 to 65535 range for every selected sprite."
                : linked.Count > 0
                    ? $"Nothing to resize — every selected sprite links to another sprite ({linked.Count} of {selected.Count})."
                    : "Nothing to resize — every selected sprite is already that size.";
            return;
        }

        void ApplyAll(bool after)
        {
            foreach (var c in changes) ApplyCrop(c, after);
            ApplyShifts(shifts, after);
            AfterEdit();
        }
        _undo.Record($"resize {changes.Count} canvas(s)", () => ApplyAll(false), () => ApplyAll(true));
        MarkDirty();
        AfterEdit();
        ApplySpriteRowSelection(selectedRows, suppressEvents: true);
        StatusText.Text = choice.Relative
            ? $"Resized {changes.Count} of {selected.Count} canvas(s) by X {choice.Width}, Y {choice.Height}"
            : $"Resized {changes.Count} of {selected.Count} canvas(s) to {choice.Width}×{choice.Height}"
            + (unchanged > 0 ? $"; {unchanged} already that size." : "")
            + (outOfRange.Count > 0 ? $"; {outOfRange.Count} outside the 1 to 65535 range." : "")
            + (linked.Count > 0 ? $"; skipped {linked.Count} linked sprite(s)." : "")
            + (shifts.Count > 0 ? $" {shifts.Count} linked sprite(s) followed." : ".");
    }

    /// <summary>
    /// Crops the whole selection, each sprite to its own tightest box, as one undo
    /// step. Linked sprites are skipped rather than refused: they have no pixels of
    /// their own, and FollowTarget moves their axis when the target is cropped.
    /// </summary>
    private async void CropSelectedSprites(List<Sprite> selected)
    {
        if (!TryBeginBusy()) return;
        try { await CropSelectedSpritesCore(selected); }
        catch (Exception ex) { await Failed("CropSelectedSprites", ex); }
        finally { EndBusy(); }
    }

    private async Task CropSelectedSpritesCore(List<Sprite> selected)
    {
        var selectedRows = SelectedRows();
        var changes = new List<CropChange>();
        var shifts = new List<AxisShift>();
        var linked = new List<Sprite>();
        int alreadyTight = 0;

        for (int i = 0; i < selected.Count; i++)
        {
            var sprite = selected[i];
            await BusyStep("Cropping", i, selected.Count, $"{sprite.Group},{sprite.Index}");
            if (sprite.SharesWith is { } t)
            {
                linked.Add(sprite);
                CrashLog.Error("crop", $"skipped: {sprite.Group},{sprite.Index} links to {t.Group},{t.Index}");
                continue;
            }
            var before = Snapshot(sprite);
            var result = SpriteCrop.Crop(sprite);
            if (!result.Changed) { alreadyTight++; continue; }
            changes.Add(WithAfter(before));
            shifts.AddRange(FollowTarget(sprite, -result.Left, -result.Top));
        }

        if (changes.Count == 0)
        {
            StatusText.Text = linked.Count > 0
                ? $"Nothing to crop — every selected sprite links to another sprite ({linked.Count} of {selected.Count})."
                : "Nothing to crop — every selected sprite is already tight, or entirely mask color.";
            return;
        }

        void ApplyAll(bool after)
        {
            foreach (var c in changes) ApplyCrop(c, after);
            ApplyShifts(shifts, after);
            AfterEdit();
        }
        _undo.Record($"crop {changes.Count} sprite(s)", () => ApplyAll(false), () => ApplyAll(true));
        MarkDirty();
        AfterEdit();
        ApplySpriteRowSelection(selectedRows, suppressEvents: true);
        StatusText.Text = $"Cropped {changes.Count} of {selected.Count} sprite(s)"
            + (alreadyTight > 0 ? $"; {alreadyTight} already tight." : "")
            + (linked.Count > 0 ? $"; skipped {linked.Count} linked sprite(s)." : "")
            + (shifts.Count > 0 ? $" {shifts.Count} linked sprite(s) followed." : ".");
    }

    private void OnCropSprite(object? sender, RoutedEventArgs e)
    {
        if (_current is null) return;

        // A single row takes the original path, message and all; only a real
        // multi-selection goes through the batch.
        var selected = SelectedSpriteTargets();
        if (selected.Count > 1) { CropSelectedSprites(selected); return; }

        var sprite = _current;
        if (RefuseBorrowerEdit(sprite, "crop")) return;
        var before = Snapshot(sprite);

        var result = SpriteCrop.Crop(sprite);
        if (!result.Changed)
        {
            StatusText.Text = "Nothing to crop — already tight, or the sprite is entirely mask color.";
            return;
        }
        var change = WithAfter(before);
        var shifts = FollowTarget(sprite, -result.Left, -result.Top);

        _undo.Record("crop sprite",
            () => { ApplyCrop(change, false); ApplyShifts(shifts, false); AfterEdit(); },
            () => { ApplyCrop(change, true); ApplyShifts(shifts, true); AfterEdit(); });
        MarkDirty();
        AfterEdit();
        StatusText.Text = $"Cropped {before.BeforeWidth}x{before.BeforeHeight} to {change.AfterWidth}x{change.AfterHeight} "
            + $"(trimmed {result.Left}px left, {result.Top}px top, {result.Right}px right, {result.Bottom}px bottom)."
            + (shifts.Count > 0 ? $" {shifts.Count} linked sprite(s) followed." : "");
    }
}
