using System.Collections.ObjectModel;
using System.Globalization;
using Air;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Editor;

/// <summary>
/// Spreadsheet-style frame editor: one row per frame, one column per AIR
/// field, in the format's own positional order (Group, Number, Off X, Off Y,
/// Time, then the optional tail — Flip, Blend, Scale X, Scale Y, Angle).
/// FrameTableList is a real virtualizing ListBox (see FrameTableRow and the
/// ItemTemplate in MainWindow.axaml) rather than a hand-built Grid, so only
/// the rows on screen are ever realized as controls — the same fix applied
/// to the sprite table, for the same reason (one row of raw controls per
/// frame was fine at a handful of frames, not at a few hundred).
///
/// An optional field's cell is blank when it's at the "not written" default
/// (Blend.None, Scale 1, Angle 0) — matching AirWriter's own trailing-blank
/// trim, not a new rule invented here. Every text cell but Blend opens one shared
/// editor on a single click; Enter or losing focus commits, going through the
/// same EditFrame undo path as every other frame edit, and Escape cancels. Blend is read-only here and opens the Blend dialog on a single
/// click or right-click instead (its own Source/Destination values don't
/// fit a single text field cleanly). H/V are checkboxes instead of text.
///
/// Deliberately a third option alongside List and Text, not a replacement —
/// still being tried out.
/// </summary>
public partial class MainWindow
{
    private bool _frameTableActive = true;
    private readonly ObservableCollection<FrameTableRow> _frameTableRows = [];

    private static readonly string[] FrameTableHeaders = ["Frame", "Group", "Number", "Off X", "Off Y", "Time", "H", "V", "Blend", "Scale X", "Scale Y", "Angle"];
    /// <summary>Layout floors, as the header's MinWidths were in XAML.</summary>
    private static readonly double[] FrameTableMinWidths = [48, 52, 52, 52, 52, 48, 26, 26, 76, 58, 58, 58];

    /// <summary>
    /// One width per column fitting its header and every row's text, set explicitly on the header and each
    /// row's cells. SharedSizeGroup did not keep the virtualized rows aligned with the header.
    /// </summary>
    private double[] FrameTableWidths(List<FrameTableRow> rows, bool updateHeader = true)
    {
        double Size(string key, double fallback) => this.TryFindResource(key, out var v) && v is double d ? d : fallback;
        double head = Size("UiFont11", 11), cell = Size("UiFont12", 12), number = Size("ControlContentThemeFontSize", 14);
        double Measure(string s, double size, Avalonia.Media.FontWeight weight) =>
            s.Length == 0 ? 0 : new Avalonia.Media.TextFormatting.TextLayout(s, new Avalonia.Media.Typeface(FontFamily, weight: weight), size, null).Width;

        var widths = new double[FrameTableHeaders.Length];
        for (int i = 0; i < widths.Length; i++)
            widths[i] = Math.Max(FrameTableMinWidths[i], Measure(FrameTableHeaders[i], head, Avalonia.Media.FontWeight.Bold) + 8 + 1); // padding 4+4, border 1
        foreach (var r in rows)
        {
            string[] texts = [r.Number, r.GroupText, r.IndexText, r.OffsetXText, r.OffsetYText, r.DurationText, "", "",
                r.BlendText, r.ScaleXText, r.ScaleYText, r.AngleText];
            widths[0] = Math.Max(widths[0], Measure(texts[0], number, Avalonia.Media.FontWeight.Normal) + 8 + 10 + 1); // room for the "L" tag
            for (int i = 1; i < texts.Length; i++)
                widths[i] = Math.Max(widths[i], Measure(texts[i], cell, Avalonia.Media.FontWeight.Normal) + 4 + 8 + 1); // TextBox padding 2+2, caret room, border 1
        }
        for (int i = 0; i < widths.Length; i++) widths[i] = Math.Ceiling(widths[i]);

        if (updateHeader && FrameTableHeader is not null)
            foreach (var child in FrameTableHeader.Children)
                if (child is Border b && Grid.GetColumn(b) is var c && c < widths.Length) b.Width = widths[c];
        return widths;
    }

    private bool IsWrap(int r, SortedSet<int> marks) =>
        _action is { } a && r == a.Elements.Count - 1 && a.Elements[r].Duration > 0 && marks.Contains(a.LoopStart);

    private FrameTableRow FrameTableRowFor(int r)
    {
        var el = _action!.Elements[r];
        return new FrameTableRow(r)
        {
            Number = (r + 1).ToString(CultureInfo.InvariantCulture),
            IsLoopFrame = r == _action.LoopStart && _action.LoopStart > 0,
            InterpOffset = r > 0 && _action.InterpolateOffset.Contains(r),
            InterpScale = r > 0 && _action.InterpolateScale.Contains(r),
            InterpAngle = r > 0 && _action.InterpolateAngle.Contains(r),
            InterpBlend = r > 0 && _action.InterpolateBlend.Contains(r),
            WrapOffset = IsWrap(r, _action.InterpolateOffset),
            WrapScale = IsWrap(r, _action.InterpolateScale),
            WrapAngle = IsWrap(r, _action.InterpolateAngle),
            WrapBlend = IsWrap(r, _action.InterpolateBlend),
            GroupText = el.Group.ToString(CultureInfo.InvariantCulture),
            IndexText = el.Index.ToString(CultureInfo.InvariantCulture),
            OffsetXText = el.OffsetX.ToString(CultureInfo.InvariantCulture),
            OffsetYText = el.OffsetY.ToString(CultureInfo.InvariantCulture),
            DurationText = el.Duration.ToString(CultureInfo.InvariantCulture),
            FlipH = el.FlipH,
            FlipV = el.FlipV,
            BlendText = el.Blend.Mode == BlendMode.None ? "" : el.Blend.DisplayText(),
            ScaleXText = el.ScaleX == 1f ? "" : el.ScaleX.ToString(CultureInfo.InvariantCulture),
            ScaleYText = el.ScaleY == 1f ? "" : el.ScaleY.ToString(CultureInfo.InvariantCulture),
            AngleText = el.Angle == 0f ? "" : el.Angle.ToString(CultureInfo.InvariantCulture),
        };
    }

    private void RefreshFrameTableRow(int frameRow)
    {
        if (!_frameTableActive || _action is null || FrameTableList is null || frameRow < 0 || frameRow >= _frameTableRows.Count) return;
        var row = FrameTableRowFor(frameRow);
        var target = _frameTableRows[frameRow];
        var widths = target.Widths;
        var required = FrameTableWidths([row], updateHeader: false);
        if (required.Where((width, column) => width > widths[column]).Any())
        {
            RebuildFrameTable();
            return;
        }
        row.Widths = widths;
        target.CopyFrom(row); // in place: the row keeps its controls, selection and focus
    }

    /// <summary>A replaced or rebuilt row loses its selection and focus; puts both back.
    /// `keep` is the multi-selection to restore; it's dropped unless it holds the current frame.</summary>
    private void RestoreFrameTableSelection(IReadOnlyCollection<int>? keep = null)
    {
        if (!_frameTableActive || FrameTableList.ItemsSource is null) return;
        int frame = FrameList.SelectedIndex;
        if (frame >= 0 && frame < _frameTableRows.Count)
        {
            IReadOnlyCollection<int> wanted = keep is not null && keep.Contains(frame) ? keep : [frame];
            if (!SelectedFrameRows().SequenceEqual(wanted.Where(r => r < _frameTableRows.Count).Distinct().Order()))
                ApplyFrameRowSelection(wanted);
        }
        FocusFrameTableRow();
    }

    // ---- multi-selection ---------------------------------------------------
    // FrameList.SelectedIndex stays the one current frame; the table's selected rows are the
    // frames Remove, Duplicate, Move and Copy act on. The current frame is always one of them.

    /// <summary>The table's selected frames, in order.</summary>
    private List<int> SelectedFrameRows() =>
        FrameTableList?.SelectedItems is { } items
            ? items.OfType<FrameTableRow>().Select(r => r.FrameRow).Distinct().Order().ToList()
            : [];

    /// <summary>Selects exactly these frames in the table without firing selection handlers.</summary>
    private void ApplyFrameRowSelection(IEnumerable<int> frames)
    {
        if (FrameTableList.ItemsSource is null || FrameTableList.SelectedItems is not { } items) return;
        bool wasSuppressed = _suppressEvents;
        _suppressEvents = true;
        try
        {
            var wanted = frames.Where(r => r >= 0 && r < _frameTableRows.Count).Distinct().Order().ToList();
            items.Clear();
            foreach (int r in wanted) items.Add(_frameTableRows[r]);
        }
        finally { _suppressEvents = wasSuppressed; }
        UpdateFrameButtons();
    }

    /// <summary>The frames a command acts on: the selection when it holds the current frame, else the current frame.</summary>
    private List<int> SelectedFrameTargets()
    {
        int current = FrameList.SelectedIndex;
        if (_action is null || current < 0 || current >= _action.Elements.Count) return [];
        List<int> rows = _frameTableActive ? SelectedFrameRows().Where(r => r < _action.Elements.Count).ToList() : [];
        return rows.Contains(current) ? rows : [current];
    }

    /// <summary>Right-click inside the selection keeps it and makes that row current; elsewhere selects only that row.</summary>
    private void SelectFrameRowForContext(object? source)
    {
        if (source is not Visual v || v.FindAncestorOfType<ListBoxItem>(true)?.DataContext is not FrameTableRow row) return;
        if (!SelectedFrameRows().Contains(row.FrameRow)) ApplyFrameRowSelection([row.FrameRow]);
        if (FrameList.SelectedIndex != row.FrameRow) FrameList.SelectedIndex = row.FrameRow;
    }

    private void SelectAllFrames()
    {
        if (!_frameTableActive || _action is null || _action.Elements.Count == 0) return;
        ApplyFrameRowSelection(Enumerable.Range(0, _action.Elements.Count));
    }

    /// <summary>Row last pressed in the table, so a Ctrl or Shift click makes that row the current frame.</summary>
    private int _framePressRow = -1;

    private void OnFrameTablePressed(object? sender, PointerPressedEventArgs e) =>
        _framePressRow = RowAt(e.GetPosition(FrameTableList)) ?? -1;

    // Posted: selection may change on release, after this tunnel handler.
    private void OnFrameTableReleased(object? sender, PointerReleasedEventArgs e) =>
        Dispatcher.UIThread.Post(() => _framePressRow = -1);

    /// <summary>Focuses the selected row once layout settles, if focus is still with the table: nowhere,
    /// the (now hidden) cell editor, or a row. Focus moved to another control is left alone.</summary>
    private void FocusFrameTableRow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_editorOpen || !FrameTableList.IsEffectivelyVisible) return;
            if (TopLevel.GetTopLevel(FrameTableList)?.FocusManager?.GetFocusedElement() is Visual now && now != FrameCellEditor
                && now.FindAncestorOfType<ListBox>(true) != FrameTableList) return;
            if (FrameTableList.ContainerFromIndex(FrameList.SelectedIndex) is Control row) row.Focus();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Rebuilds the row objects and hands them to FrameTableList — cheap now that it's just plain data, not controls.</summary>
    private void RebuildFrameTable()
    {
        if (!_frameTableActive || FrameTableList is null) return;
        if (_action is null) { FrameTableList.ItemsSource = null; _frameTableAction = null; return; }
        // A multi-selection carries over only within the same action.
        var keep = ReferenceEquals(_frameTableAction, _action) ? SelectedFrameRows() : null;
        _frameTableAction = _action;

        // The loop row sits at the loop point, between the frames — a marker
        // in the sequence rather than a summary at the end.
        var rows = new List<FrameTableRow>(_action.Elements.Count + 1);
        for (int r = 0; r < _action.Elements.Count; r++) rows.Add(FrameTableRowFor(r));

        var widths = FrameTableWidths(rows);

        // Existing rows update in place and only the tail is added or removed; clearing would discard every row's controls.
        _suppressEvents = true;
        while (_frameTableRows.Count > rows.Count) _frameTableRows.RemoveAt(_frameTableRows.Count - 1);
        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].Widths = widths;
            if (i < _frameTableRows.Count) _frameTableRows[i].CopyFrom(rows[i]);
            else _frameTableRows.Add(rows[i]);
        }
        if (!ReferenceEquals(FrameTableList.ItemsSource, _frameTableRows)) FrameTableList.ItemsSource = _frameTableRows;
        _suppressEvents = false;
        RestoreFrameTableSelection(keep);
    }

    /// <summary>The action the table's rows were last built for.</summary>
    private AnimationAction? _frameTableAction;

    /// <summary>
    /// Picks the current frame from the table's selection: the clicked row, else the row keyboard focus
    /// moved to, else the current frame if still selected, else the first selected.
    /// </summary>
    private void OnFrameTableSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _fillFrom >= 0 || FrameTableList is null) return;
        UpdateFrameButtons();
        var selected = SelectedFrameRows();
        int current = FrameList.SelectedIndex;
        int pressed = _framePressRow;
        _framePressRow = -1;
        if (selected.Count == 0)
        {
            // Ctrl+click on the last selected row: keep the current frame selected. Posted, not mid-change.
            Dispatcher.UIThread.Post(() => { if (SelectedFrameRows().Count == 0) RestoreFrameTableSelection(); });
            return;
        }
        int focused = TopLevel.GetTopLevel(FrameTableList)?.FocusManager?.GetFocusedElement() is Visual v && v.FindAncestorOfType<ListBoxItem>(true) is { } item
            && item.DataContext is FrameTableRow focusedRow ? focusedRow.FrameRow : -1;
        int next = selected.Contains(pressed) ? pressed
            : selected.Contains(focused) ? focused
            : selected.Contains(current) ? current
            : selected[0];
        if (next != current) FrameList.SelectedIndex = next;
    }

    // ---- cell editor: one shared TextBox over the clicked text cell ----

    private bool _editorOpen;
    private FrameTableRow? _editorRow;

    /// <summary>A click on a text cell opens the shared editor over it. The cells are plain TextBlocks.</summary>
    private void OnFrameCellTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border cell || cell.DataContext is not FrameTableRow row || cell.Tag is not string tag) return;
        if (e.Source is Control { Classes: var c } && c.Contains("fillhandle")) return; // a fill drag, not a click
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return; // selecting rows
        e.Handled = true;
        OpenFrameEditor(cell, row, tag);
    }

    private void OpenFrameEditor(Border cell, FrameTableRow row, string tag)
    {
        HookFillHandleHide();
        CommitFrameEditor(); // a click on another cell commits the one being edited
        if (cell.TranslatePoint(new Point(0, 0), FrameTableList) is not { } origin) return;
        FrameCellEditor.Margin = new Thickness(origin.X, origin.Y, 0, 0);
        FrameCellEditor.Width = cell.Bounds.Width;
        FrameCellEditor.Height = cell.Bounds.Height;
        FrameCellEditor.Tag = tag;
        FrameCellEditor.Text = FrameTableText(row, tag);
        _editorRow = row;
        _editorOpen = true;
        FrameCellEditor.IsVisible = true;
        FrameCellEditor.Focus();
        FrameCellEditor.SelectAll();
    }

    /// <summary>Writes the typed text into the row, then commits it through the same path the TextBox cells used.</summary>
    private void CommitFrameEditor()
    {
        if (!_editorOpen || _editorRow is not { } row) return;
        _editorOpen = false;
        SetFrameRowText(row, (string?)FrameCellEditor.Tag ?? "", FrameCellEditor.Text ?? "");
        FrameCellEditor.IsVisible = false; // LostFocus fires from here, but _editorOpen is already false
        CommitFrameCell(FrameCellEditor, row);
        // An unchanged value commits nothing, so refresh the row to drop the typed text.
        if (_action is not null && row.FrameRow < _action.Elements.Count && row.FrameRow < _frameTableRows.Count)
            RefreshFrameTableRow(row.FrameRow);
    }

    private void CloseFrameEditor()
    {
        _editorOpen = false;
        FrameCellEditor.IsVisible = false;
        FocusFrameTableRow();
    }

    private void OnFrameEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; CloseFrameEditor(); return; }
        if (e.Key is not (Key.Enter or Key.Tab) || _editorRow is not { } row) return;
        e.Handled = true;
        string tag = (string?)FrameCellEditor.Tag ?? "";
        // Enter just confirms; Tab moves right, Shift+Tab left.
        CommitFrameEditor();
        if (e.Key == Key.Tab) EditNextFrameCell(row.FrameRow, tag, e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        FocusFrameTableRow(); // does nothing if a next cell opened
    }

    /// <summary>The text cells, in the order Tab walks them. Blend and H/V aren't typed into, so Tab skips them.</summary>
    private static readonly string[] EditableFrameFields = ["Group", "Index", "OffsetX", "OffsetY", "Duration", "ScaleX", "ScaleY", "Angle"];
    private bool _navigating;

    /// <summary>Opens the editor on the cell `dField` fields across, wrapping at a row's ends. Stays put at the table's edges.</summary>
    private void EditNextFrameCell(int frameRow, string tag, int dField)
    {
        if (_action is null) return;
        int field = Array.IndexOf(EditableFrameFields, tag) + dField;
        int r = frameRow;
        if (field >= EditableFrameFields.Length) { field = 0; r++; }
        else if (field < 0) { field = EditableFrameFields.Length - 1; r--; }
        if (r < 0 || r >= _action.Elements.Count) return;
        string nextTag = EditableFrameFields[field];

        _navigating = true; // the scroll below must not commit the edit it is about to open
        try
        {
            FrameTableList.ScrollIntoView(r);
            FrameTableList.UpdateLayout();
            var item = FrameTableList.GetRealizedContainers().FirstOrDefault(c => c.DataContext is FrameTableRow x && x.FrameRow == r);
            var cell = item?.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("cell") && (string?)b.Tag == nextTag);
            if (cell?.DataContext is FrameTableRow target) OpenFrameEditor(cell, target, nextTag);
        }
        finally { _navigating = false; }
    }

    private void OnFrameEditorLostFocus(object? sender, RoutedEventArgs e) => CommitFrameEditor();

    private static string FrameTableText(FrameTableRow row, string tag) => tag switch
    {
        "Group" => row.GroupText,
        "Index" => row.IndexText,
        "OffsetX" => row.OffsetXText,
        "OffsetY" => row.OffsetYText,
        "Duration" => row.DurationText,
        "ScaleX" => row.ScaleXText,
        "ScaleY" => row.ScaleYText,
        "Angle" => row.AngleText,
        _ => "",
    };

    private static void SetFrameRowText(FrameTableRow row, string tag, string text)
    {
        switch (tag)
        {
            case "Group": row.GroupText = text; break;
            case "Index": row.IndexText = text; break;
            case "OffsetX": row.OffsetXText = text; break;
            case "OffsetY": row.OffsetYText = text; break;
            case "Duration": row.DurationText = text; break;
            case "ScaleX": row.ScaleXText = text; break;
            case "ScaleY": row.ScaleYText = text; break;
            case "Angle": row.AngleText = text; break;
        }
    }

    /// <summary>
    /// The only way to change Blend: no inline typing on this cell (see the
    /// XAML comment for why).
    /// </summary>
    private async void OnFrameBlendTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (sender is not Control control || control.DataContext is not FrameTableRow row) return;
            if (e.Source is Control { Classes: var c } && c.Contains("fillhandle")) return; // a fill drag, not a click
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return; // selecting rows
            e.Handled = true;
            await OpenBlendDialog(row);
        }
        catch (Exception ex)
        {
            await Failed("OnFrameBlendTapped", ex);
        }
    }

    private void OnFrameBlendContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not FrameTableRow row) return;
        ShowContextMenu(control, ("Edit Blend", () => _ = OpenBlendDialog(row)));
    }

    /// <summary>Commits through the same EditFrame undo path CommitTableEdit
    /// always uses.</summary>
    private async Task OpenBlendDialog(FrameTableRow row)
    {
        if (_action is null || row.FrameRow < 0 || row.FrameRow >= _action.Elements.Count) return;
        var current = _action.Elements[row.FrameRow].Blend;
        if (await Dialogs.PromptBlend(this, current) is not { } blend) return;
        CommitTableEdit(row.FrameRow, s => s with { Blend = blend });
    }

    /// <summary>
    /// Reads the already-typed text from the row's own bound property rather
    /// than the control's .Text — the TwoWay binding syncs that on every
    /// keystroke, same as any other cell, so this doesn't depend on a given
    /// control type's own focus/commit quirks.
    /// </summary>
    private void CommitFrameCell(Control control, FrameTableRow row)
    {
        string tag = (string?)control.Tag ?? "";
        string t = (tag switch
        {
            "Group" => row.GroupText,
            "Index" => row.IndexText,
            "OffsetX" => row.OffsetXText,
            "OffsetY" => row.OffsetYText,
            "Duration" => row.DurationText,
            "ScaleX" => row.ScaleXText,
            "ScaleY" => row.ScaleYText,
            "Angle" => row.AngleText,
            _ => "",
        }).Trim();
        switch (tag)
        {
            case "Group": CommitFrameInt(row, t, (v, s) => s with { Group = v }); break;
            case "Index": CommitFrameInt(row, t, (v, s) => s with { Index = v }); break;
            case "OffsetX": CommitFrameInt(row, t, (v, s) => s with { OffsetX = v }); break;
            case "OffsetY": CommitFrameInt(row, t, (v, s) => s with { OffsetY = v }); break;
            case "Duration": CommitFrameInt(row, t, (v, s) => s with { Duration = v }); break;
            case "ScaleX": CommitFrameFloat(row, t, 1f, (v, s) => s with { ScaleX = v }); break;
            case "ScaleY": CommitFrameFloat(row, t, 1f, (v, s) => s with { ScaleY = v }); break;
            case "Angle": CommitFrameFloat(row, t, 0f, (v, s) => s with { Angle = v }); break;
        }
    }

    /// <summary>Group/Number/Off X/Off Y/Time: always a real number, never optional — a blank edit leaves the value alone rather than defaulting to 0.</summary>
    private void CommitFrameInt(FrameTableRow row, string text, Func<int, FrameState, FrameState> withValue)
    {
        if (text.Length == 0) { RebuildFrameTable(); return; }
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
        {
            StatusText.Text = "Couldn't parse that as a whole number.";
            RebuildFrameTable();
            return;
        }
        CommitTableEdit(row.FrameRow, s => withValue(val, s));
    }

    /// <summary>Scale X/Y and Angle: optional, blank at their default (1 for scale, 0 for angle) — a blank edit resets to that default.</summary>
    private void CommitFrameFloat(FrameTableRow row, string text, float blankValue, Func<float, FrameState, FrameState> withValue)
    {
        float val;
        if (text.Length == 0) val = blankValue;
        else if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
        {
            StatusText.Text = "Couldn't parse that as a number.";
            RebuildFrameTable();
            return;
        }
        CommitTableEdit(row.FrameRow, s => withValue(val, s));
    }

    /// <summary>H/V: a plain checkbox, no double-click needed for a boolean. Not deferred — a completed Click, unlike a focused TextBox's LostFocus/KeyDown, isn't mid-edit when the table rebuilds under it.</summary>
    private void OnFrameFlipHClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not FrameTableRow row) return;
        CommitTableEdit(row.FrameRow, s => s with { FlipH = cb.IsChecked == true });
    }

    private void OnFrameFlipVClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not FrameTableRow row) return;
        CommitTableEdit(row.FrameRow, s => s with { FlipV = cb.IsChecked == true });
    }

    /// <summary>Selects the row being edited (so the canvas previews it, same as List/Text) and commits through the normal EditFrame undo path.</summary>
    private void CommitTableEdit(int frameRow, Func<FrameState, FrameState> change)
    {
        CrashLog.Trace($"CommitTableEdit: row {frameRow}");
        if (_action is null || frameRow < 0 || frameRow >= _action.Elements.Count) { RebuildFrameTable(); return; }
        if (FrameList.SelectedIndex != frameRow) FrameList.SelectedIndex = frameRow;
        if (CurrentElement is not { } element) { RebuildFrameTable(); return; }
        EditFrame("table edit", change(FrameState.Of(element)));
    }

    // ---- Copy/Paste Clsn (row right-click menu) ----------------------------

    /// <summary>Copied Clsn, per kind; null = that kind wasn't copied (the frame had none of its own).</summary>
    private (List<ClsnBox>? Hurt, List<ClsnBox>? Attack)? _clsnClipboard;

    /// <summary>A frame's own boxes of a kind; a default declared on it counts as own. Null when it only inherits.</summary>
    private List<ClsnBox>? OwnClsn(AnimationElement element, ClsnKind kind)
    {
        bool own = kind == ClsnKind.Attack ? element.OwnAttackBoxes : element.OwnHurtBoxes;
        if (!own && DeclaredSegmentHere(element, kind) is null) return null;
        var boxes = kind == ClsnKind.Attack ? element.Attack : element.Hurt;
        return boxes.Count > 0 ? [.. boxes] : null;
    }

    private bool CanCopyFrameClsn() =>
        CurrentElement is { } el && (OwnClsn(el, ClsnKind.Hurt) is not null || OwnClsn(el, ClsnKind.Attack) is not null);

    private void CopyFrameClsn()
    {
        if (CurrentElement is not { } el || !CanCopyFrameClsn()) return;
        _clsnClipboard = (OwnClsn(el, ClsnKind.Hurt), OwnClsn(el, ClsnKind.Attack));
        StatusText.Text = $"Copied Clsn from frame {FrameList.SelectedIndex + 1}.";
    }

    private void PasteFrameClsn()
    {
        if (_clsnClipboard is not { } clip) return;
        if (SelectedFrameTargets() is { Count: > 1 } targets)
        {
            if (EditFramesClsn("paste Clsn", targets, (a, i) => ApplyFrameClsn(a, i, clip.Hurt, clip.Attack)))
                StatusText.Text = $"Pasted Clsn into {targets.Count} frames.";
            return;
        }
        if (SetFrameClsn(clip.Hurt, clip.Attack, "paste Clsn"))
            StatusText.Text = $"Pasted Clsn into frame {FrameList.SelectedIndex + 1}.";
    }

    /// <summary>Given kinds replace the current frame's boxes; a null kind is left alone. One undo.
    /// A frame declaring a default gets them as that default: it can't hold both.</summary>
    private bool SetFrameClsn(List<ClsnBox>? hurt, List<ClsnBox>? attack, string undoName)
    {
        if (_action is not { } action || CurrentElement is not { } el) return false;
        int at = action.Elements.IndexOf(el);
        var before = CaptureActionBoxes(action);
        ApplyFrameClsn(action, at, hurt, attack);
        var after = CaptureActionBoxes(action); // RestoreActionBoxes below rebakes the frames inheriting a changed default
        if (SameActionBoxes(before, after)) return false;
        _undo.Record(undoName, () => RestoreActionBoxes(action, before), () => RestoreActionBoxes(action, after));
        RestoreActionBoxes(action, after); // refreshes canvas, table, text
        MarkAirDirty();
        return true;
    }

    /// <summary>SetFrameClsn's change for one frame, without undo or refresh.</summary>
    private static void ApplyFrameClsn(AnimationAction action, int at, List<ClsnBox>? hurt, List<ClsnBox>? attack)
    {
        var el = action.Elements[at];
        if (hurt is not null)
        {
            if (action.HurtSegmentAt(at) is { } seg && seg.At == at) { seg.Boxes = [.. hurt]; el.OwnHurtBoxes = false; }
            else { el.Hurt = [.. hurt]; el.OwnHurtBoxes = true; }
        }
        if (attack is not null)
        {
            if (action.AttackSegmentAt(at) is { } seg && seg.At == at) { seg.Boxes = [.. attack]; el.OwnAttackBoxes = false; }
            else { el.Attack = [.. attack]; el.OwnAttackBoxes = true; }
        }
    }

    // ---- Fill handle (Excel-style: drag a cell's corner up/down to copy it) ----

    private int _fillFrom = -1, _fillTo = -1;
    private int _fillSelectedFrame = -1;
    private List<int> _fillSelection = [];
    private string _fillTag = "";
    private Point _fillLastPos;
    private DispatcherTimer? _fillScrollTimer;
    private bool _fillHooked;

    private void OnFillHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle || handle.DataContext is not FrameTableRow row) return;
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        HookFillEvents();
        _fillFrom = _fillTo = row.FrameRow;
        _fillSelectedFrame = FrameList.SelectedIndex;
        _fillSelection = SelectedFrameRows();
        _fillTag = (string?)handle.Tag ?? "";
        _fillLastPos = e.GetPosition(FrameTableList);
        // Captured on the list, not the handle: a pending cell commit rebuilds the rows mid-drag.
        e.Pointer.Capture(FrameTableList);
        UpdateFillHighlight();
    }

    private void HookFillEvents()
    {
        if (_fillHooked) return;
        _fillHooked = true;
        FrameTableList.AddHandler(PointerMovedEvent, OnFillMoved, handledEventsToo: true);
        FrameTableList.AddHandler(PointerReleasedEvent, OnFillReleased, handledEventsToo: true);
        FrameTableList.AddHandler(PointerCaptureLostEvent, (_, _) => EndFill(apply: false), handledEventsToo: true);
    }

    private void OnFillMoved(object? sender, PointerEventArgs e)
    {
        if (_fillFrom < 0) return;
        _fillLastPos = e.GetPosition(FrameTableList);
        UpdateFillTarget();
    }

    /// <summary>Row under the pointer; past the list's top/bottom edge it scrolls a row per timer tick.</summary>
    private void UpdateFillTarget()
    {
        if (_fillFrom < 0 || _action is not { } action) return;
        double h = FrameTableList.Bounds.Height;
        bool above = _fillLastPos.Y < 0, below = _fillLastPos.Y > h;
        if (RowAt(new Point(4, Math.Clamp(_fillLastPos.Y, 1, Math.Max(1, h - 1)))) is { } r) _fillTo = r;
        if (above || below)
        {
            _fillTo = Math.Clamp(_fillTo + (above ? -1 : 1), 0, action.Elements.Count - 1);
            FrameTableList.ScrollIntoView(_fillTo);
            _fillScrollTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(Settings.Checked(nameof(Settings.FillScrollMilliseconds),
                _settings.FillScrollMilliseconds, Settings.DefaultFillScrollMilliseconds, v => v is >= 10 and <= 1000)),
                DispatcherPriority.Input, (_, _) => UpdateFillTarget());
            _fillScrollTimer.Start();
        }
        else _fillScrollTimer?.Stop();
        UpdateFillHighlight();
    }

    private int? RowAt(Point p) =>
        (FrameTableList.InputHitTest(p) as Visual)?.FindAncestorOfType<ListBoxItem>(true)?.DataContext is FrameTableRow row
            ? row.FrameRow : null;

    /// <summary>
    /// The one fill handle, placed over the cell under the pointer. Not one per cell: each handle was a
    /// visual per row, and rows are the expensive part of the table. Mid-drag it stays where the drag started.
    /// </summary>
    private void OnFrameTableHover(object? sender, PointerEventArgs e)
    {
        if (_fillFrom >= 0) return;
        HookFillHandleHide();
        var hit = FrameTableList.InputHitTest(e.GetPosition(FrameTableList)) as Visual;
        var cell = hit?.FindAncestorOfType<Border>(true);
        if (cell is null || !cell.Classes.Contains("cell") || cell.Tag is not string tag || cell.DataContext is not FrameTableRow row) return;
        if (cell.TranslatePoint(new Point(0, 0), FrameTableList) is not { } origin) return;
        FrameFillHandle.Margin = new Thickness(origin.X + cell.Bounds.Width - FrameFillHandle.Width - 1,
            origin.Y + cell.Bounds.Height - FrameFillHandle.Height - 1, 0, 0);
        FrameFillHandle.Tag = tag;
        FrameFillHandle.DataContext = row;
        FrameFillHandle.IsVisible = true;
    }

    private bool _fillHandleHooked;

    /// <summary>
    /// On scroll, hides the fill handle and commits an open edit, since both cells would have moved.
    /// Size-only changes (offset unchanged) don't commit.
    /// Scrolling done by our own navigation is skipped (see _navigating).
    /// </summary>
    private void HookFillHandleHide()
    {
        if (_fillHandleHooked) return;
        _fillHandleHooked = true;
        FrameTableList.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            HideFillHandle();
            // Only a real scroll moves the cells; an editor over a right-clipped cell widens the extent and viewport, offset unchanged.
            if (e.OffsetDelta == default) return;
            if (!_navigating) Dispatcher.UIThread.Post(() => CommitFrameEditor()); // posted: the commit rebuilds the table
        }, handledEventsToo: true);
    }

    /// <summary>Leaving the table hides the handle. Moving onto the handle itself doesn't, since it sits inside the table's bounds.</summary>
    private void OnFrameTableExited(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(FrameTableList);
        bool inside = p.X >= 0 && p.Y >= 0 && p.X <= FrameTableList.Bounds.Width && p.Y <= FrameTableList.Bounds.Height;
        if (!inside) HideFillHandle();
    }

    private void HideFillHandle()
    {
        if (_fillFrom < 0) FrameFillHandle.IsVisible = false; // mid-drag, keep it under the drag
    }

    /// <summary>Tints the dragged column's cells in the rows the fill covers; reapplied on every move since scrolling recycles containers.</summary>
    private void UpdateFillHighlight()
    {
        int lo = Math.Min(_fillFrom, _fillTo), hi = Math.Max(_fillFrom, _fillTo);
        foreach (var c in FrameTableList.GetRealizedContainers())
        {
            bool rowIn = _fillFrom >= 0 && c.DataContext is FrameTableRow r && r.FrameRow >= lo && r.FrameRow <= hi;
            // Each text or flag cell is a Border.cell tagged with its column.
            foreach (var cell in c.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("cell")))
                cell.Classes.Set("filltarget", rowIn && (string?)cell.Tag == _fillTag);
        }
    }

    private void OnFillReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_fillFrom < 0) return;
        e.Handled = true;
        EndFill(apply: true);
    }

    private void EndFill(bool apply)
    {
        if (_fillFrom < 0) return;
        int from = _fillFrom, to = _fillTo;
        int selectedFrame = _fillSelectedFrame;
        _fillFrom = _fillTo = _fillSelectedFrame = -1;
        _fillScrollTimer?.Stop();
        UpdateFillHighlight();
        if (selectedFrame >= 0 && selectedFrame < FrameList.ItemCount)
        {
            _suppressEvents = true;
            FrameList.SelectedIndex = selectedFrame;
            _suppressEvents = false;
            RestoreFrameTableSelection(_fillSelection);
        }
        if (apply && from != to) FillFrames(from, to, _fillTag);
    }

    /// <summary>Copies one field from frame 'from' into each frame up to 'to'; Number counts up (or down, dragging up) from a non-negative value, stopping at 0. A negative Number is copied as-is. One undo.</summary>
    private void FillFrames(int from, int to, string tag)
    {
        if (_action is not { } action || from < 0 || from >= action.Elements.Count) return;
        to = Math.Clamp(to, 0, action.Elements.Count - 1);
        var src = FrameState.Of(action.Elements[from]);
        int step = Math.Sign(to - from);
        var rows = new List<int>();
        var before = new List<FrameState>();
        var after = new List<FrameState>();
        for (int r = from + step; r != to + step; r += step)
        {
            var s = FrameState.Of(action.Elements[r]);
            var n = tag switch
            {
                "Group" => s with { Group = src.Group },
                "Index" => s with { Index = src.Index < 0 ? src.Index : Math.Max(0, src.Index + (r - from)) },
                "OffsetX" => s with { OffsetX = src.OffsetX },
                "OffsetY" => s with { OffsetY = src.OffsetY },
                "Duration" => s with { Duration = src.Duration },
                "FlipH" => s with { FlipH = src.FlipH },
                "FlipV" => s with { FlipV = src.FlipV },
                "Blend" => s with { Blend = src.Blend },
                "ScaleX" => s with { ScaleX = src.ScaleX },
                "ScaleY" => s with { ScaleY = src.ScaleY },
                "Angle" => s with { Angle = src.Angle },
                _ => s,
            };
            if (n == s) continue;
            rows.Add(r); before.Add(s); after.Add(n);
        }
        CrashLog.Trace($"FillFrames: {tag} from {from} to {to}, {rows.Count} changed");
        if (rows.Count == 0) return;

        void Apply(List<FrameState> states)
        {
            for (int i = 0; i < rows.Count; i++) states[i].ApplyTo(action.Elements[rows[i]]);
            if (_action != action) return;
            RebuildFrameList();
            ShowFrame();
            SyncTextFromAction();
            RebuildFrameTable();
        }
        _undo.Do("fill cells", () => Apply(after), () => Apply(before));
        MarkAirDirty();
        StatusText.Text = $"Filled {rows.Count} frame{(rows.Count == 1 ? "" : "s")}.";
    }
}
