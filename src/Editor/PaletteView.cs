using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Sff;

namespace Editor;

/// <summary>
/// A 16-wide grid of palette swatches. Selection works like a file browser:
/// click selects one, Ctrl-click adds or removes one, Shift-click selects a
/// contiguous range from the last click. Dragging a selection out copies those
/// colors (in index order); dropping colors writes them in starting at the
/// slot under the pointer. The data travels as a plain in-process object, since
/// drag and drop here never needs to leave the app.
///
/// Swatches autofit to the panel size.
/// </summary>
public class PaletteView : Control
{
    private const string DragFormat = "ime/palette-colors";
    /// <summary>Swatch zoom limits in pixels per cell (settings.json PaletteCellMin/Max; set at startup).</summary>
    public static double MinCell { get; set; } = Settings.DefaultPaletteCellMin;
    public static double MaxCell { get; set; } = Settings.DefaultPaletteCellMax;
    private const int DefaultSlots = 256; // shown blank when no palette is loaded, so the grid isn't just empty
    private const double GridMargin = 1;

    private Palette? _palette;
    private readonly SortedSet<int> _selected = [];
    private int _anchor = -1;
    private int _pressedIndex = -1;
    private Point _pressPoint;
    private bool _dragArmed, _dragStarted, _collapseSelectionOnRelease;
    private bool _boxArmed, _boxActive;
    private Point _boxStart, _boxEnd;
    private SortedSet<int>? _boxBase; // selection a Ctrl box adds to; null when no box is armed

    /// <summary>The unzoomed swatch size. Zero or less falls back to a sensible default.</summary>
    public double CellSize { get; set; }

    /// <summary>Backdrop color; kept in step with the app theme (see MainWindow.ApplyTheme).</summary>
    public IBrush Background { get; set; } = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2e));

    /// <summary>Raised on every click, with the swatch index, regardless of modifier keys.</summary>
    public event Action<int>? EntryClicked;

    /// <summary>Raised when colors are dropped onto the grid, at the slot under the pointer.</summary>
    public event Action<int, Rgba[]>? ColorsDropped;

    /// <summary>Raised when a box selection ends, so the owner can rebase anything keyed to the selection.</summary>
    public event Action? BoxSelected;

    public PaletteView()
    {
        MinHeight = 120;
        Focusable = true;
        ClipToBounds = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public void Show(Palette? palette)
    {
        if (!ReferenceEquals(_palette, palette))
        {
            _palette = palette;
            _selected.Clear();
            _anchor = -1;
            InvalidateMeasure();
        }
        InvalidateVisual();
    }

    /// <summary>The selected indices, in order. Empty when nothing is selected.</summary>
    public IReadOnlyCollection<int> Selection => _selected;

    private double EffectiveCellSize
    {
        get
        {
            double cell = (CellSize > 0 ? CellSize : 12);
            if (Bounds.Width > 0 && Bounds.Height > 0)
                cell = Math.Min((Bounds.Width - GridMargin) / 16, (Bounds.Height - GridMargin) / Rows);
            return Math.Clamp(cell, MinCell, MaxCell);
        }
    }

    // Always 16 rows (256 slots total) so the grid is a consistent 16x16 square
    // regardless of how many colors the palette actually has — a 32-color
    // palette just leaves the remaining 224 slots blank rather than shrinking
    // the whole grid down to 2 rows.
    private const int Rows = DefaultSlots / 16;

    protected override Size MeasureOverride(Size availableSize)
    {
        double cell = (CellSize > 0 ? CellSize : 12);
        if (!double.IsInfinity(availableSize.Width)) cell = (availableSize.Width - GridMargin) / 16;
        if (!double.IsInfinity(availableSize.Height)) cell = Math.Min(cell, (availableSize.Height - GridMargin) / Rows);
        cell = Math.Clamp(cell, MinCell, MaxCell);
        return new Size(16 * cell + GridMargin, Rows * cell + GridMargin);
    }

    /// <summary>The swatch index at a point in this control's own coordinates, or -1 outside the grid.</summary>
    public int IndexAt(Point p)
    {
        double cell = EffectiveCellSize;
        if (p.X < GridMargin || p.Y < GridMargin) return -1;
        int col = (int)((p.X - GridMargin) / cell), row = (int)((p.Y - GridMargin) / cell);
        if (col is < 0 or > 15 || row < 0) return -1;
        int i = row * 16 + col;
        return _palette is null || i >= _palette.Colors.Length ? -1 : i;
    }

    /// <summary>Selects exactly one swatch, replacing whatever was selected before.</summary>
    public void SelectOnly(int index)
    {
        _selected.Clear();
        _selected.Add(index);
        _anchor = index;
        InvalidateVisual();
    }

    /// <summary>Selects exactly these swatches, replacing whatever was selected. Anchors on the highest, so a following Shift-click extends from there.</summary>
    public void Select(IEnumerable<int> indices)
    {
        _selected.Clear();
        foreach (int i in indices) _selected.Add(i);
        _anchor = _selected.Count > 0 ? _selected.Max : -1;
        InvalidateVisual();
    }

    /// <summary>Selects every swatch the palette actually has. Empty slots stay unselected.</summary>
    public void SelectAll()
    {
        if (_palette is { } p) Select(Enumerable.Range(0, p.Colors.Length));
        else Select([]);
    }

    /// <summary>Swaps the selected and unselected colors. Only the palette's real colors take part; empty slots are left alone.</summary>
    public void InvertSelection()
    {
        if (_palette is not { } p) { Select([]); return; }
        Select(Enumerable.Range(0, p.Colors.Length).Where(i => !_selected.Contains(i)));
    }

    /// <summary>
    /// False makes a click report its index through <see cref="EntryClicked"/> without touching
    /// the selection or arming a block drag. The eyedropper needs this: the selection is changed
    /// here, before EntryClicked is raised, so an owner that wanted to leave the selection alone
    /// would have nothing left to restore.
    /// </summary>
    public bool ClickSelects { get; set; } = true;

    /// <summary>True makes the grid view-only: no drag out, no drop in. Clicks still select.</summary>
    public bool ReadOnly
    {
        get => _readOnly;
        set { _readOnly = value; DragDrop.SetAllowDrop(this, !value); }
    }
    private bool _readOnly;

    /// <summary>True keeps the grid to one selected swatch: Shift- and Ctrl-clicks act like a plain click.</summary>
    public bool SingleSelect { get; set; }

        /// <summary>Drops the selection and the shift anchor. Used when another grid becomes the one being edited.</summary>
        public void ClearSelection()
        {
            if (_selected.Count == 0) return;
            _selected.Clear();
            _anchor = -1;
            InvalidateVisual();
        }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Background, new Rect(Bounds.Size));

        double cell = EffectiveCellSize;
        var maskPen = new Pen(new SolidColorBrush(Color.FromRgb(0x7a, 0xa8, 0xff)), 1.5);
        var selPen = new Pen(Brushes.White, 2);
        var blankBrush = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x44));
        int count = _palette?.Colors.Length ?? 0;

        for (int i = 0; i < DefaultSlots; i++)
        {
            var r = new Rect(GridMargin + (i % 16) * cell, GridMargin + (i / 16) * cell, cell - 1, cell - 1);
            if (_palette is null)
            {
                ctx.FillRectangle(blankBrush, r);
                continue;
            }
            if (i >= count) continue; // past this palette's real colors — left blank, not shrunk away

            var c = _palette.Colors[i];
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), r);
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(c.A, c.A, c.A)), new Rect(r.X, r.Bottom - 3, r.Width, 3));

            if (i == Sprite.TransparentIndex) ctx.DrawRectangle(maskPen, r);
            if (_selected.Contains(i)) ctx.DrawRectangle(selPen, r);
        }
        if (_boxActive)
            ctx.DrawRectangle(new Pen(Brushes.White, 1, new DashStyle(new double[] { 4, 3 }, 0)), new Rect(_boxStart, _boxEnd));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_palette is null) return;

        var p = e.GetCurrentPoint(this);
        if (!p.Properties.IsLeftButtonPressed) return;
        int idx = IndexAt(p.Position);
        if (idx < 0) return;

        bool ctrl = !SingleSelect && e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = !SingleSelect && e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool insideSelection = _selected.Contains(idx);
        // A press on an unselected swatch starts a box selection; a press on a selected one still drags the group.
        bool boxable = ClickSelects && !ReadOnly && !shift && !insideSelection;
        if (boxable) _boxBase = ctrl ? new SortedSet<int>(_selected) : new SortedSet<int>();
        if (boxable) e.Pointer.Capture(this); // so the release is seen even outside the grid
        _boxArmed = boxable;
        _boxActive = false;
        _boxStart = p.Position;
        _collapseSelectionOnRelease = ClickSelects && !ctrl && !shift && insideSelection && _selected.Count > 1;
        _pressedIndex = idx;
        _dragStarted = false;

        if (ClickSelects)
        {
            if (shift && _anchor >= 0)
            {
                _selected.Clear();
                for (int i = Math.Min(_anchor, idx); i <= Math.Max(_anchor, idx); i++) _selected.Add(i);
            }
            else if (ctrl)
            {
                if (!_selected.Remove(idx)) _selected.Add(idx);
                _anchor = idx;
            }
            else if (!insideSelection)
            {
                _selected.Clear();
                _selected.Add(idx);
                _anchor = idx;
            }
        }

        EntryClicked?.Invoke(idx);
        // Keep an existing group intact until the pointer either drags it or releases as a click.
        _pressPoint = p.Position;
        _dragArmed = ClickSelects && !ReadOnly && !boxable;
        InvalidateVisual();
    }

    protected override async void OnPointerMoved(PointerEventArgs e)
    {
        try
        {
            base.OnPointerMoved(e);
            if (_boxArmed || _boxActive)
            {
                _boxEnd = e.GetPosition(this);
                if (!_boxActive)
                {
                    double bx = _boxEnd.X - _boxStart.X, by = _boxEnd.Y - _boxStart.Y;
                    if (bx * bx + by * by < 36) return; // same 6px threshold as a drag
                    _boxArmed = false;
                    _boxActive = true;
                }
                ApplyBoxSelection();
                return;
            }
            if (!_dragArmed || _palette is null || _selected.Count == 0) return;

            var p = e.GetPosition(this);
            double dx = p.X - _pressPoint.X, dy = p.Y - _pressPoint.Y;
            if (dx * dx + dy * dy < 36) return; // 6px, squared: too small to be a deliberate drag
            _dragArmed = false;
            _dragStarted = true;

            var colors = _selected.Where(i => i < _palette.Colors.Length).Select(i => _palette.Colors[i]).ToArray();
            if (colors.Length == 0) return;

            var data = new DataObject();
            data.Set(DragFormat, colors);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            CrashLog.Write("PaletteView.OnPointerMoved", ex);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_boxActive)
        {
            _boxActive = false;
            InvalidateVisual(); // clears the box outline drawn during the drag
            BoxSelected?.Invoke();
        }
        _boxArmed = false;
        _boxBase = null;
        if (_collapseSelectionOnRelease && !_dragStarted && _pressedIndex >= 0)
            SelectOnly(_pressedIndex);
        _pressedIndex = -1;
        _dragArmed = false;
        _dragStarted = false;
        _collapseSelectionOnRelease = false;
    }

    /// <summary>Selects every swatch the box touches, added to the selection a Ctrl press started from.</summary>
    private void ApplyBoxSelection()
    {
        if (_palette is null) return;
        var box = new Rect(_boxStart, _boxEnd);
        double cell = EffectiveCellSize;
        var hits = new SortedSet<int>(_boxBase ?? new SortedSet<int>());
        for (int i = 0; i < _palette.Colors.Length; i++)
        {
            var r = new Rect(GridMargin + (i % 16) * cell, GridMargin + (i / 16) * cell, cell - 1, cell - 1);
            if (box.Intersects(r)) hits.Add(i);
        }
        _selected.Clear();
        _selected.UnionWith(hits);
        InvalidateVisual();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (ReadOnly || _palette is null) return;
        if (e.Data.Get(DragFormat) is not Rgba[] colors || colors.Length == 0) return;

        int at = IndexAt(e.GetPosition(this));
        if (at < 0) at = _palette.Colors.Length > 0 ? _palette.Colors.Length - 1 : 0;
        ColorsDropped?.Invoke(at, colors);
    }
}
