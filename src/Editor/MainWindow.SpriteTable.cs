#pragma warning disable CS8602
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sff;

namespace Editor;

/// <summary>
/// The sprite table is SpriteList itself now — a real virtualizing ListBox
/// (see MainWindow.axaml's ItemTemplate) instead of a hand-built Grid, so
/// Avalonia only ever measures/arranges the rows on screen. On a 1591-sprite
/// SFFv1 file this used to cost ~1s to build plus ~7s for Avalonia to lay
/// out the ~8000 resulting controls, every time the tab was (re)shown;
/// virtualization makes both costs O(visible rows) instead of O(sprites).
/// Selection highlighting is the ListBox's own :selected style now too —
/// no more hand-tracked "current row" cell list to keep in sync.
/// </summary>
public partial class MainWindow
{
    /// <summary>Rebuilds the row objects and hands them to SpriteList — cheap now that it's just plain data, not controls.
    /// Returns true when the rows were replaced, false when they were refreshed in place (so a
    /// caller must not assume the previous selection is gone).</summary>
    private bool RebuildSpriteTable()
    {
        if (SpriteList is null) return false;

        if (_file is null) { SpriteList.ItemsSource = null; return true; }

        // SFFv1 stores palettes as a chain: a sprite whose palette is the
        // previous carrier's reuses it ("Previous"); any other shows its S number.
        // SFFv2 always shows the key.
        bool v1 = SffVersions.IsV1(_file.Version);
        var externalOrdinals = ExternalPaletteOrdinals(_file);
        var v1Ordinals = V1PaletteOrdinals(_file);
        var spriteIndex = new Dictionary<Sprite, int>(_file.Sprites.Count);
        for (int s = 0; s < _file.Sprites.Count; s++) spriteIndex[_file.Sprites[s]] = s;

        var paletteText = new Dictionary<Sprite, string>(_file.Sprites.Count);
        Palette? previousPalette = null;
        foreach (var sprite in _file.Sprites)
        {
            // A true-color sprite carries no palette of its own; the column would
            // otherwise show the first palette the sprite happens to reference.
            if (sprite.Direct is not null)
            {
                int depth = sprite.SrcDepth is 24 or 32 ? sprite.SrcDepth : 32;
                paletteText[sprite] = $"{depth}-bit";
                continue;
            }
            if (sprite.Palette is not { } palette) { paletteText[sprite] = ""; continue; }
            if (v1 && sprite.SharesWith is null && ReferenceEquals(palette, previousPalette))
                paletteText[sprite] = "Previous";
            else if (palette.Source == PaletteSource.External)
                paletteText[sprite] = externalOrdinals.TryGetValue(palette, out int externalIndex) ? $"{ExternalGroup(palette)}{externalIndex}" : ExternalGroup(palette);
            else
                paletteText[sprite] = v1Ordinals.TryGetValue(palette, out int sn) ? $"S{sn}" : $"{palette.Group},{palette.Index}";
            // Pixel links carry no palette in v1, so they don't advance the chain.
            if (sprite.SharesWith is null) previousPalette = palette;
        }

        // Widths over every sprite, so filtering doesn't move the columns; X/Y fit any axis, as they change live.
        var flags = _file.Sprites.ToDictionary(s => s, s => SharedOrErrorText(s, spriteIndex));
        var widths = TextTableWidths(SpriteTableHeader, _file.Sprites.Select((s, i) => new[]
        {
            (i + 1).ToString(CultureInfo.InvariantCulture), s.Group.ToString(CultureInfo.InvariantCulture),
            s.Index.ToString(CultureInfo.InvariantCulture), "", "", paletteText[s], flags[s],
        }).ToList(), floors: ["", "", "", "-32768", "-32768"]);

        // Most edits leave the visible set alone: the rows would be identical apart from the
        // cells just edited. Refresh them where they lie. A fresh ItemsSource drops the list's
        // scroll offset and re-lays-out every container, which is what makes the table look like
        // it moves under the pointer.
        if (SpriteList.ItemsSource is IList<SpriteTableRow> existing && SameVisibleRows(existing))
        {
            foreach (var row in existing)
            {
                row.Widths = widths;
                row.Refresh(flags[row.Sprite], paletteText[row.Sprite]);
            }
            return false;
        }

        var rows = new List<SpriteTableRow>(_visible.Count);
        for (int row = 0; row < _visible.Count; row++)
        {
            var sprite = _file.Sprites[_visible[row]];
            rows.Add(new SpriteTableRow(sprite, row, _visible[row])
            {
                Flags = flags[sprite],
                PaletteText = paletteText[sprite],
                Widths = widths,
            });
        }
        SpriteList.ItemsSource = rows;
        return true;
    }

    /// <summary>True when the sprite at this position in the file is already one of the selected
    /// rows, so re-selecting it would only narrow an existing multi-selection.</summary>
    private bool SpriteRowSelected(int spriteIndex) =>
        _file is not null && spriteIndex >= 0 && spriteIndex < _file.Sprites.Count
        && SpriteList.SelectedItems is { } items
        && items.OfType<SpriteTableRow>().Any(row => _file.Sprites[spriteIndex] == row.Sprite);

    /// <summary>True when the rows already handed to the list are the visible set, in order:
    /// nothing was inserted, removed, reordered or filtered since they were built.</summary>
    private bool SameVisibleRows(IList<SpriteTableRow> rows)
    {
        if (_file is null || rows.Count != _visible.Count) return false;
        for (int i = 0; i < rows.Count; i++)
            if (!ReferenceEquals(rows[i].Sprite, _file.Sprites[_visible[i]])) return false;
        return true;
    }

    private void UpdateSpriteTableAxes(Sprite sprite) => UpdateSpriteTableAxes([sprite]);

    private void UpdateSpriteTableAxes(IEnumerable<Sprite> sprites)
    {
        if (SpriteList is null || SpriteList.ItemsSource is not IList<SpriteTableRow> rows) return;
        var changed = sprites.ToHashSet();
        foreach (var row in rows)
            if (changed.Contains(row.Sprite)) row.UpdateAxes(row.Sprite.AxisX, row.Sprite.AxisY);
    }

    // Shows the linked-to sprite's real position in the file (its "#" — what you'd
    // scroll to), not its Group/Number identity: t.Index is the sprite's own SFF-format
    // Number field, easy to mistake for a row position but not one, especially once a
    // filter or a large SFFv1 file makes "#" and "Number" diverge.
    private string SharedOrErrorText(Sprite s, Dictionary<Sprite, int> spriteIndex) =>
        s.DecodeError is not null ? "\u26a0 error"
        : s.SharesWith is { } t && spriteIndex.TryGetValue(t, out int i) ? (i + 1).ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>
    /// Clicks select rows, with Shift ranges and Ctrl toggles. Double-click opens
    /// the editor for the clicked field; Group and Number commit through the same
    /// field-edit undo path the old inline cells used. DoubleTapped lives on
    /// the cell Border, not the TextBlock (same hit-testing reason as the
    /// action list's template documents).
    /// </summary>
    private async void OnSpriteCellDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_file is null || sender is not Control c || c.DataContext is not SpriteTableRow row) return;
        var file = _file;
        string? field = c.Tag as string;

        try
        {
            if (field == "Palette")
            {
                if (file.Palettes.Count == 0)
                {
                    StatusText.Text = "No palettes are available in this sprite file.";
                    return;
                }
                if (SffVersions.IsV1(file.Version))
                {
                    if (row.Sprite.SharesWith is not null)
                    {
                        StatusText.Text = "A linked sprite uses its link target's palette; unlink it first.";
                        return;
                    }
                    var previous = file.PreviousPalette(row.Sprite);
                    bool usePrevious = previous is not null && ReferenceEquals(previous, row.Sprite.Palette);
                    var choice = await Dialogs.PromptSffV1PaletteChoice(this, previous is not null, usePrevious);
                    if (choice is null || !ReferenceEquals(_file, file)) return;
                    if (choice == SffV1PaletteChoice.Previous) ApplyUsePreviousPalette(file, row.Sprite);
                    else ApplyMakePaletteUnique(file, row.Sprite);
                    return;
                }

                var paletteEntries = file.Palettes
                    .Where(palette => palette.Source == PaletteSource.Embedded)
                    .ToArray();
                if (paletteEntries.Length == 0)
                {
                    StatusText.Text = "No embedded SFFv2 palettes are available. Create or duplicate one in the Palettes tab first.";
                    return;
                }
                var choices = paletteEntries.Select(palette => PaletteDisplayName(file, palette)).ToArray();
                int currentPalette = Array.FindIndex(paletteEntries, palette => ReferenceEquals(palette, row.Sprite.Palette));
                int? selected = await Dialogs.PromptSffV2PaletteChoice(this, choices, currentPalette);
                if (selected is not { } paletteIndex || !ReferenceEquals(_file, file)) return;
                if (paletteIndex < 0 || paletteIndex >= paletteEntries.Length) return;
                await AssignSpritePalette(row.Sprite, paletteEntries[paletteIndex]);
                return;
            }

            if (field == "Shared")
            {
                var choice = await Dialogs.PromptSpriteSharing(this);
                if (!ReferenceEquals(_file, file) || choice is null) return;
                switch (choice.Value)
                {
                    case SpriteShareDialogAction.Unlink:
                        UnshareSprite(row.Sprite);
                        break;
                    case SpriteShareDialogAction.Link:
                    {
                        var existingTarget = row.Sprite.SharesWith;
                        var targetChoice = await Dialogs.PromptSpriteLinkTarget(this,
                            existingTarget?.Group.ToString(CultureInfo.InvariantCulture) ?? "",
                            existingTarget?.Index.ToString(CultureInfo.InvariantCulture) ?? "",
                            SpriteFieldMax,
                            (group, image) => ValidateSpriteShareTarget(file, row.Sprite, group, image));
                        if (!ReferenceEquals(_file, file) || targetChoice is null) return;
                        if (targetChoice.Action == SpriteTargetDialogAction.PickFromList)
                        {
                            BeginPickShare(row.Sprite, row.VisibleRow);
                            break;
                        }
                        var target = file.Sprites.Single(s => s.Group == targetChoice.Group && s.Index == targetChoice.Image);
                        await ShareSpriteWith(row.Sprite, target);
                        break;
                    }
                }
                return;
            }

            if (field is not ("Group" or "Number" or "X" or "Y")) return;
            bool isGroup = field == "Group";
            bool isNumber = field == "Number";
            bool isX = field == "X";
            string fieldName = isGroup ? "group" : isNumber ? "number" : isX ? "X offset" : "Y offset";
            int currentValue = isGroup ? row.Sprite.Group : isNumber ? row.Sprite.Index : isX ? row.Sprite.AxisX : row.Sprite.AxisY;
            var result = await Dialogs.PromptText(this, $"Change {fieldName}",
                $"New {fieldName}:", currentValue.ToString(CultureInfo.InvariantCulture));
            if (result is null || !ReferenceEquals(_file, file)) return;
            if (!int.TryParse(result.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
            {
                await Dialogs.ShowMessage(this, "Not a Number", "Type a whole number.");
                return;
            }
            int minimum = isX || field == "Y" ? SpriteAxisMin : 0;
            int maximum = isX || field == "Y" ? SpriteAxisMax : SpriteFieldMax;
            if (val < minimum || val > maximum)
            {
                StatusText.Text = isX || field == "Y"
                    ? _settings.DebugMode ? "Axis offsets must be whole numbers." : "Axis offsets must be whole numbers from -32768 to 32767."
                    : _settings.DebugMode ? "Group and Number must be whole numbers of 0 or more." : "Group and Number must be whole numbers from 0 to 65535.";
                return;
            }
            CommitSpriteField(row.VisibleRow,
                before => isGroup ? (val, before.N, before.X, before.Y)
                    : isNumber ? (before.G, val, before.X, before.Y)
                    : isX ? (before.G, before.N, val, before.Y)
                    : (before.G, before.N, before.X, val));
        }
        catch (Exception ex)
        {
            // async void: an exception here has nowhere else to go — log it
            // explicitly rather than relying on it reaching AppDomain intact.
            CrashLog.Write("OnSpriteCellDoubleTapped", ex);
            await Dialogs.ShowMessage(this, "Something Went Wrong", ex.Message);
        }
    }

    // ---- Fill handle (Excel-style: drag a cell corner to copy down/up) ----

    private int _spriteFillFrom = -1, _spriteFillTo = -1;
    private int _spriteFillSelectionAnchor = -1;
    private string _spriteFillTag = "";
    private Point _spriteFillLastPos;
    private List<Sprite>? _spriteFillRows;
    private List<Sprite>? _spriteFillSelection;
    private SffFile? _spriteFillFile;
    private DispatcherTimer? _spriteFillScrollTimer;
    private bool _spriteFillHooked;

    private void OnSpriteFillHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle || handle.DataContext is not SpriteTableRow row) return;
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
        try
        {
            e.Handled = true;
            _spriteFillFile = _file;
            if (_spriteFillFile is null) { CancelSpriteFill(); return; }
            HookSpriteFillEvents();
            _spriteFillRows = SpriteList.ItemsSource is IEnumerable<SpriteTableRow> rows
                ? rows.Select(item => item.Sprite).ToList()
                : null;
            if (_spriteFillRows is null || row.VisibleRow < 0 || row.VisibleRow >= _spriteFillRows.Count)
            {
                CancelSpriteFill();
                return;
            }
            _spriteFillFrom = _spriteFillTo = row.VisibleRow;
            _spriteFillSelection = SelectedSpriteTargets();
            _spriteFillSelectionAnchor = _spriteSelectionAnchor;
            _spriteFillTag = (string?)handle.Tag ?? "";
            _spriteFillLastPos = e.GetPosition(SpriteList);
            // Capture on the list, not a recycled row, so virtualization is safe during a drag.
            e.Pointer.Capture(SpriteList);
            UpdateSpriteFillHighlight();
        }
        catch (Exception ex)
        {
            CancelSpriteFill();
            CrashLog.Write("OnSpriteFillHandlePressed", ex);
        }
    }

    private void HookSpriteFillEvents()
    {
        if (_spriteFillHooked) return;
        _spriteFillHooked = true;
        SpriteList.AddHandler(PointerMovedEvent, OnSpriteFillMoved, handledEventsToo: true);
        SpriteList.AddHandler(PointerReleasedEvent, OnSpriteFillReleased, handledEventsToo: true);
        SpriteList.AddHandler(PointerCaptureLostEvent, (_, _) => CancelSpriteFill(), handledEventsToo: true);
    }

    private void OnSpriteFillMoved(object? sender, PointerEventArgs e)
    {
        if (_spriteFillFrom < 0) return;
        try
        {
            _spriteFillLastPos = e.GetPosition(SpriteList);
            UpdateSpriteFillTarget();
        }
        catch (Exception ex)
        {
            CancelSpriteFill();
            CrashLog.Write("OnSpriteFillMoved", ex);
        }
    }

    private void UpdateSpriteFillTarget()
    {
        if (_spriteFillFrom < 0 || _spriteFillRows is not { Count: > 0 } rows) return;
        double height = SpriteList.Bounds.Height;
        bool above = _spriteFillLastPos.Y < 0, below = _spriteFillLastPos.Y > height;
        var point = new Point(4, Math.Clamp(_spriteFillLastPos.Y, 1, Math.Max(1, height - 1)));
        if (SpriteList.InputHitTest(point) is Visual hit
            && hit.FindAncestorOfType<ListBoxItem>(true) is { } item)
        {
            int row = SpriteList.IndexFromContainer(item);
            if (row >= 0 && row < rows.Count) _spriteFillTo = row;
        }
        if (above || below)
        {
            _spriteFillTo = Math.Clamp(_spriteFillTo + (above ? -1 : 1), 0, rows.Count - 1);
            SpriteList.ScrollIntoView(_spriteFillTo);
            _spriteFillScrollTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(Settings.Checked(
                nameof(Settings.FillScrollMilliseconds), _settings.FillScrollMilliseconds,
                Settings.DefaultFillScrollMilliseconds, value => value is >= 10 and <= 1000)),
                DispatcherPriority.Input, (_, _) =>
                {
                    try { UpdateSpriteFillTarget(); }
                    catch (Exception ex)
                    {
                        CancelSpriteFill();
                        CrashLog.Write("Sprite fill autoscroll", ex);
                    }
                });
            _spriteFillScrollTimer.Start();
        }
        else _spriteFillScrollTimer?.Stop();
        UpdateSpriteFillHighlight();
    }

    // Reapplies the highlight only when the fill range, tag, item source or realized rows change.
    // A pointer move within one row changes none of these, so it skips the visual-tree walk.
    private (int, int, int, string, object?) _spriteHighlightKey;
    private int[]? _spriteHighlightRealized;

    private void UpdateSpriteFillHighlight()
    {
        int low = Math.Min(_spriteFillFrom, _spriteFillTo), high = Math.Max(_spriteFillFrom, _spriteFillTo);
        var containers = SpriteList.GetRealizedContainers().ToList();
        var realized = containers.Select(c => (c.DataContext as SpriteTableRow)?.VisibleRow ?? -1).ToArray();
        var key = (low, high, _spriteFillFrom, _spriteFillTag, (object?)SpriteList.ItemsSource);
        if (key == _spriteHighlightKey && _spriteHighlightRealized is { } previous && realized.SequenceEqual(previous)) return;
        _spriteHighlightKey = key;
        _spriteHighlightRealized = realized;
        foreach (var container in containers)
        {
            bool rowIn = _spriteFillFrom >= 0 && container.DataContext is SpriteTableRow row
                && row.VisibleRow >= low && row.VisibleRow <= high;
            foreach (var handle in container.GetVisualDescendants().OfType<Border>()
                         .Where(border => border.Classes.Contains("spritefillhandle")))
                if (handle.Parent?.Parent is Border cell)
                    cell.Classes.Set("filltarget", rowIn && (string?)handle.Tag == _spriteFillTag);
        }
    }

    private async void OnSpriteFillReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_spriteFillFrom < 0) return;
        e.Handled = true;
        try { await EndSpriteFill(apply: true); }
        catch (Exception ex) { await Failed("OnSpriteFillReleased", ex); }
    }

    private async Task EndSpriteFill(bool apply)
    {
        if (_spriteFillFrom < 0) return;
        int from = _spriteFillFrom, to = _spriteFillTo;
        string tag = _spriteFillTag;
        var rows = _spriteFillRows;
        var file = _spriteFillFile;
        var selection = _spriteFillSelection;
        int selectionAnchor = _spriteFillSelectionAnchor;
        _spriteFillFrom = _spriteFillTo = _spriteFillSelectionAnchor = -1;
        _spriteFillTag = "";
        _spriteFillRows = null;
        _spriteFillSelection = null;
        _spriteFillFile = null;
        _spriteFillScrollTimer?.Stop();
        UpdateSpriteFillHighlight();
        if (file is not null && ReferenceEquals(_file, file) && selection is not null)
        {
            RestoreSpriteSelection(selection);
            _spriteSelectionAnchor = selectionAnchor;
        }
        if (apply && file is not null && rows is not null && from != to)
            await FillSpriteRows(file, rows, from, to, tag);
    }

    private void CancelSpriteFill()
    {
        if (_spriteFillFrom < 0 && _spriteFillRows is null && _spriteFillFile is null) return;
        var file = _spriteFillFile;
        var selection = _spriteFillSelection;
        int selectionAnchor = _spriteFillSelectionAnchor;
        _spriteFillFrom = _spriteFillTo = _spriteFillSelectionAnchor = -1;
        _spriteFillTag = "";
        _spriteFillRows = null;
        _spriteFillSelection = null;
        _spriteFillFile = null;
        _spriteFillScrollTimer?.Stop();
        UpdateSpriteFillHighlight();
        if (file is not null && ReferenceEquals(_file, file) && selection is not null)
        {
            RestoreSpriteSelection(selection);
            _spriteSelectionAnchor = selectionAnchor;
        }
    }

    private async Task FillSpriteRows(SffFile file, List<Sprite> rows, int from, int to, string tag)
    {
        if (!ReferenceEquals(_file, file) || from < 0 || from >= rows.Count) return;
        to = Math.Clamp(to, 0, rows.Count - 1);
        var source = rows[from];
        CrashLog.Trace($"Sprite fill: {tag} from {from} to {to}");
        int direction = Math.Sign(to - from);
        var before = new List<SpriteFillState>();
        var after = new List<SpriteFillState>();
        Palette? fillPalette = tag == "Palette" ? source.Palette : null;
        var targets = new List<(int Row, Sprite Sprite)>();
        for (int row = from + direction; row != to + direction; row += direction)
        {
            var sprite = rows[row];
            var old = SpriteFillState.Of(sprite);
            var next = old with
            {
                Group = tag == "Group" ? source.Group : old.Group,
                Number = tag == "Number"
                    ? (int)Math.Clamp((long)source.Index + row - from, 0L, SpriteFieldMax)
                    : old.Number,
                X = tag == "X" ? source.AxisX : old.X,
                Y = tag == "Y" ? source.AxisY : old.Y,
                Palette = tag == "Palette" ? fillPalette : old.Palette,
            };
            if (tag == "Palette") targets.Add((row, sprite));
            if (next == old) continue;
            before.Add(old);
            after.Add(next);
        }

        if (tag == "Palette")
        {
            string? problem = PaletteFillProblem(file, fillPalette, targets);
            if (problem is not null)
            {
                CrashLog.Error("Sprite palette fill", problem);
                await Dialogs.ShowMessage(this, "Palette Fill Needs Manual Changes", problem);
                return;
            }
            before.Clear();
            after.Clear();
            foreach (var (_, sprite) in targets)
            {
                var old = SpriteFillState.Of(sprite);
                var next = old with { Palette = fillPalette };
                if (next == old) continue;
                before.Add(old);
                after.Add(next);
            }
        }

        CrashLog.Trace($"Sprite fill: {tag}, {after.Count} changed rows");
        if (after.Count == 0)
        {
            StatusText.Text = "No sprite rows changed.";
            return;
        }

        void Apply(List<SpriteFillState> states)
        {
            foreach (var state in states) state.Apply();
        }
        Edit("fill sprite cells", () => Apply(after), () => Apply(before));
        StatusText.Text = $"Filled {after.Count} sprite row{(after.Count == 1 ? "" : "s")}.";
    }

    private string? PaletteFillProblem(SffFile file, Palette? palette, List<(int Row, Sprite Sprite)> targets)
    {
        if (targets.All(target => ReferenceEquals(target.Sprite.Palette, palette))) return null;
        if (palette is null)
            return "The source row has no palette to copy. Make a palette assignment manually.";
        if (palette.Source == PaletteSource.External)
            return "The source palette is external and must be embedded or prepared first. Assign it manually.";

        bool v1 = SffVersions.IsV1(file.Version);
        foreach (var (row, sprite) in targets)
        {
            if (ReferenceEquals(sprite.Palette, palette)) continue;
            string identity = $"row {row + 1} ({sprite.Group},{sprite.Index})";
            if (v1 && sprite.SharesWith is not null)
                return $"{identity} is linked and cannot carry its own SFFv1 palette. Unlink it and assign the palette manually.";
            if (sprite.Direct is not null && sprite.Indexed is null)
                return $"{identity} is true color and needs a conversion choice. Convert it and assign the palette manually.";
            if (sprite.Indexed is { Length: > 0 } indexes && indexes.Max(index => (int)index) >= palette.Colors.Length)
                return $"The source palette is too short for {identity}'s indexes. Prepare and assign the palette manually.";
        }
        if (v1 && HasSffV1AlphaLoss(palette.Colors))
            return "This SFFv1 palette contains alpha that cannot be kept. Handle the palette manually.";
        return null;
    }

    private readonly record struct SpriteFillState(
        Sprite Sprite, int Group, int Number, int X, int Y, Palette? Palette)
    {
        public static SpriteFillState Of(Sprite sprite) =>
            new(sprite, sprite.Group, sprite.Index, sprite.AxisX, sprite.AxisY, sprite.Palette);

        public void Apply()
        {
            Sprite.Group = Group;
            Sprite.Index = Number;
            Sprite.AxisX = X;
            Sprite.AxisY = Y;
            Sprite.Palette = Palette;
        }
    }

    private static string? ValidateSpriteShareTarget(SffFile file, Sprite source, int group, int image)
    {
        var matches = file.Sprites.Where(s => s.Group == group && s.Index == image).ToArray();
        if (matches.Length == 0) return "No sprite has that Group and Image.";
        if (matches.Length > 1) return "More than one sprite has that Group and Image. Pick the intended row from the list.";
        var target = matches[0];
        if (ReferenceEquals(source, target)) return "A sprite cannot link to itself.";
        if (file.PositionOf(target) > file.PositionOf(source))
        {
            CrashLog.Error("Sprite linking", $"refused {source.Group},{source.Index} to later sprite {target.Group},{target.Index}");
            return $"Sprite {target.Group},{target.Index} appears after {source.Group},{source.Index} in the sprite list. Choose an earlier sprite.";
        }
        if (!target.HasPixels) return "That sprite has no decoded pixels to link.";
        for (Sprite? current = target; current is not null; current = current.SharesWith)
            if (ReferenceEquals(current, source)) return "That target would create a sprite-link loop.";
        return null;
    }

    /// <summary>Selects the row being edited (so the rest of the UI follows it) and commits through the normal field-edit undo path.</summary>
    private void CommitSpriteField(int row, Func<(int G, int N, int X, int Y), (int G, int N, int X, int Y)> change)
    {
        if (_file is null || row < 0 || row >= _visible.Count) { RebuildSpriteTable(); return; }
        if (CurrentSpriteRow() != row) SelectOnlySpriteRow(row);
        if (_current is not { } sprite) { RebuildSpriteTable(); return; }

        var before = (sprite.Group, sprite.Index, sprite.AxisX, sprite.AxisY);
        var after = change(before);
        if (before == after) { RebuildSpriteTable(); return; }

        Edit("field change", () => Apply(sprite, after), () => Apply(sprite, before), $"fields:{_file.PositionOf(sprite)}");
    }

    // ---- selection ---------------------------------------------------------

    private List<Sprite> SelectedSpriteTargets() =>
        SpriteList.SelectedItems is { } selectedItems
            ? selectedItems.OfType<SpriteTableRow>().Select(row => row.Sprite).ToList()
            : [];

    private void SelectWholeGroup()
    {
        var selected = SelectedSpriteTargets();
        if (selected.Count == 0 || _file is null) return;
        int group = selected[0].Group;
        var groupSprites = _file.Sprites.Where(s => s.Group == group).ToList();
        if (groupSprites.Count <= 1) return;
        _suppressEvents = true;
        try
        {
            SpriteList.SelectedItems.Clear();
            foreach (var sprite in groupSprites)
            {
                var row = SpriteList.ItemsSource is IEnumerable<SpriteTableRow> rows
                    ? rows.FirstOrDefault(r => r!.Sprite is not null && ReferenceEquals(r!.Sprite, sprite))
                    : null;
                if (row is not null && row!.Sprite is not null)
                    SpriteList.SelectedItems.Add(row);
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    /// <summary>The sprite table's selected rows, for putting a multi-selection back after AfterEdit
    /// rebuilds the rows and re-selects the current sprite alone.</summary>
    private List<int> SelectedRows() =>
        SpriteList.SelectedItems is { } items
            ? items.OfType<SpriteTableRow>().Select(row => row.VisibleRow).ToList()
            : [];

    private int CurrentSpriteRow() =>
        _file is not null && _current is not null
            ? _visible.IndexOf(_file.PositionOf(_current))
            : SpriteList.SelectedIndex;

    private void SelectOnlySpriteRow(int row, bool suppressEvents = false)
    {
        ApplySpriteRowSelection([row], suppressEvents);
        _spriteSelectionAnchor = row;
    }

    private async Task<bool> SelectSpriteRowForContext(object? source)
    {
        if (source is not Visual visual || visual.FindAncestorOfType<ListBoxItem>(true) is not { } item)
            return true;
        int row = SpriteList.IndexFromContainer(item);
        if (row < 0 || row >= _visible.Count || !await ActivateSpriteRow(row)) return false;

        bool alreadySelected = SpriteList.SelectedItems is { } selectedItems
            && selectedItems.OfType<SpriteTableRow>().Any(selected => selected.VisibleRow == row);
        if (alreadySelected)
            _spriteSelectionAnchor = row;
        else
            SelectOnlySpriteRow(row);
        RefreshAssignPaletteButton();
        return true;
    }

    private void ApplySpriteRowSelection(IEnumerable<int> selectedRows, bool suppressEvents)
    {
        if (SpriteList.ItemsSource is not IList<SpriteTableRow> rows
            || SpriteList.SelectedItems is not { } selectedItems) return;
        bool wasSuppressed = _suppressEvents;
        if (suppressEvents) _suppressEvents = true;
        try
        {
            var wanted = selectedRows.Distinct().Order().ToList();
            selectedItems.Clear();
            foreach (int row in wanted)
                if (row >= 0 && row < rows.Count) selectedItems.Add(rows[row]);
        }
        finally { _suppressEvents = wasSuppressed; }
    }

    private async Task<bool> ActivateSpriteRow(int row)
    {
        if (_file is null || row < 0 || row >= _visible.Count) return false;
        var target = _file.Sprites[_visible[row]];
        if (_pickingFor is not null)
        {
            await CompletePick(target);
            return false;
        }

        if (!ReferenceEquals(_current, target))
        {
            if (_paintBaselineSprite is { } pending && !ReferenceEquals(pending, target)
                && !await ResolvePendingPaintBeforeSwitch())
                return false;
            if (_editingPalette is not null) DiscardPendingColorAdjust();
            _current = target;
            _palettePreviewSprite = null;
            RefreshSprite();
        }
        return true;
    }

    // ---- drag to reorder ---------------------------------------------------
    // Same press/move/release-armed drag as the action list (MainWindow.Animations.cs),
    // carrying the source's absolute position in _file.Sprites (not a filtered row
    // index) as the drag payload. Reordering never touches pixel sharing: SharesWith
    // is an object reference, and positions are only computed fresh at write time
    // (SffWriter), so moving a sprite around the list re-links everything that shares
    // with it (or with anything else) automatically — nothing here needs to "fix up"
    // a link, unlike a format where sharing is stored as a raw index.

    private const string SpriteDragFormat = "ime/sprite-index";
    private bool _spriteDragArmed;
    private int _spriteDragFromIndex = -1;
    private int _spritePointerPressToken;
    private Point _spriteDragPressPoint;

    private async void OnSpriteRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_file is null || sender is not Control c || c.DataContext is not SpriteTableRow row) return;
        var p = e.GetCurrentPoint(c);
        if (!p.Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount >= 2) { _spriteDragArmed = false; return; }
        int pressToken = ++_spritePointerPressToken;
        e.Pointer.Capture(c);

        try
        {
            if (!await ActivateSpriteRow(row.VisibleRow) || pressToken != _spritePointerPressToken) return;

            var selected = SpriteList.SelectedItems is { } selectedItems
                ? selectedItems.OfType<SpriteTableRow>().Select(item => item.VisibleRow).ToHashSet()
                : new HashSet<int>();
            bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            int anchor = _spriteSelectionAnchor;
            // The anchor only means something while it is still one of the selected rows. Selection
            // changes without ever going through SelectOnlySpriteRow — an edit rebuilds the rows,
            // undo restores an older set, a right-click elsewhere re-anchors on its own row — which
            // leaves a stale anchor behind, and extending from that picks a range nobody asked for.
            if (shift && (anchor < 0 || anchor >= _visible.Count || !selected.Contains(anchor)))
                anchor = CurrentSpriteRow() >= 0 ? CurrentSpriteRow() : row.VisibleRow;

            if (shift)
            {
                if (!control) selected.Clear();
                for (int i = Math.Min(anchor, row.VisibleRow); i <= Math.Max(anchor, row.VisibleRow); i++)
                    selected.Add(i);
            }
            else if (control)
            {
                if (!selected.Add(row.VisibleRow)) selected.Remove(row.VisibleRow);
                _spriteSelectionAnchor = row.VisibleRow;
            }
            else
            {
                selected.Clear();
                selected.Add(row.VisibleRow);
                _spriteSelectionAnchor = row.VisibleRow;
            }

            ApplySpriteRowSelection(selected, suppressEvents: true);
            RefreshAssignPaletteButton();
            _spriteDragFromIndex = _file.PositionOf(row.Sprite);
            _spriteDragPressPoint = p.Position;
            _spriteDragArmed = _spriteDragFromIndex >= 0;
        }
        catch (Exception ex)
        {
            await Failed("OnSpriteRowPointerPressed", ex);
        }
    }

    /// <summary>Past a small threshold, turns an armed press into a real drag carrying the source's absolute position.</summary>
    private async void OnSpriteRowPointerMoved(object? sender, PointerEventArgs e)
    {
        try
        {
            if (!_spriteDragArmed || sender is not Control c) return;
            var p = e.GetPosition(c);
            double dx = p.X - _spriteDragPressPoint.X, dy = p.Y - _spriteDragPressPoint.Y;
            if (dx * dx + dy * dy < 36) return; // 6px, squared
            _spriteDragArmed = false;

            var data = new DataObject();
            data.Set(SpriteDragFormat, _spriteDragFromIndex);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            await Failed("OnSpriteRowPointerMoved", ex);
        }
    }

    private void OnSpriteRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _spriteDragArmed = false;
        _spritePointerPressToken++;
    }

    private void OnSpriteListDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.Data.Contains(SpriteDragFormat) ? DragDropEffects.Move : DragDropEffects.None;

    private void OnSpriteListDrop(object? sender, DragEventArgs e)
    {
        if (_file is null || e.Data.Get(SpriteDragFormat) is not int from) return;
        // IndexFromContainer is a position in the (possibly filtered) ItemsSource;
        // _visible already maps that straight to an absolute _file.Sprites index,
        // so — unlike the action list — there's no need to go through object
        // identity to translate it.
        int to = _file.Sprites.Count;
        if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(true) is { } item)
        {
            int row = SpriteList.IndexFromContainer(item);
            if (row >= 0 && row < _visible.Count) to = _visible[row];
        }
        MoveSprite(from, to);
    }

    private void MoveSprite(int from, int to)
    {
        if (_file is null || from < 0 || from >= _file.Sprites.Count) return;
        to = Math.Clamp(to, 0, _file.Sprites.Count);
        if (to == from || to == from + 1) return; // no real move

        var file = _file;
        var sprite = file.Sprites[from];

        // `to` is a drop boundary, so moving down inserts one slot before it.
        // Undo and redo use final list positions instead; find the sprite by
        // identity each time so a prior move cannot make a captured index stale.
        int destination = to > from ? to - 1 : to;
        void ApplyAt(int target)
        {
            int oldPosition = file.Sprites.IndexOf(sprite);
            CrashLog.Trace($"[sprite reorder] apply: group={sprite.Group}, number={sprite.Index}, target={target}, actual={oldPosition}, count={file.Sprites.Count}");
            if (oldPosition < 0) throw new InvalidOperationException("The sprite being reordered is no longer in the file.");
            file.Sprites.RemoveAt(oldPosition);
            file.Sprites.Insert(Math.Clamp(target, 0, file.Sprites.Count), sprite);
            int occurrences = file.Sprites.Count(s => ReferenceEquals(s, sprite));
            CrashLog.Trace($"[sprite reorder] applied: group={sprite.Group}, number={sprite.Index}, position={file.Sprites.IndexOf(sprite)}, occurrences={occurrences}, count={file.Sprites.Count}");
        }
        Edit("reorder sprites", () => ApplyAt(destination), () => ApplyAt(from));
    }
}
