using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Sff;

namespace Editor;

/// <summary>
/// The whole palette, for Sort Colors, Manual. Only the selected slots take part: dropping one of
/// their colors on the gap beside another moves it there among them, and dropping it on the middle
/// of one swaps the two. The other slots are left blank and stay put.
/// </summary>
public class ManualColorOrderView : Control
{
    private const double Cell = 24;
    private const double Gap = 4;
    private const double DragThreshold = 4;
    private const double DraggedOpacity = 0.35;
    /// <summary>Share of a cell's width, at each side, that counts as the gap beside it</summary>
    private const double EdgeShare = 0.25;

    private readonly Rgba[] _colors;
    private readonly int[] _slots; // the selected palette indexes, ascending
    private readonly Dictionary<int, int> _slotPos; // palette index to its position in _slots
    private int[] _order;
    private int _pressed = -1; // position in _slots
    private Point _pressPoint;
    private bool _dragging;
    private int _insertAt = -1; // position in _slots to insert before (Count = after the last)
    private int _swapWith = -1;
    private Rect _insertLine;
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    public IBrush Background { get; set; } = Brushes.Black;
    public IBrush Accent { get; set; } = Brushes.White;

    /// <summary>order[k] is the position, among the selected slots, of the color now at selected slot k</summary>
    public int[] Order => (int[])_order.Clone();

    public bool IsChanged => _order.Where((p, k) => p != k).Any();

    public event Action? Changed;

    public ManualColorOrderView(IReadOnlyList<Rgba> colors, IReadOnlyList<int> selected)
    {
        _colors = [.. colors];
        _slots = [.. selected.OrderBy(i => i)];
        _slotPos = _slots.Select((index, k) => (index, k)).ToDictionary(x => x.index, x => x.k);
        _order = [.. Enumerable.Range(0, _slots.Length)];
    }

    private const int Columns = 16;
    private int Rows => (_colors.Length + Columns - 1) / Columns;

    public void Reset()
    {
        _order = [.. Enumerable.Range(0, _slots.Length)];
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Gap + Columns * (Cell + Gap), Gap + Rows * (Cell + Gap));

    private static Rect CellRect(int index) =>
        new(Gap + (index % Columns) * (Cell + Gap), Gap + (index / Columns) * (Cell + Gap), Cell, Cell);

    /// <summary>The palette index whose cell, with half the gap around it, holds p; -1 when none</summary>
    private int IndexAt(Point p)
    {
        int col = (int)Math.Floor((p.X - Gap / 2) / (Cell + Gap));
        int row = (int)Math.Floor((p.Y - Gap / 2) / (Cell + Gap));
        if (col < 0 || row < 0 || col >= Columns || row >= Rows) return -1;
        int index = row * Columns + col;
        return index < _colors.Length ? index : -1;
    }

    /// <summary>Position among the selected slots of the palette index under p; -1 when locked or none</summary>
    private int PosAt(Point p) => _slotPos.TryGetValue(IndexAt(p), out int k) ? k : -1;

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Background, new Rect(Bounds.Size));
        for (int i = 0; i < _colors.Length; i++)
        {
            // Locked slots are left blank, like the slots past a short palette's end in the main grid
            if (!_slotPos.TryGetValue(i, out int k)) continue;
            var c = _colors[_slots[_order[k]]];
            var r = CellRect(i);
            using (ctx.PushOpacity(_dragging && k == _pressed ? DraggedOpacity : 1))
            {
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), r);
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(c.A, c.A, c.A)), new Rect(r.X, r.Bottom - 3, r.Width, 3));
            }
        }
        if (!_dragging) return;
        ctx.DrawRectangle(new Pen(Accent, 1), CellRect(_slots[_pressed]));
        if (_swapWith >= 0) ctx.DrawRectangle(new Pen(Accent, 2), CellRect(_slots[_swapWith]).Inflate(1));
        else if (_insertAt >= 0) ctx.FillRectangle(Accent, _insertLine);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        try
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _pressPoint = e.GetPosition(this);
            _pressed = PosAt(_pressPoint);
            if (_pressed < 0) return;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("ManualColorOrderView.OnPointerPressed", ex);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        try
        {
            var p = e.GetPosition(this);
            if (_pressed < 0)
            {
                // The hand shows only over colors that can move
                Cursor = PosAt(p) >= 0 ? HandCursor : Cursor.Default;
                return;
            }
            if (!_dragging)
            {
                var d = p - _pressPoint;
                if (Math.Abs(d.X) < DragThreshold && Math.Abs(d.Y) < DragThreshold) return;
                _dragging = true;
            }
            UpdateTarget(p);
            InvalidateVisual();
        }
        catch (Exception ex)
        {
            CrashLog.Write("ManualColorOrderView.OnPointerMoved", ex);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        try
        {
            if (_pressed < 0) return;
            if (_dragging)
            {
                UpdateTarget(e.GetPosition(this));
                Drop(_pressed);
            }
            EndDrag();
            e.Pointer.Capture(null);
        }
        catch (Exception ex)
        {
            CrashLog.Write("ManualColorOrderView.OnPointerReleased", ex);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        _pressed = -1;
        _dragging = false;
        _insertAt = _swapWith = -1;
        InvalidateVisual();
    }

    /// <summary>Picks the drop target under p: the edges of a selected cell are the gaps beside it, the middle swaps</summary>
    private void UpdateTarget(Point p)
    {
        _insertAt = _swapWith = -1;
        int k = PosAt(p);
        if (k < 0) return; // locked colors take no drops
        var r = CellRect(_slots[k]);
        double t = (p.X - r.Left) / r.Width;
        if (t < EdgeShare) SetInsert(k, r.Left - Gap / 2, r);
        else if (t > 1 - EdgeShare) SetInsert(k + 1, r.Right + Gap / 2, r);
        else if (k != _pressed) _swapWith = k;
    }

    private void SetInsert(int k, double x, Rect cell)
    {
        if (k == _pressed || k == _pressed + 1) return; // dropping beside itself changes nothing
        _insertAt = k;
        _insertLine = new Rect(x - 1.5, cell.Top - 2, 3, cell.Height + 4);
    }

    private void Drop(int from)
    {
        if (_swapWith >= 0)
        {
            (_order[from], _order[_swapWith]) = (_order[_swapWith], _order[from]);
        }
        else if (_insertAt >= 0)
        {
            var list = _order.ToList();
            int moved = list[from];
            list.RemoveAt(from);
            list.Insert(_insertAt > from ? _insertAt - 1 : _insertAt, moved);
            _order = [.. list];
        }
        else return;
        Changed?.Invoke();
    }
}
