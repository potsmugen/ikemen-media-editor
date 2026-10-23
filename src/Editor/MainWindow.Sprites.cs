using System.Globalization;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sff;

namespace Editor;

public partial class MainWindow
{
    // ---- sprite list ------------------------------------------------------

    // Find Similar Sprites: which of the three named levels was last chosen this session,
    // and whether the same palette was required. Both start at their settings.json defaults.
    private const int SimilarityDefaultLevel = 1; // "Almost identical"
    private int? _similarLevel;
    private bool? _similarRequirePalette;

    /// <summary>The named levels Find Similar Sprites offers, strictest first, with the
    /// percentages each scans at read from settings.json.</summary>
    private List<SimilarityChoice> SimilarityLevels() =>
    [
        new("Identical", "Exact duplicates, the same test Link All Duplicate Sprites uses.",
            Settings.Checked(nameof(Settings.SpritesSection.SimilarExactPercent), _settings.Sprites.SimilarExactPercent, Settings.DefaultSimilarExact, v => v is >= 1 and <= 100)),
        new("Almost identical", "A stray pixel or two, or a small edit.",
            Settings.Checked(nameof(Settings.SpritesSection.SimilarNearPercent), _settings.Sprites.SimilarNearPercent, Settings.DefaultSimilarNear, v => v is >= 1 and <= 100)),
        new("Similar", "The same drawing, visibly reworked.",
            Settings.Checked(nameof(Settings.SpritesSection.SimilarLoosePercent), _settings.Sprites.SimilarLoosePercent, Settings.DefaultSimilarLoose, v => v is >= 1 and <= 100)),
    ];
    /// <summary>The open Similar sprites report (it isn't modal); reopening replaces it.</summary>
    private Window? _similarReport;

    /// <summary>Filters by group, or by group and number when a comma is typed. Returns true when
    /// the table's rows were replaced, false when they were refreshed where they were.</summary>
    /// <param name="keepScroll">After an edit: put the scroll position back when the rows are replaced
    /// (a fresh ItemsSource drops it), so adding a row doesn't move the list.</param>
    private bool ApplyFilter(bool keepScroll = false)
    {
        if (_file is null) return false;

        var text = (FilterBox.Text ?? "").Trim();
        var keep = _current;
        _visible = [];
        for (int i = 0; i < _file.Sprites.Count; i++)
        {
            if (text.Length == 0 || Matches(_file.Sprites[i], text)) _visible.Add(i);
        }

        // RebuildSpriteTable is what actually assigns SpriteList.ItemsSource now
        // (SpriteList is the visible table itself, not a hidden selection-only
        // list beside a separate hand-built Grid) — see MainWindow.SpriteTable.cs.
        // Suppression is scoped to the rebuild only; the SelectedIndex set below
        // fires or not exactly as the caller already decided. AfterEdit wants it
        // suppressed, so it can restore the real post-edit selection right after
        // without a visible flash to row 0 first (that flash — jump to 0, then to
        // the right row — was the actual bug, not which row ended up selected).
        // A direct filter-box edit isn't suppressed to begin with, so it still
        // fires normally and refreshes the view for the new top row.
        var scroller = keepScroll ? SpriteList.FindDescendantOfType<ScrollViewer>() : null;
        var offset = scroller?.Offset;
        bool wasSuppressed = _suppressEvents;
        _suppressEvents = true;
        bool replaced = RebuildSpriteTable();
        _suppressEvents = wasSuppressed;
        if (replaced && scroller is not null && offset is { } before)
        {
            SpriteList.UpdateLayout();
            double max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
            scroller.Offset = new Vector(before.X, Math.Clamp(before.Y, 0, max));
            SpriteList.UpdateLayout();
        }

        if (_visible.Count > 0)
        {
            // A rebuild replaces the row objects, so the selection has to be made again. When the
            // rows were refreshed in place the old selection still holds, and forcing row 0 here
            // would throw away a multi-selection on every single edit.
            // Keep the current sprite selected if the filter still shows it; otherwise fall back to row 0.
            int keepRow = keep is not null && _file is not null ? _visible.IndexOf(_file.PositionOf(keep)) : -1;
            if (replaced)
            {
                SelectOnlySpriteRow(keepRow >= 0 ? keepRow : 0);
                if (keepRow >= 0) CenterSpriteRow(keepRow);
            }
            // Row 0 was picked for the view, not clicked, so it must not
            // become the shift anchor: Shift should extend from the sprite in view instead.
            _spriteSelectionAnchor = -1;
        }
        else
        {
            ApplySpriteRowSelection([], suppressEvents: true);
            _spriteSelectionAnchor = -1;
            _current = null;
            SpriteCanvas.Show(null, null, false);
            RefreshAssignPaletteButton();
        }
        UpdateStatus(); // the selection event may not fire (suppressed, or same sprite kept)
        return replaced;
    }

    private static bool Matches(Sprite s, string text)
    {
        var parts = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return $"{s.Group}".StartsWith(parts[0]) && $"{s.Index}".StartsWith(parts[1]);
        return $"{s.Group}".StartsWith(parts[0]) || $"{s.Group},{s.Index}".StartsWith(parts[0]);
    }

    private void OnFilterChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        ApplyFilter();
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        // SpriteList is hidden now that the table replaced it, so Focus() would go nowhere —
        // just step the selection directly instead.
        if (e.Key is Key.Down or Key.Enter) { StepSprite(0); e.Handled = true; }
    }

    private async void OnSpriteSelected(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (_suppressEvents || _file is null) return;
            int row = SpriteList.SelectedIndex;
            if (row < 0 || row >= _visible.Count) return;
            bool picking = _pickingFor is not null;
            if (!await ActivateSpriteRow(row) && !picking
                && _paintBaselineSprite is { } pending)
            {
                _suppressEvents = true;
                SelectSprite(_file.PositionOf(pending));
                _suppressEvents = false;
            }
        }
        catch (Exception ex)
        {
            await Failed("OnSpriteSelected", ex);
        }
    }

    // ---- sprite linking ---------------------------------------------------

    private void BeginPickShare(Sprite sprite, int visibleRow)
    {
        if (_file is null) return;
        if (_pickingFor is not null) { CancelPick(restoreSelection: true); return; }

        _pickingFor = sprite;
        _pickReturnRow = visibleRow;
        StatusText.Text = "Choose a sprite to link to, or press Escape.";
    }

    private void UnshareSprite(Sprite sprite)
    {
        if (_file is null) return;
        if (sprite.SharesWith is not { } target)
        {
            CrashLog.Error("UnshareSprite", $"refused unlink of unlinked sprite {sprite.Group},{sprite.Index}");
            StatusText.Text = $"Sprite {sprite.Group},{sprite.Index} is not linked.";
            return;
        }
        var pixels = sprite.Indexed;
        var direct = sprite.Direct;
        Edit("unlink sprite",
            () => sprite.Unshare(),
            () =>
            {
                sprite.SharesWith = target;
                sprite.Indexed = pixels;
                sprite.Direct = direct;
                sprite.PixelsDirty = false;
            });
        StatusText.Text = "Now has its own copy of the pixels.";
    }

    /// <summary>Gives every linked sprite its own copy, as one undo step.</summary>
    private async void OnUnshareAllSprites(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_file is null) return;
            if (_pickingFor is not null) CancelPick(restoreSelection: true);
            var shared = _file.Sprites.Where(s => s.SharesWith is not null)
                .Select(s => (Sprite: s, Target: s.SharesWith!, s.Indexed, s.Direct, s.PixelsDirty)).ToList();
            if (shared.Count == 0) { StatusText.Text = "No linked sprites."; return; }

            Edit($"unlink {shared.Count} sprite(s)",
                () => { foreach (var x in shared) x.Sprite.Unshare(); },
                () =>
                {
                    foreach (var x in shared)
                    {
                        x.Sprite.SharesWith = x.Target;
                        x.Sprite.Indexed = x.Indexed;
                        x.Sprite.Direct = x.Direct;
                        x.Sprite.PixelsDirty = x.PixelsDirty;
                    }
                });
            CrashLog.Trace($"[sprites] unlinked {shared.Count} sprites");
            StatusText.Text = $"Unlinked {shared.Count} sprite(s); each now has its own copy of the pixels.";
        }
        catch (Exception ex)
        {
            await Failed("OnUnshareAllSprites", ex);
        }
    }

    /// <summary>Links every sprite whose pixels match an earlier one's to that first copy, as one undo step.</summary>
    private async void OnShareAllDuplicateSprites(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_file is null) return;
            if (_pickingFor is not null) CancelPick(restoreSelection: true);
            if (!await ResolvePendingPaintBeforeSwitch()) return; // paint edits the pixels being compared
            var file = _file;
            var sets = file.FindDuplicateSprites();
            int count = sets.Sum(set => set.Skip(1).Count(m => !ReferenceEquals(m.SharesWith, set[0])));
            if (count == 0) { StatusText.Text = "No duplicate sprites to link."; return; }

            bool v1 = SffVersions.IsV1(file.Version);
            if (!await Dialogs.Confirm(this, "Link All Duplicate Sprites",
                    $"Link {count} sprite(s) to {sets.Count} identical original(s)? Each linked sprite keeps its own group, number and axis"
                    + (v1 ? "" : " and palette") + ". Makes the saved file smaller. One undo step.",
                    "Link All"))
                return;
            if (!ReferenceEquals(_file, file)) return; // file changed while the dialog was open

            // v1 may drop palettes from the list: pending color adjustments go (as when
            // leaving them), and the palette override is re-found by object, not index.
            if (_editingPalette is not null) DiscardPendingColorAdjust();
            var before = file.SnapshotPixelLinks();
            PixelLinkSnapshot? after = null;
            int linked = 0;
            Edit($"link {count} duplicate sprite(s)",
                () =>
                {
                    var shown = OverriddenPalette();
                    if (after is null) { linked = file.ShareDuplicateSprites(sets); after = file.SnapshotPixelLinks(); }
                    else after.Restore(); // redo
                    _paletteOverride = shown is null ? -1 : file.Palettes.IndexOf(shown);
                },
                () =>
                {
                    var shown = OverriddenPalette();
                    before.Restore();
                    _paletteOverride = shown is null ? -1 : file.Palettes.IndexOf(shown);
                });
            CrashLog.Trace($"[sprites] linked {linked} duplicate sprites to {sets.Count} originals");
            StatusText.Text = $"Linked {linked} duplicate sprite(s) to {sets.Count} original(s).";
        }
        catch (Exception ex)
        {
            await Failed("OnShareAllDuplicateSprites", ex);
        }
    }

    /// <summary>Lists the sprites at least the chosen percentage the same as the current one. Report
    /// only: nothing in the file changes, and a click on a match selects that sprite. Linked sprites
    /// are left out, since one only ever matches the target it borrows from.</summary>
    private async void OnFindSimilarSprites(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_file is not { } file) { StatusText.Text = "Open a sprite file first."; return; }
            if (_current is not { } sprite)
            {
                StatusText.Text = "Select a sprite to compare the others against.";
                return;
            }
            if (sprite.DecodeError is not null)
            {
                StatusText.Text = $"Sprite {sprite.Group},{sprite.Index} could not be decoded, so there is nothing to compare.";
                CrashLog.Error("OnFindSimilarSprites", $"refused: {sprite.Group},{sprite.Index} has no pixels");
                return;
            }
            if (_pickingFor is not null) CancelPick(restoreSelection: true);
            if (!await ResolvePendingPaintBeforeSwitch()) return; // paint edits the pixels being compared

            // v1 keeps its colors in the palette, so a different palette there really is a
            // different picture; in v2 a sprite's palette is independent and only pixels matter.
            bool v1 = SffVersions.IsV1(file.Version);
            var levels = SimilarityLevels();
            var choices = await Dialogs.PromptSimilarSprites(this, levels,
                _similarLevel ?? SimilarityDefaultLevel,
                _similarRequirePalette ?? v1,
                $"{sprite.Group},{sprite.Index}");
            if (choices is null || !ReferenceEquals(_file, file) || !ReferenceEquals(_current, sprite)) return;
            string levelName = choices.Level.Label;
            _similarLevel = levels.IndexOf(choices.Level); // by position, so a custom percentage still selects its own row
            _similarRequirePalette = choices.RequireSamePalette;

            StatusText.Text = $"Finding sprites {levelName.ToLowerInvariant()} as {sprite.Group},{sprite.Index}\u2026";
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var matches = file.FindSimilarSprites(sprite, choices.Level.Percent, choices.RequireSamePalette);
            if (!ReferenceEquals(_file, file)) return;

            var lines = new List<(string, Action?)>
            {
                ($"Compared against sprite ({sprite.Width}\u00d7{sprite.Height} canvas,"
                    + $" {levelName}, ignoring empty borders{(choices.RequireSamePalette ? ", same palette required" : "")}).", null),
                ($"Sprite {sprite.Group},{sprite.Index}", (Action)(() => JumpToSprite(sprite))),
            };
            if (matches.Count == 0)
            {
                lines.Add(("", null));
                lines.Add(("No other sprite matched.", null));
            }
            else
            {
                lines.Add(("", null));
                foreach (var m in matches)
                    lines.Add(($"  Sprite {m.Sprite.Group},{m.Sprite.Index}: {m.Percent}%", (Action)(() => JumpToSprite(m.Sprite))));
            }

            _similarReport?.Close();
            _similarReport = Dialogs.ShowReport(this, "Similar Sprites", lines);
            _similarReport.Closed += (s, _) => { if (ReferenceEquals(s, _similarReport)) _similarReport = null; };
            StatusText.Text = matches.Count == 0
                ? $"No sprite is {levelName.ToLowerInvariant()} to {sprite.Group},{sprite.Index}."
                : $"Found {matches.Count} sprite(s) {levelName.ToLowerInvariant()} to {sprite.Group},{sprite.Index}.";
            CrashLog.Trace($"[sprites] similar-to {sprite.Group},{sprite.Index} at {levelName} ({choices.Level.Percent}%): {matches.Count} match(es)");
        }
        catch (Exception ex)
        {
            await Failed("OnFindSimilarSprites", ex);
        }
    }

    /// <summary>
    /// Pixel edits on a sprite that borrows another's pixels are refused, never
    /// silently unshared: true (with a status message and a log line) if so.
    /// </summary>
    private bool RefuseBorrowerEdit(Sprite sprite, string action)
    {
        if (sprite.SharesWith is not { } t) return false;
        ShowPopup("Linked Sprite", $"Can't {action} {sprite.Group},{sprite.Index}: it links to {t.Group},{t.Index}. Unlink it first.");
        CrashLog.Error(action, $"refused: {sprite.Group},{sprite.Index} links to {t.Group},{t.Index}");
        return true;
    }

    private Palette? OverriddenPalette() =>
        _file is { } f && _paletteOverride >= 0 && _paletteOverride < f.Palettes.Count ? f.Palettes[_paletteOverride] : null;

    private async Task CompletePick(Sprite target)
    {
        var sprite = _pickingFor;
        CancelPick(restoreSelection: true);
        if (sprite is null || _file is null) return;

        await ShareSpriteWith(sprite, target);
    }

    private async Task ShareSpriteWith(Sprite sprite, Sprite target)
    {
        if (_file is not { } file) return;
        if (!file.Sprites.Contains(sprite) || !file.Sprites.Contains(target))
        {
            CrashLog.Error("Sprite linking", "refused source or target no longer in the sprite list");
            return;
        }
        if (ReferenceEquals(sprite, target))
        {
            StatusText.Text = "A sprite cannot link to itself.";
            return;
        }
        if (file.PositionOf(target) > file.PositionOf(sprite))
        {
            CrashLog.Error("Sprite linking", $"refused {sprite.Group},{sprite.Index} to later sprite {target.Group},{target.Index}");
            await Dialogs.ShowMessage(this, "Cannot Link to Later Sprite",
                $"Sprite {target.Group},{target.Index} appears after {sprite.Group},{sprite.Index} in the sprite list. Choose an earlier sprite.");
            return;
        }
        if (ReferenceEquals(sprite.SharesWith, target))
        {
            StatusText.Text = $"{sprite.Group},{sprite.Index} is already linked to that sprite.";
            return;
        }
        var before = (sprite.SharesWith, sprite.Indexed, sprite.Direct, sprite.Width, sprite.Height, sprite.PixelsDirty);
        if (!file.Share(sprite, target))
        {
            StatusText.Text = "That would make the sprite links form a loop.";
            return;
        }
        var after = (sprite.SharesWith, sprite.Indexed, sprite.Direct, sprite.Width, sprite.Height, sprite.PixelsDirty);
        _undo.Record("link sprite",
            () => { Restore(sprite, before); AfterEdit(); },
            () => { Restore(sprite, after); AfterEdit(); });
        MarkDirty();
        AfterEdit();
        StatusText.Text = $"Linked to {target.Group},{target.Index}.";
    }

    private static void Restore(Sprite s,
        (Sprite? Shares, byte[]? Indexed, byte[]? Direct, int W, int H, bool Dirty) v)
    {
        s.SharesWith = v.Shares;
        s.Indexed = v.Indexed;
        s.Direct = v.Direct;
        s.Width = v.W;
        s.Height = v.H;
        s.PixelsDirty = v.Dirty;
    }

    /// <summary>Everything OnReplaceSprite's own undo snapshot captures, bundled so a
    /// bulk operation (Import All Sprites + Palettes) can take one of these per sprite
    /// and fold many into a single undo/redo closure instead of one Edit()/Record() each.</summary>
    private readonly record struct SpriteSnapshot(
        Sprite? Shares, byte[]? Indexed, byte[]? Direct, int Width, int Height, bool Dirty,
        Palette? Palette, SpriteFormat SrcFormat, byte SrcDepth, int AxisX, int AxisY)
    {
        public static SpriteSnapshot Of(Sprite s) =>
            new(s.SharesWith, s.Indexed, s.Direct, s.Width, s.Height, s.PixelsDirty,
                s.Palette, s.SrcFormat, s.SrcDepth, s.AxisX, s.AxisY);
    }

    private static void RestoreSpriteSnapshot(Sprite s, SpriteSnapshot v)
    {
        Restore(s, (v.Shares, v.Indexed, v.Direct, v.Width, v.Height, v.Dirty));
        s.Palette = v.Palette;
        s.SrcFormat = v.SrcFormat;
        s.SrcDepth = v.SrcDepth;
        s.AxisX = v.AxisX;
        s.AxisY = v.AxisY;
    }

    /// <summary>
    /// Headless version of what OnReplaceSprite does to one sprite — no dialogs, no
    /// per-file confirmation — for bulk import. Keeps the sprite's existing palette and
    /// its pixel indexes when the image is indexed (what our own exported PNGs always
    /// are); an indexed image against a sprite with no palette yet takes its own
    /// embedded one instead (same as "own palette" import); a true-color image needs
    /// SFF 2.0.1.0, reported as an error rather than silently written if the open file
    /// predates that. Returns a before/after snapshot so the caller can build one
    /// composite undo step out of many of these, and null Error on success.
    /// </summary>
    private (SpriteSnapshot Before, SpriteSnapshot After, string? Error) ReplaceSpriteHeadless(Sprite sprite, byte[] image, Palette? palette = null)
    {
        if (_file is null) { var s0 = SpriteSnapshot.Of(sprite); return (s0, s0, "no file open"); }
        var before = SpriteSnapshot.Of(sprite);
        bool indexed = ImageImport.ImageIsIndexed(image);

        ImportPlan plan;
        if (!indexed)
        {
            if (!SffVersions.SupportsPng(_file.Version))
                return (before, before, "true-color image, but this file's SFF version doesn't support 32-bit sprites");
            plan = ImportPlan.TrueColor32();
        }
        else if ((palette ?? sprite.Palette) is { } pal)
        {
            plan = ImportPlan.Given(pal, keepIndexes: true);
        }
        else
        {
            plan = ImportPlan.Own();
        }

        try
        {
            ImageImport.ReplaceImage(sprite, image, plan);
        }
        catch (Exception ex)
        {
            // ReplaceImage may have half-written the sprite before throwing; put it back.
            CrashLog.Write($"ReplaceSpriteHeadless {sprite.Group},{sprite.Index}", ex);
            RestoreSpriteSnapshot(sprite, before);
            return (before, before, ex.Message);
        }
        if (!plan.TrueColor && plan.OwnPalette && sprite.Palette is { } newPal)
        {
            newPal.Group = sprite.Group;
            newPal.Index = NextFreePaletteIndex(sprite.Group);
        }
        _file.EnsurePalette(sprite.Palette);

        var after = SpriteSnapshot.Of(sprite);
        return (before, after, null);
    }

    /// <summary>
    /// The one place a pixel-sharing pick session actually ends, one way or
    /// another — canceled here, or handed to CompletePick which calls this
    /// first. Always safe to call even when nothing is being picked.
    /// </summary>
    private void CancelPick(bool restoreSelection)
    {
        if (_pickingFor is null) return;
        _pickingFor = null;
        if (restoreSelection && _pickReturnRow >= 0 && _pickReturnRow < _visible.Count)
        {
            SelectOnlySpriteRow(_pickReturnRow, suppressEvents: true);
        }
        _pickReturnRow = -1;
    }

    private void SelectSprite(int spriteIndex)
    {
        int row = _visible.IndexOf(spriteIndex);
        if (row < 0) { FilterBox.Text = ""; row = _visible.IndexOf(spriteIndex); }
        if (row < 0) return;
        SelectOnlySpriteRow(row);
        CenterSpriteRow(row);
    }

    /// <summary>Scrolls the sprite table so the row sits in the middle of the visible area,
    /// clamped to the ends of the list. A row already fully in view stays where it is.</summary>
    private void CenterSpriteRow(int row)
    {
        if (SpriteList.FindDescendantOfType<ScrollViewer>() is { } view && SpriteList.ContainerFromIndex(row) is Control shown
            && shown.TranslatePoint(new Point(0, 0), view) is { } at && at.Y >= 0 && at.Y + shown.Bounds.Height <= view.Viewport.Height)
            return;
        // Bring the row into view first so its container is realized, then center it.
        SpriteList.ScrollIntoView(row);
        SpriteList.UpdateLayout();
        if (SpriteList.ContainerFromIndex(row) is not Control item) return;
        if (SpriteList.FindDescendantOfType<ScrollViewer>() is not { } scroller) return;
        if (item.TranslatePoint(new Point(0, 0), scroller) is not { } top) return;

        double target = scroller.Offset.Y + top.Y + item.Bounds.Height / 2 - scroller.Viewport.Height / 2;
        double max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(target, 0, max));
        SpriteList.UpdateLayout();
    }

    // ---- editing ----------------------------------------------------------

    private static void Apply(Sprite s, (int G, int N, int X, int Y) v) =>
        (s.Group, s.Index, s.AxisX, s.AxisY) = v;

    private void OnAxisDragStarted()
    {
        if (_current is not null)
            _axisBeforeDrag = (_current.Group, _current.Index, _current.AxisX, _current.AxisY);
    }

    private void OnAxisDragFinished()
    {
        if (_current is null || _axisBeforeDrag is not { } before) return;
        _axisBeforeDrag = null;

        var sprite = _current;
        var after = (sprite.Group, sprite.Index, sprite.AxisX, sprite.AxisY);
        if (before == after) return;

        // The drag already moved it, so record the reversal rather than re-applying.
        _undo.Record("axis move", () => { Apply(sprite, before); AfterEdit(); },
                                  () => { Apply(sprite, after); AfterEdit(); });
        MarkDirty();
    }

    private void OnAxisDragged()
    {
        if (_current is null) return;
        // The canvas drag writes the sprite's fields directly, bypassing the
        // spinners' own bounds, so this is where a normal-mode s16 limit lives.
        // Clamping here also keeps OnAxisDragFinished's before/after pair
        // (and so the undo record) to values the file can actually hold.
        _current.AxisX = Math.Clamp(_current.AxisX, SpriteAxisMin, SpriteAxisMax);
        _current.AxisY = Math.Clamp(_current.AxisY, SpriteAxisMin, SpriteAxisMax);
        UpdateSpriteTableAxes(_current);
        MarkDirty();
        UpdateStatus();
    }

    private async void OnShiftSpriteOffsets(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_file is not { } file) return;
            var selected = SelectedSpriteTargets();
            if (selected.Count == 0) return;

            long minX = Math.Max((long)int.MinValue, selected.Max(sprite => (long)SpriteAxisMin - sprite.AxisX));
            long maxX = Math.Min((long)int.MaxValue, selected.Min(sprite => (long)SpriteAxisMax - sprite.AxisX));
            long minY = Math.Max((long)int.MinValue, selected.Max(sprite => (long)SpriteAxisMin - sprite.AxisY));
            long maxY = Math.Min((long)int.MaxValue, selected.Min(sprite => (long)SpriteAxisMax - sprite.AxisY));
            if (minX > maxX || minY > maxY)
            {
                await Dialogs.ShowMessage(this, "Can't Shift Offsets", "The selected sprites cannot all be shifted into the permitted axis range.");
                return;
            }

            var shift = await Dialogs.PromptSpriteOffsetShift(this,
                (int)minX, (int)maxX, (int)minY, (int)maxY);
            if (shift is null || !ReferenceEquals(_file, file)) return;
            if (shift.X == 0 && shift.Y == 0) return;

            var before = selected.Select(sprite => (Sprite: sprite, X: sprite.AxisX, Y: sprite.AxisY)).ToList();
            var after = before.Select(item => (item.Sprite, X: item.X + shift.X, Y: item.Y + shift.Y)).ToList();
            void ApplyAxes(List<(Sprite Sprite, int X, int Y)> axes)
            {
                foreach (var item in axes) (item.Sprite.AxisX, item.Sprite.AxisY) = (item.X, item.Y);
                UpdateSpriteTableAxes(selected);
                SpriteCanvas.InvalidateVisual();
                UpdateStatus();
            }
            _undo.Do($"shift offsets for {selected.Count} sprites",
                () => ApplyAxes(after), () => ApplyAxes(before));
            MarkDirty();
            StatusText.Text = $"Shifted {selected.Count} sprite(s) by X {shift.X}, Y {shift.Y}.";
        }
        catch (Exception ex)
        {
            await Failed("OnShiftSpriteOffsets", ex);
        }
    }

    private async void OnSetSpriteAxis(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_file is not { } file) return;
            var selected = SelectedSpriteTargets();
            if (selected.Count == 0) return;

            var position = await Dialogs.PromptSpriteAxisPosition(this);
            if (position is null || !ReferenceEquals(_file, file)) return;
            var positionLabel = position.Value switch
            {
                SpriteAxisPosition.TopLeft => "Top Left",
                SpriteAxisPosition.TopCenter => "Top Center",
                SpriteAxisPosition.TopRight => "Top Right",
                SpriteAxisPosition.MiddleLeft => "Middle Left",
                SpriteAxisPosition.Center => "Center",
                SpriteAxisPosition.MiddleRight => "Middle Right",
                SpriteAxisPosition.BottomLeft => "Bottom Left",
                SpriteAxisPosition.BottomCenter => "Bottom Center",
                SpriteAxisPosition.BottomRight => "Bottom Right",
                _ => throw new ArgumentOutOfRangeException(nameof(position)),
            };

            (int X, int Y) Coordinates(Sprite sprite) => position.Value switch
            {
                SpriteAxisPosition.TopLeft => (0, 0),
                SpriteAxisPosition.TopCenter => (sprite.Width / 2, 0),
                SpriteAxisPosition.TopRight => (sprite.Width, 0),
                SpriteAxisPosition.MiddleLeft => (0, sprite.Height / 2),
                SpriteAxisPosition.Center => (sprite.Width / 2, sprite.Height / 2),
                SpriteAxisPosition.MiddleRight => (sprite.Width, sprite.Height / 2),
                SpriteAxisPosition.BottomLeft => (0, sprite.Height),
                SpriteAxisPosition.BottomCenter => (sprite.Width / 2, sprite.Height),
                SpriteAxisPosition.BottomRight => (sprite.Width, sprite.Height),
                _ => throw new ArgumentOutOfRangeException(nameof(position)),
            };

            var before = selected.Select(sprite => (Sprite: sprite, X: sprite.AxisX, Y: sprite.AxisY)).ToList();
            var after = before.Select(item =>
            {
                var coordinates = Coordinates(item.Sprite);
                return (item.Sprite, X: coordinates.X, Y: coordinates.Y);
            }).ToList();
            var outOfRange = after.Where(item => item.X < SpriteAxisMin || item.X > SpriteAxisMax
                || item.Y < SpriteAxisMin || item.Y > SpriteAxisMax).ToList();
            if (outOfRange.Count > 0)
            {
                CrashLog.Error("Set Sprite Axis", $"refused position {positionLabel} for sprites outside the permitted axis range: "
                    + string.Join(", ", outOfRange.Select(item => $"{item.Sprite.Group},{item.Sprite.Index} ({item.X},{item.Y})")));
                await Dialogs.ShowMessage(this, "Can't Set Axis", $"Can't set axis position: {outOfRange.Count} selected sprite(s) exceed the permitted axis range.");
                return;
            }
            if (before.SequenceEqual(after)) return;

            void ApplyAxes(List<(Sprite Sprite, int X, int Y)> axes)
            {
                foreach (var item in axes) (item.Sprite.AxisX, item.Sprite.AxisY) = (item.X, item.Y);
                UpdateSpriteTableAxes(selected);
                SpriteCanvas.InvalidateVisual();
                UpdateStatus();
            }
            _undo.Do($"set axis to {positionLabel.ToLowerInvariant()} for {selected.Count} sprites",
                () => ApplyAxes(after), () => ApplyAxes(before));
            MarkDirty();
            StatusText.Text = $"Set axis to {positionLabel} for {selected.Count} sprite(s).";
        }
        catch (Exception ex)
        {
            await Failed("OnSetSpriteAxis", ex);
        }
    }


    private async void OnAssignPalette(object? sender, RoutedEventArgs e)
    {
        if (_file is null || _paletteOverride < 0 || _paletteOverride >= _file.Palettes.Count) return;

        try
        {
            var selected = SelectedSpriteTargets();
            if (selected.Count == 0) return;
            var chosen = _file.Palettes[_paletteOverride];
            if (IsTempPalette(chosen))
            {
                if (!await Dialogs.Confirm(this, "Move Temp Palette",
                        "Applying this temp palette moves it into the SFF file.", "Apply"))
                    return;
            }
            else if (chosen.Source == PaletteSource.External && !await Dialogs.Confirm(this, "Import External Palette",
                    "Applying this external palette imports a copy of it into the SFF file.", "Apply"))
                return;
            if (selected.Count > 1)
            {
                if (!await AssignPaletteToSelectedSprites(chosen, selected)) return;
                _paletteOverride = -1;
                PaletteBox.SelectedIndex = 0;
                return;
            }
            if (!await AssignSpritePalette(selected[0], chosen)) return;
            _paletteOverride = -1;
            PaletteBox.SelectedIndex = 0;
        }
        catch (Exception ex) { await Failed("OnAssignPalette", ex); }
    }

    /// <summary>
    /// Pixel state around a palette mapping: an indexed remap, or a direct-color
    /// sprite converted to indexed. AfterIndexes is null when nothing changed.
    /// </summary>
    private sealed record PixelMapping(
        bool Confirmed, byte[]? BeforeIndexed, byte[]? BeforeDirect,
        byte[]? AfterIndexes, bool PixelsDirtyBefore)
    {
        public static PixelMapping Canceled(Sprite sprite) =>
            new(false, sprite.Indexed, sprite.Direct, null, sprite.PixelsDirty);

        public static PixelMapping Unchanged(Sprite sprite) =>
            new(true, sprite.Indexed, sprite.Direct, null, sprite.PixelsDirty);

        public void Apply(Sprite sprite)
        {
            if (AfterIndexes is null) return;
            sprite.Indexed = AfterIndexes;
            sprite.Direct = null;
            sprite.PixelsDirty = true;
        }

        public void Revert(Sprite sprite)
        {
            if (AfterIndexes is null) return;
            sprite.Indexed = BeforeIndexed;
            sprite.Direct = BeforeDirect;
            sprite.PixelsDirty = PixelsDirtyBefore;
        }
    }

    private async Task<PixelMapping>
        PromptPaletteIndexMapping(Sprite sprite, Palette? sourcePalette, Rgba[] targetColors, bool v1, Palette? sharedPalette)
    {
        bool hasBorrowers = _file?.Sprites.Any(other => ReferenceEquals(other.SharesWith, sprite)) == true;
        bool direct = sprite.Indexed is null && sprite.Direct is not null;
        string? disabledReason = sprite.SharesWith is not null
            ? "Unlink this sprite before remapping its pixels."
            : hasBorrowers
                ? "Unlink sprites that borrow from this one before remapping its pixels."
                : sprite.Indexed is null && sprite.Direct is null
                    ? "This sprite has no pixels to remap."
                    : sprite.Indexed is not null && sourcePalette is null
                        ? "This sprite has no current palette to map from."
                        : null;
        string? notice = null;
        if (v1 && sharedPalette is not null && _file is { } file
            && file.Sprites.Any(other => !ReferenceEquals(other, sprite) && ReferenceEquals(other.Palette, sharedPalette)))
            notice = "Other sprites using this SFFv1 palette keep their indexes and may look different.";

        bool allowNearest = disabledReason is null;
        bool allowKeep = sprite.Indexed is not null || !allowNearest;
        var choice = await Dialogs.PromptPaletteIndexMapping(this, allowNearest, disabledReason, notice,
            allowKeep: allowKeep, keepDisabledReason: allowKeep ? null : "This sprite has no indexes to keep.");
        if (choice is null) return PixelMapping.Canceled(sprite);
        if (choice != ImportIndexChoice.Nearest) return PixelMapping.Unchanged(sprite);

        if (direct)
            return new PixelMapping(true, null, sprite.Direct,
                ImageImport.MatchToPalette(sprite.Direct!, targetColors), sprite.PixelsDirty);

        var before = sprite.Indexed!;
        var after = ImageImport.RemapIndexes(before, sourcePalette!, targetColors);
        if (after.AsSpan().SequenceEqual(before)) return PixelMapping.Unchanged(sprite);
        return new PixelMapping(true, before, null, after, sprite.PixelsDirty);
    }

    private async void OnImportPaletteIntoSprite(object? sender, RoutedEventArgs e)
    {
        if (_file is null) return;
        try
        {
            var selected = SelectedSpriteTargets();
            if (selected.Count == 0) return;
            if (selected.Count > 1)
            {
                await ImportPaletteIntoSelectedSprites(selected);
                return;
            }
            var file = _file;
            var sprite = selected[0];
            if (sprite.Indexed is null && sprite.Direct is null)
            {
                await Dialogs.ShowMessage(this, "Cannot Import Palette", "This sprite has no pixels for a palette to map.");
                return;
            }
            var current = sprite.Palette;
            if (sprite.Indexed is not null && current is null)
            {
                await Dialogs.ShowMessage(this, "Cannot Import Palette", "This sprite has no palette. Give it a palette first.");
                return;
            }
            bool v1 = SffVersions.IsV1(file.Version);
            if (v1 && sprite.SharesWith is not null)
            {
                await Dialogs.ShowMessage(this, "Cannot Import Palette", "This sprite links to another sprite and cannot carry its own palette. Unlink it first.");
                return;
            }
            if (v1 && (current!.Source != PaletteSource.Embedded || file.PaletteCanBeMadeUnique(sprite)))
            {
                await Dialogs.ShowMessage(this, "Cannot Import Palette", "This sprite still uses a palette inherited from another sprite. Set its Palette field to Own first, then import the file.");
                return;
            }

            var files = await PickOpen(new FilePickerOpenOptions
            {
                Title = v1 ? "Import palette into SFFv1 sprite" : "Import palette into SFFv2 sprite",
                AllowMultiple = false,
                FileTypeFilter = [PaletteFilesType],
            });
            if (files.Count == 0) return;
            if (files[0].TryGetLocalPath() is not { } path)
            {
                CrashLog.Error("OnImportPaletteIntoSprite", $"{files[0].Name}: selected file has no local path");
                await Dialogs.ShowMessage(this, "Could Not Import Palette", "The selected file has no local path.");
                return;
            }

            Palette imported;
            try { imported = PaletteFileImport.Read(path); }
            catch (Exception ex)
            {
                CrashLog.Write($"OnImportPaletteIntoSprite {path}", ex);
                await Dialogs.ShowMessage(this, "Could Not Import Palette", $"{Path.GetFileName(path)}: {ex.Message}");
                return;
            }

            var prepared = await PreparePaletteForSprite(sprite, imported.Colors, v1);
            if (prepared is null) return;
            imported.Colors = prepared.Value.Colors;
            bool droppedAlpha = prepared.Value.DroppedAlpha;
            var mapping = await PromptPaletteIndexMapping(sprite, current, imported.Colors, v1, current);
            if (!mapping.Confirmed) return;
            if (current is null && mapping.AfterIndexes is null)
            {
                StatusText.Text = "This sprite is true color; there are no indexes to keep.";
                return;
            }

            if (v1)
            {
                var before = (Rgba[])current!.Colors.Clone();
                var after = (Rgba[])imported.Colors.Clone();
                Edit("import palette",
                    () =>
                    {
                        current!.Colors = after;
                        mapping.Apply(sprite);
                    },
                    () =>
                    {
                        current!.Colors = before;
                        mapping.Revert(sprite);
                    });
                StatusText.Text = $"Imported {Path.GetFileName(path)} into sprite {sprite.Group},{sprite.Index}'s own SFFv1 palette{(droppedAlpha ? " without alpha" : "")}.";
            }
            else
            {
                int group;
                int index;
                if (current is null)
                {
                    var key = await Dialogs.PromptNewPaletteKey(this, sprite.Group, NextFreePaletteIndex(sprite.Group), SpriteFieldMax);
                    if (key is null) return;
                    (group, index) = key.Value;
                }
                else
                {
                    group = current.Group;
                    index = NextFreePaletteIndex(group);
                }
                var added = new Palette
                {
                    Group = group,
                    Index = index,
                    Colors = (Rgba[])imported.Colors.Clone(),
                    Source = PaletteSource.Embedded,
                };
                Edit("import palette",
                    () =>
                    {
                        file.Palettes.Add(added);
                        sprite.Palette = added;
                        mapping.Apply(sprite);
                    },
                    () =>
                    {
                        sprite.Palette = current;
                        file.Palettes.Remove(added);
                        mapping.Revert(sprite);
                    });
                StatusText.Text = $"Imported {Path.GetFileName(path)} as palette {group},{added.Index} for sprite {sprite.Group},{sprite.Index}.";
            }
        }
        catch (Exception ex)
        {
            await Failed("OnImportPaletteIntoSprite", ex);
        }
    }

    /// <summary>
    /// Points a sprite at a different palette. In v2 a shared Palette is a
    /// deliberate resource reference, so only this one sprite is reassigned.
    /// In v1 two sprites sharing one Palette object reflects a real on-disk
    /// relationship (see SffVersions.IsV1): rather than repointing this one
    /// sprite at a different object, overwrite the colors of the object it
    /// already shares with the others. They inherit the change for free,
    /// the same way editing a palette's colors already works elsewhere —
    /// no need to go find and repoint every sprite that shares it.
    /// </summary>
    private async Task<bool> AssignSpritePalette(Sprite sprite, Palette chosen)
    {
        if (_file is null) return false;
        var file = _file;
        var current = sprite.Palette;
        if (ReferenceEquals(current, chosen) && chosen.Source != PaletteSource.External) return true;

        if (chosen.Source == PaletteSource.External && SffVersions.IsV1(file.Version))
        {
            if (current is null || current.Source != PaletteSource.Embedded || file.PaletteCanBeMadeUnique(sprite))
            {
                await Dialogs.ShowMessage(this, "Palette", "Set this sprite's Palette field to Own before using an external palette in SFFv1.");
                return false;
            }
            var prepared = await PreparePaletteForSprite(sprite, chosen.Colors, v1: true);
            if (prepared is null) return false;
            var mapping = await PromptPaletteIndexMapping(sprite, current, prepared.Value.Colors,
                v1: true, sharedPalette: current);
            if (!mapping.Confirmed) return false;
            var before = (Rgba[])current.Colors.Clone();
            var after = prepared.Value.Colors;
            var (dropTemp, restoreTemp) = TempPaletteLeavesList(file, chosen);
            string label = PaletteDisplayName(file, chosen);
            Edit("use external palette",
                () =>
                {
                    current.Colors = after;
                    mapping.Apply(sprite);
                    dropTemp();
                },
                () =>
                {
                    restoreTemp();
                    current.Colors = before;
                    mapping.Revert(sprite);
                });
            StatusText.Text = $"Copied {label} into sprite {sprite.Group},{sprite.Index}'s own SFFv1 palette{(prepared.Value.DroppedAlpha ? " without alpha" : "")}.";
            return true;
        }

        if (chosen.Source == PaletteSource.External)
        {
            var prepared = await PreparePaletteForSprite(sprite, chosen.Colors, v1: SffVersions.IsV1(file.Version));
            if (prepared is null) return false;
            var mapping = await PromptPaletteIndexMapping(sprite, current, prepared.Value.Colors,
                v1: SffVersions.IsV1(file.Version), sharedPalette: current);
            if (!mapping.Confirmed) return false;
            if (MovesIntoSff(file, chosen))
            {
                string label = PaletteDisplayName(file, chosen);
                var (moveIn, moveOut, movedIndex) = MoveTempPaletteIntoSff(file, chosen, prepared.Value.Colors);
                Edit("move temp palette into sff",
                    () =>
                    {
                        moveIn();
                        sprite.Palette = chosen;
                        mapping.Apply(sprite);
                    },
                    () =>
                    {
                        sprite.Palette = current;
                        moveOut();
                        mapping.Revert(sprite);
                    });
                StatusText.Text = $"Moved {label} into the SFF as palette {chosen.Group},{movedIndex} for sprite {sprite.Group},{sprite.Index}{(prepared.Value.DroppedAlpha ? " without alpha" : "")}.";
                return true;
            }
            int group = chosen.Group;
            var embedded = new Palette
            {
                Group = group,
                Index = NextFreePaletteIndex(group),
                Colors = prepared.Value.Colors,
                Source = PaletteSource.Embedded,
            };
            Edit("use external palette",
                () =>
                {
                    file.Palettes.Add(embedded);
                    sprite.Palette = embedded;
                    mapping.Apply(sprite);
                },
                () =>
                {
                    sprite.Palette = current;
                    file.Palettes.Remove(embedded);
                    mapping.Revert(sprite);
                });
            StatusText.Text = $"Copied {PaletteDisplayName(file, chosen)} into SFF palette {group},{embedded.Index} for sprite {sprite.Group},{sprite.Index}{(prepared.Value.DroppedAlpha ? " without alpha" : "")}.";
            return true;
        }

        if (SffVersions.IsV1(file.Version) && current is not null)
        {
            var mapping = await PromptPaletteIndexMapping(sprite, current, chosen.Colors,
                v1: true, sharedPalette: current);
            if (!mapping.Confirmed) return false;
            var before = (Rgba[])current.Colors.Clone();
            var after = (Rgba[])chosen.Colors.Clone();
            Edit("palette change",
                () =>
                {
                    current.Colors = after;
                    mapping.Apply(sprite);
                },
                () =>
                {
                    current.Colors = before;
                    mapping.Revert(sprite);
                });
            // This path bypasses RefreshPaletteViews, so mark the external palette here.
            if (current.Source == PaletteSource.External) current.Dirty = true;
            StatusText.Text = $"{sprite.Group},{sprite.Index}'s palette colors now match {PaletteDisplayName(file, chosen)} — every sprite using it updates too.";
            return true;
        }

        var paletteMapping = await PromptPaletteIndexMapping(sprite, current, chosen.Colors,
            v1: false, sharedPalette: null);
        if (!paletteMapping.Confirmed) return false;
        Edit("palette change",
            () =>
            {
                sprite.Palette = chosen;
                paletteMapping.Apply(sprite);
            },
            () =>
            {
                sprite.Palette = current;
                paletteMapping.Revert(sprite);
            });
        StatusText.Text = $"{sprite.Group},{sprite.Index} now uses palette {PaletteDisplayName(file, chosen)}.";
        return true;
    }

    private static bool HasSffV1AlphaLoss(Rgba[] colors) =>
        colors.Skip(1).Any(color => color.A != 255);

    private async Task<(Rgba[] Colors, bool DroppedAlpha)?> PreparePaletteForSprite(Sprite sprite, Rgba[] colors, bool v1)
    {
        var prepared = (Rgba[])colors.Clone();
        bool droppedAlpha = v1 && HasSffV1AlphaLoss(prepared);
        if (droppedAlpha)
        {
            if (!await Dialogs.PromptSffV1AlphaLoss(this)) return null;
            prepared = WithoutSffV1Alpha(prepared);
        }

        int needed = sprite.Indexed is { Length: > 0 } indices ? indices.Max(index => (int)index) + 1 : 0;
        if (prepared.Length < needed)
        {
            if (!await Dialogs.PromptShortPalette(this, needed, prepared.Length)) return null;
            var padded = new Rgba[needed];
            Array.Copy(prepared, padded, prepared.Length);
            for (int i = prepared.Length; i < padded.Length; i++) padded[i] = new Rgba(0, 0, 0, 255);
            prepared = padded;
        }

        return (prepared, droppedAlpha);
    }

    private static Rgba[] WithoutSffV1Alpha(Rgba[] colors)
    {
        var result = (Rgba[])colors.Clone();
        for (int i = 0; i < result.Length; i++) result[i] = result[i] with { A = i == 0 ? (byte)0 : (byte)255 };
        return result;
    }

    private void ApplyUsePreviousPalette(SffFile file, Sprite sprite)
    {
        if (SffVersions.IsV1(file.Version) && sprite.SharesWith is not null)
        {
            ShowPopup("Linked Sprite", "A linked sprite uses the palette of its link target; unlink it first.");
            return;
        }
        var previous = file.PreviousPalette(sprite);
        if (previous is null) { StatusText.Text = "No earlier sprite has a palette to use."; return; }
        if (ReferenceEquals(previous, sprite.Palette))
        {
            StatusText.Text = $"{sprite.Group},{sprite.Index} already uses the previous sprite's palette.";
            return;
        }

        var before = PaletteSnapshot(file);
        Edit("use previous palette",
            () => file.UsePreviousPalette(sprite),
            () => RestorePaletteSnapshot(file, before));
        StatusText.Text = SffVersions.IsV1(file.Version)
            ? $"{sprite.Group},{sprite.Index} now uses the previous sprite's palette ({PaletteDisplayName(file, previous)}); the sprites that followed it come along."
            : $"{sprite.Group},{sprite.Index} now uses palette {PaletteDisplayName(file, previous)}.";
    }

    private void ApplyMakePaletteUnique(SffFile file, Sprite sprite)
    {
        if (!file.PaletteCanBeMadeUnique(sprite))
        {
            if (sprite.Palette is not null && SffVersions.IsV1(file.Version) && sprite.SharesWith is not null)
                ShowPopup("Linked Sprite", "A linked sprite cannot carry its own palette; unlink it first.");
            else
                StatusText.Text = sprite.Palette is null ? "This sprite has no palette."
                    : $"{sprite.Group},{sprite.Index}'s palette is already its own.";
            return;
        }

        var before = PaletteSnapshot(file);
        Edit("make palette unique",
            () => file.MakePaletteUnique(sprite),
            () => RestorePaletteSnapshot(file, before));
        StatusText.Text = SffVersions.IsV1(file.Version)
            ? $"{sprite.Group},{sprite.Index} now carries its own copy of the palette; sprites after it that used the old one follow the copy."
            : $"{sprite.Group},{sprite.Index} now has its own copy of the palette; other sprites keep the original.";
    }

    // These palette-run operations only change which palette object each sprite
    // points at, plus possibly appending the new copy to file.Palettes — so
    // undo is a full pointer-and-list restore, and redo re-runs the operation
    // on the restored state.
    private static (List<(Sprite S, Palette? P)> Sprites, List<Palette> Palettes) PaletteSnapshot(SffFile file) =>
        (file.Sprites.Select(s => (s, s.Palette)).ToList(), file.Palettes.ToList());

    private static void RestorePaletteSnapshot(SffFile file,
        (List<(Sprite S, Palette? P)> Sprites, List<Palette> Palettes) before)
    {
        foreach (var (s, p) in before.Sprites) s.Palette = p;
        file.Palettes.Clear();
        file.Palettes.AddRange(before.Palettes);
    }

    /// <summary>
    /// One picked image, expanded to the sprites it will add. A GIF becomes one
    /// sprite per frame; a PNG or PCX stays a single image. Either way the
    /// sprites are built after the import dialog, once the plan is known.
    /// </summary>
    private sealed record PickedSource(string Name, byte[]? Bytes, List<GifImport.DecodedFrame>? Frames)
    {
        public int Count => Frames?.Count ?? 1;
        public bool IsGif => Frames is not null;
    }

    /// <summary>
    /// Turns the picked files into the images they will add, asking about
    /// repeated GIF frames as it goes. Returns null if the user cancels.
    /// </summary>
    private async Task<List<PickedSource>?> ExpandPicked(List<(byte[] Bytes, string Name)> images)
    {
        var sources = new List<PickedSource>(images.Count);
        foreach (var (bytes, name) in images)
        {
            if (!Sff.GifDecoder.LooksLikeGif(bytes)) { sources.Add(new PickedSource(name, bytes, null)); continue; }

            int total = Sff.GifImport.FrameCount(bytes);
            var frames = Sff.GifImport.DecodeFrames(bytes);
            int duplicates = Sff.GifImport.DuplicateCount(frames);
            if (duplicates > 0)
            {
                if (await Dialogs.PromptDuplicateGifFrames(this, name, total, duplicates) is not { } drop) return null;
                if (drop) frames = Sff.GifImport.DropDuplicates(frames);
            }
            sources.Add(new PickedSource(name, null, frames));
        }
        return sources;
    }

    /// <summary>One source's sprites, plus a status note if its colors needed the user's decision.</summary>
    private readonly record struct BuiltSource(List<(Sprite Sprite, ImageImport.Report Report)> Sprites, string Note);

    /// <summary>
    /// Builds the sprites a source will add, using the chosen plan. A GIF's
    /// frames share one palette; if they need more colors between them than a
    /// palette holds, the user decides what to do. Returns null if they stop.
    /// </summary>
    private async Task<BuiltSource?> BuildSource(
        PickedSource source, ImportPlan plan, ImageImport.PreparedOwnPaletteImport? prepared = null)
    {
        if (source.Frames is not { } frames)
        {
            CrashLog.Trace($"OnAddSprite: image import start {source.Name}");
            var watch = Stopwatch.StartNew();
            ImageImport.Report single;
            Sprite sprite;
            if (prepared is null) sprite = ImageImport.FromImage(source.Bytes!, plan, out single);
            else sprite = prepared.Build(out single);
            CrashLog.Trace($"OnAddSprite: image import done {source.Name}: {single.Width}x{single.Height}, "
                + $"{single.Colors} colors, reduced={single.Reduced}, {watch.ElapsedMilliseconds} ms");
            return new BuiltSource([(sprite, single)], "");
        }

        Palette? shared = null;
        string note = "";
        if (!plan.TrueColor && plan.OwnPalette)
        {
            var colors = Sff.GifImport.SharedColors(frames);
            if (colors.Overflows)
            {
                var choice = await Dialogs.PromptGifTooManyColors(this, source.Name, frames.Count, colors.ColorCount);
                if (choice is null) return null;

                note = $"{source.Name} needs {colors.ColorCount} colors, more than a palette holds, so ";
                if (choice == GifOverflowChoice.TrueColor)
                {
                    plan = ImportPlan.TrueColor32();
                    note += "it was imported as 32-bit color.";
                }
                else
                {
                    shared = colors.Reduced();
                    note += $"it was imported on a palette reduced to {shared.Colors.Length - 1} colors.";
                }
            }
            else shared = colors.ToPalette();
        }

        List<Sprite> built;
        List<ImageImport.Report> reports;
        if (shared is null) built = Sff.GifImport.ToSprites(frames, plan, out reports);
        else built = Sff.GifImport.ToSprites(frames, plan, shared, out reports);
        return new BuiltSource([.. built.Select((s, i) => (s, reports[i]))], note);
    }

    /// <summary>
    /// Adds a batch of already-built sprites just after the selected one: one
    /// undo step, numbers running upward from the dialog's choice, and any new
    /// palette folded in and taken back out again. Shared by Add and Add From
    /// Sheet so both place, number and undo identically.
    /// </summary>
    private async Task<bool> AddBuiltBatch(List<(Sprite Sprite, ImageImport.Report Report)> built,
        ImportChoices choices, ImportPlan plan, string sourceName, string what)
    {
        var file = _file!;

        // "The image's own palette" reuses an identical palette the file
        // already has, so a re-import does not pile up duplicates. Scanned
        // in batch order, so a run of identical images shares one palette.
        // A genuinely new one gets the dialog's chosen Group/Index, walking
        // past taken numbers the same way sprite numbers do below — several
        // new palettes in one batch can't all claim the same number.
        bool own = !plan.TrueColor && plan.OwnPalette;
        int reused = 0;
        var seen = file.Palettes.ToList();
        var usedPalNumbers = file.Palettes.Where(p => p.Group == choices.PaletteGroup).Select(p => p.Index).ToHashSet();
        int palNumber = choices.PaletteIndex;
        if (own)
            foreach (var (s, _) in built)
            {
                if (seen.FirstOrDefault(p => p.Colors.AsSpan().SequenceEqual(s.Palette!.Colors)) is { } match)
                { s.Palette = match; reused++; }
                else
                {
                    while (usedPalNumbers.Contains(palNumber)) palNumber++;
                    s.Palette!.Group = choices.PaletteGroup;
                    s.Palette!.Index = palNumber;
                    usedPalNumbers.Add(palNumber);
                    palNumber++;
                    seen.Add(s.Palette!);
                }
            }
        int fresh = own ? seen.Count - file.Palettes.Count : 0;

        // Numbers run from the chosen one; taken numbers are skipped.
        var used = file.Sprites.Where(s => s.Group == choices.Group).Select(s => s.Index).ToHashSet();
        int n = choices.Number;
        var skipped = new List<int>();
        foreach (var (s, _) in built)
        {
            while (used.Contains(n))
            {
                skipped.Add(n);
                if (++n > 65535)
                {
                    await Dialogs.ShowMessage(this, "No Free Numbers",
                        $"Group {choices.Group} has no free number left for all {built.Count} {what}.");
                    return false;
                }
            }
            s.Group = choices.Group;
            s.Index = n;
            used.Add(n++);
        }

        int at = _current is null ? file.Sprites.Count : file.PositionOf(_current) + 1;
        var before = _current;
        var added = built.Select(b => b.Sprite).ToList();
        var addedPalettes = added.Select(s => s.Palette)
            .OfType<Palette>().Where(p => !file.Palettes.Contains(p)).Distinct().ToList();

        Edit(added.Count == 1 ? "add sprite" : $"add {added.Count} sprites",
            () => { for (int i = 0; i < added.Count; i++) file.Insert(at + i, added[i]); _current = added[^1]; },
            () =>
            {
                foreach (var s in added) file.Remove(s);
                foreach (var p in addedPalettes) file.Palettes.Remove(p);
                _current = before;
            });
        MarkDirty();

        var numbers = added.Select(s => s.Index).ToList();
        string list = string.Join(", ", numbers.Take(5)) + (numbers.Count > 5 ? ", …" : "");
        string skipNote = skipped.Count > 0
            ? $"; skipped taken number{(skipped.Count == 1 ? "" : "s")} {string.Join(", ", skipped.Distinct())}"
            : "";
        string paletteFate = own
            ? (fresh, reused) switch
            {
                (> 0, 0) => $"Added {fresh} new palette{(fresh == 1 ? "" : "s")} to the file",
                (0, > 0) => $"Reused identical palette{(reused == 1 ? "" : "s")} already in the file",
                _ => $"Added {fresh} new palette{(fresh == 1 ? "" : "s")}, {reused} reused",
            }
            : "";
        StatusText.Text = added.Count == 1
            ? $"Added {choices.Group},{numbers[0]} from {sourceName}. {built[0].Report}"
              + (paletteFate.Length > 0 ? $" {paletteFate}." : "")
            : $"Added {added.Count} sprites to group {choices.Group}: {list}{skipNote}. {built[0].Report}"
              + (paletteFate.Length > 0 ? $" {paletteFate}." : "");
        if (built.Count > 1)
        {
            int reducedCount = built.Count(b => b.Report.Reduced);
            if (reducedCount > 0)
            {
                string reduction = reducedCount == 1
                    ? "1 sprite was reduced"
                    : $"{reducedCount} sprites were reduced";
                StatusText.Text += $" {reduction} to {Sff.GifImport.MaxSharedColors} colors by median cut.";
            }
        }
        return true;
    }

    /// <summary>
    /// Finds the sprites in one image and adds each as its own sprite, cropped
    /// tight. Separate from Add because the layout is a per-image decision, and
    /// one sheet is one file rather than a batch of them.
    /// </summary>
    private async void OnAddSpriteSheet(object? sender, RoutedEventArgs e)
    {
        if (_file is null) return;
        try
        {
            if (await PickImages("Import from Sheet", allowMultiple: false) is not { } picked) return;
            var (bytes, name) = picked[0];

            SheetLayout layout;
            try
            {
                layout = SheetImport.Detect(bytes);
            }
            catch (Exception ex)
            {
                CrashLog.Write("OnAddSpriteSheet", ex);
                await Dialogs.ShowMessage(this, "Could Not Read the Sheet", ex.Message);
                return;
            }

            if (layout.Count == 0)
            {
                await Dialogs.ShowMessage(this, "No Sprites Found",
                    $"{name} has nothing that stands out from its background. Try a different background, or a lower size floor.");
                return;
            }

            // The guess and the floor are the two things detection decided on
            // the user's behalf, so both go past them before anything is added.
            if (await Dialogs.PromptSheetDetect(this, name, bytes, layout, layout.Count,
                    SheetImport.MinimumSize) is not { } detected) return;

            // Re-detect if the floor or background moved, so what is imported
            // is what the dialog just showed.
            if (detected.MinimumSize != SheetImport.MinimumSize || detected.Background != layout.Background)
            {
                // Kept for the rest of the session, like Find Similar's choices; settings.json seeds it.
                SheetImport.MinimumSize = detected.MinimumSize;
                try { layout = SheetImport.Detect(bytes, detected.Background); }
                catch (Exception ex)
                {
                    CrashLog.Write("OnAddSpriteSheet", ex);
                    await Dialogs.ShowMessage(this, "Could Not Read the Sheet", ex.Message);
                    return;
                }
            }

            if (detected.Excluded != null && detected.Excluded.Count > 0)
            {
                var kept = layout.Cells.Where(c => !detected.Excluded.Contains(c)).ToList();
                layout = new Sff.SheetLayout(kept, layout.Background, layout.AssumedBackground);
            }

            if (layout.Count == 0)
            {
                await Dialogs.ShowMessage(this, "No Sprites Found",
                    $"{name} has nothing that stands out from that background at a floor of {detected.MinimumSize} pixels.");
                return;
            }

            var file = _file;
            int group = _current?.Group ?? 0;
            int number = NextFreeIndex(group);
            var previous = file.PaletteBefore(_current is null ? file.Sprites.Count - 1 : file.PositionOf(_current));
            var source = new PickedSource(name, bytes, null);

            CrashLog.Trace($"OnAddSpriteSheet: {name}, {layout.Count} sprites, floor {detected.MinimumSize}, background {detected.Background?.ToString() ?? "(the sheet's own transparency)"}");

            // The dialog's own choices still decide placement, palette and
            // indexes, exactly as they do for Add.
            if (await PromptImport("Import from Sheet", [source], layout.Count, file, previous,
                    askPlacement: true, group, number, group, layout) is not { } choices) return;

            var plan = BuildPlan(choices, previous);
            Palette? sharedPalette = null;
            string colorNote = "";
            if (!plan.TrueColor && plan.OwnPalette && !ImageImport.ImageIsIndexed(bytes))
            {
                var colors = SheetImport.SharedColors(bytes, layout);
                if (colors.Overflows)
                {
                    var choice = await Dialogs.PromptTooManyColors(
                        this, name, colors.ColorCount, SffVersions.SupportsPng(file.Version));
                    if (choice is null) return;
                    if (choice == Dialogs.TooManyColorsChoice.TrueColor)
                    {
                        plan = ImportPlan.TrueColor32();
                        colorNote = "Imported as 32-bit color.";
                    }
                    else
                    {
                        sharedPalette = colors.Reduced();
                        colorNote = $"Colors were reduced to {Sff.GifImport.MaxSharedColors} by median cut.";
                    }
                }
                else sharedPalette = colors.ToPalette();
            }

            List<Sprite> sprites;
            List<ImageImport.Report> reports;
            if (sharedPalette is null)
                sprites = SheetImport.ToSprites(bytes, layout, plan, out reports);
            else
                sprites = SheetImport.ToSprites(bytes, layout, plan, sharedPalette, out reports);
            var built = sprites.Select((s, i) => (s, reports[i])).ToList();
            CrashLog.Trace($"OnAddSpriteSheet: built {built.Count} sprite(s), true color {plan.TrueColor}");

            if (!await AddBuiltBatch(built, choices, plan, name, "sprites")) return;
            if (colorNote.Length > 0) StatusText.Text += " " + colorNote;
        }
        catch (Exception ex)
        {
            CrashLog.Write("OnAddSpriteSheet", ex);
            await Dialogs.ShowMessage(this, "Could Not Import", ex.Message);
        }
    }

    /// <summary>Duplicates the sprite to the next free number in its own group, just after it.
    /// The copy owns its pixels rather than linking them, so it can be painted or turned
    /// without touching the original; the palette stays shared, as the format intends.
    /// </summary>
    private async void OnDuplicateSprite(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_file is not { } file || _current is not { } source) return;
            var selected = SelectedSpriteTargets();
            if (selected.Count > 1) { await DuplicateSprites(file, selected); return; }
            if (!source.HasPixels)
            {
                StatusText.Text = $"Nothing to duplicate \u2014 sprite {source.Group},{source.Index} has no pixels.";
                return;
            }

            int group = source.Group;
            int index = NextFreeIndex(group);
            if (index > 65535)
            {
                await Dialogs.ShowMessage(this, "Duplicate Sprite", $"Group {group} has no free number left.");
                return;
            }

            var copy = DuplicateOf(source, index);
            int at = file.PositionOf(source) + 1;
            Edit("duplicate sprite",
                () => { file.Insert(at, copy); _current = copy; },
                () => { file.Remove(copy); _current = source; });
            MarkDirty();
            StatusText.Text = $"Duplicated {source.Group},{source.Index} to {group},{index}.";
        }
        catch (Exception ex)
        {
            await Failed("OnDuplicateSprite", ex);
        }
    }

    /// <summary>An unlinked copy of the sprite's pixels, axis and palette under a new number.</summary>
    private static Sprite DuplicateOf(Sprite source, int index) => new()
    {
        Group = source.Group, Index = index,
        Width = source.Width, Height = source.Height,
        AxisX = source.AxisX, AxisY = source.AxisY,
        Palette = source.Palette,
        Indexed = source.Indexed?.ToArray(),
        Direct = source.Direct?.ToArray(),
        SrcFormat = source.SrcFormat,
        SrcDepth = source.SrcDepth,
    };

    /// <summary>
    /// Several sprites: per group, the copies go in one block right after that group's last
    /// selected sprite, in file order, numbered from the group's next free index. One undo
    /// step; the copies end up selected. Sprites without pixels are skipped and named.
    /// </summary>
    private async Task DuplicateSprites(SffFile file, List<Sprite> selected)
    {
        var sources = selected.Where(s => s.HasPixels).OrderBy(file.PositionOf).ToList();
        var skipped = selected.Where(s => !s.HasPixels).Select(s => $"{s.Group},{s.Index}").ToList();
        if (sources.Count == 0) { StatusText.Text = "Nothing to duplicate: the selected sprites have no pixels."; return; }

        // (insert after this original, copies) per group, laid out against the file as it is now.
        var blocks = new List<(Sprite After, List<Sprite> Copies)>();
        foreach (var group in sources.GroupBy(s => s.Group))
        {
            var used = file.Sprites.Where(s => s.Group == group.Key).Select(s => s.Index).ToHashSet();
            var copies = new List<Sprite>();
            int next = 0;
            foreach (var source in group)
            {
                while (used.Contains(next)) next++;
                if (next > 65535)
                {
                    await Dialogs.ShowMessage(this, "Duplicate Sprites", $"Group {group.Key} has no free number left for every copy.");
                    return;
                }
                used.Add(next);
                copies.Add(DuplicateOf(source, next));
            }
            blocks.Add((group.Last(), copies));
        }

        var all = blocks.SelectMany(b => b.Copies).ToList();
        var before = _current;
        Edit(all.Count == 1 ? "duplicate sprite" : $"duplicate {all.Count} sprites",
            () =>
            {
                // Each block goes in after its group's last original; positions are looked up as they stand.
                foreach (var (after, copies) in blocks)
                {
                    int at = file.PositionOf(after) + 1;
                    for (int i = 0; i < copies.Count; i++) file.Insert(at + i, copies[i]);
                }
                _current = all[0];
            },
            () => { foreach (var copy in all) file.Remove(copy); _current = before; });
        MarkDirty();

        var rows = all.Select(c => _visible.IndexOf(file.PositionOf(c))).Where(r => r >= 0).ToList();
        ApplySpriteRowSelection(rows, suppressEvents: true);
        CrashLog.Trace($"[sprites] duplicated {all.Count} sprites");
        StatusText.Text = $"Duplicated {all.Count} sprite{(all.Count == 1 ? "" : "s")}."
            + (skipped.Count > 0 ? $" Skipped (no pixels): {string.Join(", ", skipped.Take(5))}{(skipped.Count > 5 ? ", …" : "")}." : "");
    }

    /// <summary>Imports one or more images as new sprites just after the selected one.</summary>
    private async void OnAddSprite(object? sender, RoutedEventArgs e)
    {
        if (_file is null) return;
        try
        {
            if (await PickImages("Add Sprite from Image", allowMultiple: true) is not { } picked) return;
            if (await ExpandPicked(picked) is not { } sources) return;
            int count = sources.Sum(s => s.Count);

            var file = _file;
            int group = _current?.Group ?? 0;
            int number = NextFreeIndex(group);
            var previous = file.PaletteBefore(_current is null ? file.Sprites.Count - 1 : file.PositionOf(_current));

            if (await PromptImport($"Add Sprite from Image{(count == 1 ? "" : "s")}", sources, count, file, previous,
                    askPlacement: true, group, number, group) is not { } choices) return;

            CrashLog.Trace($"OnAddSprite: {count} sprite(s) into group {choices.Group} from number {choices.Number}");
            var plan = BuildPlan(choices, previous);
            var prepared = new ImageImport.PreparedOwnPaletteImport?[sources.Count];
            var reductions = new List<string>();
            if (!plan.TrueColor && plan.OwnPalette)
                for (int i = 0; i < sources.Count; i++)
                {
                    var source = sources[i];
                    if (source.Frames is not null) continue;
                    CrashLog.Trace($"OnAddSprite: palette preflight start {source.Name}");
                    var watch = Stopwatch.StartNew();
                    prepared[i] = ImageImport.PrepareOwnPalette(source.Bytes!);
                    CrashLog.Trace($"OnAddSprite: palette preflight done {source.Name}: {prepared[i]!.Width}x{prepared[i]!.Height}, "
                        + $"medianCut={prepared[i]!.RequiresMedianCut}, {watch.ElapsedMilliseconds} ms");
                    if (prepared[i]!.RequiresMedianCut) reductions.Add(source.Name);
                }

            if (reductions.Count > 0)
            {
                CrashLog.Trace($"OnAddSprite: awaiting median-cut confirmation for {reductions.Count} image(s)");
                if (!await Dialogs.ConfirmMedianCut(this, reductions))
                {
                    CrashLog.Trace("OnAddSprite: canceled median-cut confirmation");
                    return;
                }
                CrashLog.Trace("OnAddSprite: median-cut confirmation accepted");
            }

            var built = new List<(Sprite Sprite, ImageImport.Report Report)>(count);
            var notes = new List<string>();
            for (int i = 0; i < sources.Count; i++)
            {
                if (await BuildSource(sources[i], plan, prepared[i]) is not { } made) return;
                built.AddRange(made.Sprites);
                if (made.Note.Length > 0) notes.Add(made.Note);
            }

            if (!await AddBuiltBatch(built, choices, plan, sources[0].Name, "images")) return;
            if (notes.Count > 0) StatusText.Text += " " + string.Join(" ", notes);
        }
        catch (Exception ex)
        {
            CrashLog.Write("OnAddSprite", ex);
            await Dialogs.ShowMessage(this, "Could Not Import", ex.Message);
        }
    }

    /// <summary>
    /// Swaps in new pixels, keeping the sprite's group, number and axis. A
    /// multi-frame GIF is refused, since one sprite cannot hold an animation.
    /// </summary>
    private async void OnReplaceSprite(object? sender, RoutedEventArgs e)
    {
        if (_file is null || _current is null) return;
        try
        {
            if (RefuseBorrowerEdit(_current, "replace")) return;
            if (await PickImages($"Replace {_current.Group},{_current.Index}", allowMultiple: false) is not { } picked) return;
            var gif = Sff.GifDecoder.LooksLikeGif(picked[0].Bytes);
            int total = gif ? Sff.GifImport.FrameCount(picked[0].Bytes) : 0;
            if (total > 1)
            {
                CrashLog.Error("OnReplaceSprite", $"{picked[0].Name}: {total} frames, refused");
                await Dialogs.ShowMessage(this, "Cannot Replace Sprite",
                    $"{picked[0].Name} has {total} frames, which would make {total} sprites.\nOne sprite can't be replaced by multiple sprites.\nUse Add to import the frames as new sprites.");
                return;
            }

            var file = _file;
            var sprite = _current;
            var previous = file.PaletteBefore(file.PositionOf(sprite) - 1);
            var sources = new List<PickedSource> { new(picked[0].Name, picked[0].Bytes, null) };
            if (await PromptImport($"Replace {sprite.Group},{sprite.Index}", sources, 1, file, previous,
                    askPlacement: false, 0, 0, sprite.Group) is not { } choices) return;

            int dependents = file.Sprites.Count(s => ReferenceEquals(s.SharesWith, sprite));
            if (dependents > 0 && !await Dialogs.Confirm(this, "Linked Sprites",
                    $"{dependents} other sprite(s) link to these pixels and will change too.",
                    "Replace Anyway"))
                return;

            CrashLog.Trace($"OnReplaceSprite: {sprite.Group},{sprite.Index} from {picked[0].Name}");
            var plan = BuildPlan(choices, previous);
            ImageImport.PreparedOwnPaletteImport? prepared = null;
            if (!gif && !plan.TrueColor && plan.OwnPalette)
            {
                CrashLog.Trace($"OnReplaceSprite: palette preflight start {picked[0].Name}");
                var watch = Stopwatch.StartNew();
                prepared = ImageImport.PrepareOwnPalette(picked[0].Bytes);
                CrashLog.Trace($"OnReplaceSprite: palette preflight done {picked[0].Name}: {prepared.Width}x{prepared.Height}, "
                    + $"medianCut={prepared.RequiresMedianCut}, {watch.ElapsedMilliseconds} ms");
                if (prepared.RequiresMedianCut)
                {
                    CrashLog.Trace("OnReplaceSprite: awaiting median-cut confirmation");
                    if (!await Dialogs.ConfirmMedianCut(this, [picked[0].Name]))
                    {
                        CrashLog.Trace("OnReplaceSprite: canceled median-cut confirmation");
                        return;
                    }
                    CrashLog.Trace("OnReplaceSprite: median-cut confirmation accepted");
                }
            }

            var before = (sprite.SharesWith, sprite.Indexed, sprite.Direct, sprite.Width, sprite.Height, sprite.PixelsDirty);
            var beforePalette = sprite.Palette;
            var beforeFormat = (sprite.SrcFormat, sprite.SrcDepth);

            ImageImport.Report report;
            if (gif)
            {
                // A single-frame GIF, so frame 0 is the whole image.
                var frames = Sff.GifImport.DecodeFrames(picked[0].Bytes);
                var first = Sff.GifImport.ToSprites([frames[0]], plan, out var gifReports)[0];
                report = gifReports[0];
                sprite.SharesWith = null;
                sprite.Indexed = first.Indexed;
                sprite.Direct = first.Direct;
                sprite.Palette = first.Palette;
                sprite.Width = first.Width;
                sprite.Height = first.Height;
                sprite.DecodeError = first.DecodeError;
                sprite.PixelsDirty = first.PixelsDirty;
                sprite.SrcFormat = first.SrcFormat;
                sprite.SrcDepth = first.SrcDepth;
            }
            else
            {
                CrashLog.Trace($"OnReplaceSprite: image import start {picked[0].Name}");
                var watch = Stopwatch.StartNew();
                var replacement = prepared is null
                    ? ImageImport.FromImage(picked[0].Bytes, plan, out report)
                    : prepared.Build(out report);
                CrashLog.Trace($"OnReplaceSprite: image import done {picked[0].Name}: {report.Width}x{report.Height}, "
                    + $"{report.Colors} colors, reduced={report.Reduced}, {watch.ElapsedMilliseconds} ms");
                sprite.SharesWith = replacement.SharesWith;
                sprite.Indexed = replacement.Indexed;
                sprite.Direct = replacement.Direct;
                sprite.Palette = replacement.Palette;
                sprite.Width = replacement.Width;
                sprite.Height = replacement.Height;
                sprite.DecodeError = replacement.DecodeError;
                sprite.PixelsDirty = replacement.PixelsDirty;
                sprite.SrcFormat = replacement.SrcFormat;
                sprite.SrcDepth = replacement.SrcDepth;
            }
            // An own-palette import builds a new Palette object, so it is safe to
            // stamp the dialog's chosen numbers onto it here.
            if (!plan.TrueColor && plan.OwnPalette && sprite.Palette is { } newPal)
            {
                newPal.Group = choices.PaletteGroup;
                newPal.Index = choices.PaletteIndex;
            }
            file.EnsurePalette(sprite.Palette);

            var after = (sprite.SharesWith, sprite.Indexed, sprite.Direct, sprite.Width, sprite.Height, sprite.PixelsDirty);
            var afterPalette = sprite.Palette;
            var afterFormat = (sprite.SrcFormat, sprite.SrcDepth);

            _undo.Record("replace image",
                () => { Restore(sprite, before); sprite.Palette = beforePalette; (sprite.SrcFormat, sprite.SrcDepth) = beforeFormat; AfterEdit(); },
                () => { Restore(sprite, after); sprite.Palette = afterPalette; (sprite.SrcFormat, sprite.SrcDepth) = afterFormat; AfterEdit(); });
            MarkDirty();
            AfterEdit();
            StatusText.Text = $"Replaced from {picked[0].Name}. {report}";
        }
        catch (Exception ex)
        {
            CrashLog.Write("OnReplaceSprite", ex);
            await Dialogs.ShowMessage(this, "Could Not Import", ex.Message);
        }
    }

    private async void OnDeleteSprite(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_file is not { } file) return;

            // File order, so undo re-inserts each sprite at its own position.
            var spritesToDelete = SelectedSpriteTargets().OrderBy(file.PositionOf).ToList();
            if (spritesToDelete.Count == 0) return;
            var selectedSet = spritesToDelete.ToHashSet();

            // Only borrowers left behind block; a linker deleted with its target is fine.
            var names = spritesToDelete.Select(s => $"{s.Group},{s.Index}").ToList();
            var dependents = file.Sprites
                .Where(dep => !selectedSet.Contains(dep) && dep.SharesWith is { } t && selectedSet.Contains(t))
                .ToList();
            if (dependents.Count > 0)
            {
                CrashLog.Error("OnDeleteSprite", $"refused: {dependents.Count} sprite(s) link to selected sprite pixels");
                string borrowers = string.Join(", ", dependents.Take(5).Select(d => $"{d.Group},{d.Index}"))
                    + (dependents.Count > 5 ? ", …" : "");
                bool one = dependents.Count == 1;
                await Dialogs.ShowMessage(this, "Cannot Delete Sprite",
                    $"{borrowers} {(one ? "uses" : "use")} pixels from the selected "
                    + (spritesToDelete.Count == 1 ? "sprite" : "sprites")
                    + $". Unlink {(one ? "it" : "them")} first, or select {(one ? "it" : "them")} too.");
                return;
            }

            bool v1 = SffVersions.IsV1(file.Version);
            var palettesToDelete = spritesToDelete.Select(sprite => sprite.Palette).OfType<Palette>()
                .Distinct()
                .Where(palette => palette.Source == PaletteSource.Embedded && (v1 || !palette.IsCharacterPalette)
                    && file.Palettes.Contains(palette)
                    && !file.Sprites.Any(sprite => !selectedSet.Contains(sprite) && ReferenceEquals(sprite.Palette, palette)))
                .OrderBy(file.Palettes.IndexOf)
                .ToList();
            bool deletePalettes = v1 && palettesToDelete.Count > 0;
            var choice = await Dialogs.Confirm(this, "Delete Sprite",
                spritesToDelete.Count == 1 ? $"Delete {names[0]}?" : $"Delete {spritesToDelete.Count} sprites?",
                "Delete");
            if (!choice || !ReferenceEquals(_file, file)
                || spritesToDelete.Any(sprite => !file.Sprites.Contains(sprite))) return;

            if (!v1 && palettesToDelete.Count > 0)
            {
                var candidateSet = palettesToDelete.ToHashSet();
                int linkedPalettes = file.Palettes.Count(palette => palette.LinksTo is { } target
                    && candidateSet.Contains(target) && !candidateSet.Contains(palette));
                var paletteChoice = await Dialogs.PromptDeleteSpritesAndPalettes(this, palettesToDelete, linkedPalettes);
                if (paletteChoice == DeleteSpritesPaletteChoice.Cancel || !ReferenceEquals(_file, file)
                    || spritesToDelete.Any(sprite => !file.Sprites.Contains(sprite))) return;
                deletePalettes = paletteChoice == DeleteSpritesPaletteChoice.DeleteSpritesAndPalettes;
            }

            if (deletePalettes)
            {
                palettesToDelete = palettesToDelete.Where(palette => file.Palettes.Contains(palette)
                    && !file.Sprites.Any(sprite => !selectedSet.Contains(sprite) && ReferenceEquals(sprite.Palette, palette)))
                    .ToList();
                deletePalettes = palettesToDelete.Count > 0;
            }

            var linkers = deletePalettes
                ? file.Palettes.Where(palette => palettesToDelete.Any(target => ReferenceEquals(palette.LinksTo, target))).ToList()
                : [];
            bool pendingPaletteAffected = HasPendingColorAdjust()
                && _editingPalette is { Source: PaletteSource.Embedded } pendingPalette
                && (palettesToDelete.Contains(pendingPalette) || linkers.Contains(pendingPalette));
            if (pendingPaletteAffected)
            {
                var pendingChoice = await Dialogs.AskAboutPendingColorAdjust(this);
                if (pendingChoice == PendingAdjustChoice.Cancel || !ReferenceEquals(_file, file)
                    || spritesToDelete.Any(sprite => !file.Sprites.Contains(sprite))) return;
                if (pendingChoice == PendingAdjustChoice.Commit) CommitPendingColorAdjust();
                else DiscardPendingColorAdjust();
            }

            var positions = spritesToDelete.Select(file.PositionOf).ToList();
            var paletteOrder = deletePalettes ? file.Palettes.ToList() : [];
            var linkerStates = linkers.Select(palette => (Palette: palette, State: PaletteLinkState.Capture(palette))).ToList();
            var paletteOverride = _paletteOverride >= 0 && _paletteOverride < file.Palettes.Count
                ? file.Palettes[_paletteOverride]
                : null;
            bool paletteOverrideDeleted = deletePalettes && paletteOverride is not null && palettesToDelete.Contains(paletteOverride);
            bool editingPaletteDeleted = deletePalettes && _editingPalette is { } editing && palettesToDelete.Contains(editing);
            var editingPalette = _editingPalette;
            var editingView = _editingView;
            int selectedSwatch = _selectedSwatch;
            bool colorEditorEnabled = ColorEditor.IsEnabled;
            string? swatchInfo = SwatchInfo.Text;

            void ClearDeletedPaletteEditor()
            {
                if (!editingPaletteDeleted) return;
                _editingPalette = null;
                _editingView = null;
                _selectedSwatch = -1;
                ColorEditor.IsEnabled = false;
                ASlider.IsEnabled = false;
                ResetColorAdjustBase();
            }

            void RestoreDeletedPaletteEditor()
            {
                if (!editingPaletteDeleted || editingPalette is null) return;
                _editingPalette = editingPalette;
                _editingView = editingView;
                _selectedSwatch = selectedSwatch;
                SwatchInfo.Text = swatchInfo;
                ColorEditor.IsEnabled = colorEditorEnabled;
                ASlider.IsEnabled = colorEditorEnabled && CanEditPaletteAlpha(editingPalette);
                if (selectedSwatch >= 0 && selectedSwatch < editingPalette.Colors.Length)
                {
                    var color = editingPalette.Colors[selectedSwatch];
                    _suppressEvents = true;
                    RSlider.Value = color.R; GSlider.Value = color.G; BSlider.Value = color.B; ASlider.Value = color.A;
                    _suppressEvents = false;
                    SpriteCanvas.PaintColorIndex = selectedSwatch;
                }
                ResetColorAdjustBase();
            }

            void DeleteSpritesAndPalettes()
            {
                // One pass: file.Remove would unshare in-batch linkers, and undo would restore them unlinked.
                file.Sprites.RemoveAll(selectedSet.Contains);
                if (deletePalettes)
                {
                    foreach (var (palette, state) in linkerStates)
                        new PaletteLinkState(null, state.Colors).Restore(palette);
                    foreach (var palette in palettesToDelete.OrderByDescending(file.Palettes.IndexOf))
                        file.Palettes.Remove(palette);
                    ClearDeletedPaletteEditor();
                }

                _paletteOverride = paletteOverrideDeleted || paletteOverride is null
                    ? -1
                    : file.Palettes.IndexOf(paletteOverride);
                _suppressEvents = true;
                PaletteBox.SelectedIndex = _paletteOverride + 1;
                _suppressEvents = false;

                int firstAt = positions.Min();
                _current = firstAt < file.Sprites.Count ? file.Sprites[firstAt]
                    : file.Sprites.Count > 0 ? file.Sprites[^1]
                    : null;
            }

            void RestoreSpritesAndPalettes()
            {
                if (deletePalettes)
                {
                    file.Palettes.Clear();
                    file.Palettes.AddRange(paletteOrder);
                    foreach (var (palette, state) in linkerStates)
                        state.Restore(palette);
                }
                for (int i = 0; i < spritesToDelete.Count; i++) file.Insert(positions[i], spritesToDelete[i]);
                _current = spritesToDelete[0];
                _paletteOverride = paletteOverride is null ? -1 : file.Palettes.IndexOf(paletteOverride);
                _suppressEvents = true;
                PaletteBox.SelectedIndex = _paletteOverride + 1;
                _suppressEvents = false;
                RestoreDeletedPaletteEditor();
            }

            Edit(deletePalettes && palettesToDelete.Count > 0 ? "delete sprites and palettes" : "delete sprite",
                DeleteSpritesAndPalettes, RestoreSpritesAndPalettes);

            StatusText.Text = deletePalettes && palettesToDelete.Count > 0 && !v1
                ? $"Deleted {spritesToDelete.Count} sprite(s) and {palettesToDelete.Count} palette(s)."
                : $"Deleted {spritesToDelete.Count} sprite(s).";
        }
        catch (Exception ex)
        {
            await Failed("OnDeleteSprite", ex);
        }
    }

    private void SelectAllSprites()
    {
        CrashLog.Trace("SelectAllSprites: start");
        if (SpriteList is not null && SpriteList.SelectedItems is not null)
        {
            // Select the whole list in one batched SelectionModel operation (what ListBox.SelectAll /
            // native Ctrl+A already do) rather than Add-ing rows one at a time. AutoScrollToSelectedItem
            // is off on SpriteList, so the view never relocates on a selection change.
            _suppressEvents = true;
            try
            {
                SpriteList.SelectAll();
                CrashLog.Trace("SelectAllSprites: all items selected");
            }
            finally
            {
                _suppressEvents = false;
                CrashLog.Trace("SelectAllSprites: done");
            }
        }
    }

    private void InvertSpriteSelection()
    {
        CrashLog.Trace("InvertSpriteSelection: start");
        if (SpriteList is not null && SpriteList.SelectedItems is not null)
        {
            // Build the inverted set and apply by clearing and re-adding (not by assigning a fresh list,
            // which breaks the click-to-collapse path afterwards). AutoScrollToSelectedItem is off on the
            // list, so the view doesn't relocate. The add loop is fast; it was only slow when each add
            // scrolled the virtualized panel.
            _suppressEvents = true;
            try
            {
                var selectedItems = new HashSet<object?>(SpriteList.SelectedItems.Cast<object?>());
                SpriteList.SelectedItems.Clear();
                CrashLog.Trace($"InvertSpriteSelection: clearing done, inverting {SpriteList.Items.Count} items");
                for (int i = 0; i < SpriteList.Items.Count; i++)
                {
                    var item = SpriteList.Items[i];
                    if (item is not null && !selectedItems.Contains(item))
                        SpriteList.SelectedItems.Add(item);
                }
                CrashLog.Trace("InvertSpriteSelection: done");
            }
            finally
            {
                _suppressEvents = false;
            }
        }
    }


    private int NextFreeIndex(int group)
    {
        if (_file is null) return 0;
        var used = _file.Sprites.Where(s => s.Group == group).Select(s => s.Index).ToHashSet();
        int n = 0;
        while (used.Contains(n)) n++;
        return n;
    }

    /// <summary>In SFFv2 a temp palette moves into the SFF when applied, so no duplicate is left behind.</summary>
    private static bool MovesIntoSff(SffFile file, Palette chosen) => IsTempPalette(chosen) && !SffVersions.IsV1(file.Version);

    /// <summary>SFFv1: once a temp palette's colors are copied into sprites, it leaves the list; no-ops for other palettes.</summary>
    private static (Action Drop, Action Restore) TempPaletteLeavesList(SffFile file, Palette palette)
    {
        if (!IsTempPalette(palette)) return (() => { }, () => { });
        int at = file.Palettes.IndexOf(palette);
        return (() => file.Palettes.Remove(palette),
            () => { if (!file.Palettes.Contains(palette)) file.Palettes.Insert(Math.Min(at, file.Palettes.Count), palette); });
    }

    /// <summary>Turns a temp palette into an embedded one at the next free index of its group; returns apply/undo steps.</summary>
    private static (Action MoveIn, Action MoveOut, int Index) MoveTempPaletteIntoSff(SffFile file, Palette palette, Rgba[] colors)
    {
        var used = file.Palettes.Where(p => p.Group == palette.Group && !ReferenceEquals(p, palette)).Select(p => p.Index).ToHashSet();
        int index = 0;
        while (used.Contains(index)) index++;
        var before = (palette.Index, palette.Colors, palette.Dirty);
        return (() =>
        {
            palette.Source = PaletteSource.Embedded;
            palette.Index = index;
            palette.Colors = (Rgba[])colors.Clone();
            palette.Dirty = false;
        }, () =>
        {
            palette.Source = PaletteSource.External;
            (palette.Index, palette.Colors, palette.Dirty) = before;
        }, index);
    }

    /// <summary>Same idea as NextFreeIndex, but over the file's palettes rather than its sprites —
    /// the default Index offered for a brand-new "own palette".</summary>
    private int NextFreePaletteIndex(int group)
    {
        if (_file is null) return 0;
        var used = _file.Palettes.Where(p => p.Group == group).Select(p => p.Index).ToHashSet();
        int n = 0;
        while (used.Contains(n)) n++;
        return n;
    }

    private async Task<List<(byte[] Bytes, string Name)>?> PickImages(string title, bool allowMultiple)
    {
        var files = await PickOpen(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = [SpriteImageType],
        });
        if (files.Count == 0) return null;
        var picked = files
            .Select(f => f.TryGetLocalPath())
            .OfType<string>()
            .Select(p => (Bytes: File.ReadAllBytes(p), Name: Path.GetFileName(p)))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return picked.Count == 0 ? null : picked;
    }

    /// <summary>
    /// The add/replace import-options dialog. previousPalette is what "the
    /// previous sprite's palette" means here; imagesIndexed decides whether
    /// "keep the image's indexes" is offered at all. paletteGroup defaults the
    /// new-palette-group field to the sprite's own group; its Index field
    /// defaults to the next free number in that group. sheet is the detected
    /// layout when the one source is a sprite sheet.
    /// </summary>
    private async Task<ImportChoices?> PromptImport(string title, List<PickedSource> sources, int spriteCount,
        SffFile file, Palette? previousPalette, bool askPlacement, int group, int number, int paletteGroup,
        SheetLayout? sheet = null)
    {
        // A GIF's frames decode to color, never to indexes, so "keep the
        // image's indexes" is off for them, and they default to 32-bit color
        // the same way a true-color image does.
        bool allIndexed = sources.All(s => s.Bytes is not null && ImageImport.ImageIsIndexed(s.Bytes));
        bool anyGif = sources.Any(s => s.IsGif);
        var externalOrdinals = ExternalPaletteOrdinals(file);
        var carriers = V1PaletteCarriers(file);
        var paletteLabels = file.Palettes.Select((palette, index) =>
            $"{index + 1}. {PaletteDropdownName(file, palette, externalOrdinals, carriers)}").ToArray();
        // Defaults come from the images, not the last import: a true-color
        // image has no palette to keep, so it starts as 32-bit color, while an
        // indexed batch starts on its own palette.
        var initial = new ImportChoices(
            ImportPaletteChoice.Own, null, ImportIndexChoice.Keep, anyGif || !allIndexed ? BitDepthChoice.ThirtyTwoBit : BitDepthChoice.EightBit,
            group, number, paletteGroup, NextFreePaletteIndex(paletteGroup));

        // Every image is scored against every palette once, so the dropdown can
        // open on one that reproduces the whole batch exactly.
        var palettes = file.Palettes.ToList();
        var watch = Stopwatch.StartNew();
        var (scored, references, keepMatches, nearestMatches) = await Task.Run(() =>
        {
            // Replace hands over a single-frame GIF unexpanded.
            var expanded = sources.Select(s => s.Frames is null && Sff.GifDecoder.LooksLikeGif(s.Bytes!)
                ? new PickedSource(s.Name, null, Sff.GifImport.DecodeFrames(s.Bytes!).Take(1).ToList()) : s).ToList();
            var refs = ReferenceSprites(expanded, sheet);
            var fit = new PaletteFit(refs);
            return (expanded, refs, palettes.Select(p => fit.Matches(p, true)).ToArray(),
                palettes.Select(p => fit.Matches(p, false)).ToArray());
        });
        CrashLog.Trace($"PromptImport: matched {references.Count} image(s) against {palettes.Count} palette(s) in {watch.ElapsedMilliseconds} ms");

        bool keepByDefault = initial.IndexChoice == ImportIndexChoice.Keep && allIndexed;
        var defaultMatches = keepByDefault ? keepMatches : nearestMatches;
        bool Full(Palette? p) => p is not null && references.Count > 0
            && palettes.IndexOf(p) is int at && at >= 0 && defaultMatches[at] == references.Count;
        var fullMatch = new[] { _current?.Palette, previousPalette }.FirstOrDefault(Full)
            ?? palettes.FirstOrDefault(Full);
        var specificInitial = fullMatch ?? previousPalette ?? _current?.Palette;

        var preview = new ImportPreview(
            c => BuildPreview(scored[0], sheet, BuildPlan(c, previousPalette)), _canvasLook);

        var choices = await Dialogs.PromptSpriteImport(this, title, spriteCount, SffVersions.SupportsPng(file.Version),
            file.Palettes, previousPalette, specificInitial,
            askPlacement, group, number, paletteGroup, NextFreePaletteIndex(paletteGroup), allIndexed,
            initial, paletteLabels, framesGif: anyGif,
            matches: new ImportPaletteMatches(keepMatches, nearestMatches, references.Count), preview: preview);
        return choices;
    }

    /// <summary>One lossless sprite per image the import adds (own palette if indexed, else 32-bit).
    /// A source that can't be built so (a PCX with no palette) yields nulls, which match nothing.</summary>
    private static List<Sprite?> ReferenceSprites(List<PickedSource> sources, SheetLayout? sheet)
    {
        var result = new List<Sprite?>();
        foreach (var source in sources)
        {
            try
            {
                if (source.Frames is { } frames)
                    result.AddRange(GifImport.ToSprites(frames, ImportPlan.TrueColor32(), out _));
                else if (sheet is not null)
                    result.AddRange(SheetImport.ToSprites(source.Bytes!, sheet, LosslessPlan(source.Bytes!), out _));
                else
                    result.Add(ImageImport.FromImage(source.Bytes!, LosslessPlan(source.Bytes!), out _));
            }
            catch (Exception ex)
            {
                CrashLog.Write($"ReferenceSprites {source.Name}", ex);
                for (int i = 0; i < (sheet?.Count ?? source.Count); i++) result.Add(null);
            }
        }
        return result;
    }

    private static ImportPlan LosslessPlan(byte[] image) =>
        ImageImport.ImageIsIndexed(image) ? ImportPlan.Own() : ImportPlan.TrueColor32();

    /// <summary>The first sprite the import would add under a plan. Shared colors that
    /// overflow a palette show reduced, one of the choices the import then offers.</summary>
    private static Sprite BuildPreview(PickedSource source, SheetLayout? sheet, ImportPlan plan)
    {
        bool own = !plan.TrueColor && plan.OwnPalette;
        if (source.Frames is { } frames)
        {
            var first = frames.Take(1).ToList();
            if (!own) return GifImport.ToSprites(first, plan, out _)[0];
            var colors = GifImport.SharedColors(frames);
            return GifImport.ToSprites(first, plan, colors.Overflows ? colors.Reduced() : colors.ToPalette(), out _)[0];
        }
        var bytes = source.Bytes!;
        if (sheet is null) return ImageImport.FromImage(bytes, plan, out _);
        var firstCell = new SheetLayout([sheet.Cells[0]], sheet.Background, sheet.AssumedBackground);
        if (!own || ImageImport.ImageIsIndexed(bytes)) return SheetImport.ToSprites(bytes, firstCell, plan, out _)[0];
        var shared = SheetImport.SharedColors(bytes, sheet);
        return SheetImport.ToSprites(bytes, firstCell, plan, shared.Overflows ? shared.Reduced() : shared.ToPalette(), out _)[0];
    }

    /// <summary>Turns the dialog's choices into a plan for ImageImport.</summary>
    private static ImportPlan BuildPlan(ImportChoices c, Palette? previous)
    {
        if (c.BitDepth == BitDepthChoice.ThirtyTwoBit) return ImportPlan.TrueColor32();
        bool keep = c.IndexChoice == ImportIndexChoice.Keep;
        return c.PaletteChoice switch
        {
            // The dialog only offers these when the palette exists; fall back to
            // the image's own rather than guessing if one ever slips through.
            ImportPaletteChoice.Previous when previous is not null => ImportPlan.Given(previous, keep, c.KeepMask),
            ImportPaletteChoice.Specific when c.SpecificPalette is not null => ImportPlan.Given(c.SpecificPalette, keep, c.KeepMask),
            _ => ImportPlan.Own(),
        };
    }
}
