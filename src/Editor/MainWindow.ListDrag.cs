using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Editor;

// Drag to reorder in the sprite, palette and sound lists: pressing a selected row drags the
// whole selection, and a drop moves it as one block in list order. While dragging, the list
// scrolls when the pointer is in its top or bottom edge band.
public partial class MainWindow
{
    /// <summary>The order after moving `moving` as one block (in list order) to drop boundary `to`; null when nothing moves.</summary>
    private static List<T>? MovedAsBlock<T>(IReadOnlyList<T> list, IEnumerable<T> moving, int to) where T : class
    {
        var set = new HashSet<T>(moving, ReferenceEqualityComparer.Instance);
        to = Math.Clamp(to, 0, list.Count);
        var rest = new List<T>(list.Count);
        var block = new List<T>();
        int insertAt = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (set.Contains(list[i])) block.Add(list[i]);
            else
            {
                if (i < to) insertAt++;
                rest.Add(list[i]);
            }
        }
        if (block.Count == 0) return null;
        rest.InsertRange(insertAt, block);
        return rest.SequenceEqual(list, ReferenceEqualityComparer.Instance) ? null : rest;
    }

    /// <summary>Items a drag from `pressed` carries: the selection when it holds the pressed item, else that item alone.</summary>
    private static T[] DraggedItems<T>(IReadOnlyCollection<T> selected, T pressed) where T : class =>
        selected.Contains(pressed, ReferenceEqualityComparer.Instance) ? [.. selected] : [pressed];

    /// <summary>Replaces a list's contents with a snapshot of the same items, refusing if the list changed under it.</summary>
    private static void ApplyListOrder<T>(List<T> list, List<T> order, string what) where T : class
    {
        var now = new HashSet<T>(list, ReferenceEqualityComparer.Instance);
        if (list.Count != order.Count || !order.All(now.Contains))
            throw new InvalidOperationException($"The {what} being reordered are no longer in the list.");
        list.Clear();
        list.AddRange(order);
    }

    private DispatcherTimer? _dragScrollTimer;
    private ScrollViewer? _dragScroller;
    private ListBox? _dragScrollList;
    private Point _dragScrollPoint;
    private long _dragScrollTick;
    private double _dragScrollEdge = Settings.DefaultDragScrollEdgePixels, _dragScrollRows = Settings.DefaultDragScrollRowsPerSecond;

    /// <summary>Starts a row drag from `list` and stops its edge scrolling when the drag ends, dropped or canceled.</summary>
    private async Task DragRows(ListBox list, PointerEventArgs e, DataObject data)
    {
        try { await DragDrop.DoDragDrop(e, data, DragDropEffects.Move); }
        finally { StopDragScroll(); }
    }

    /// <summary>Shared DragOver for the row lists: allows the move for its format and tracks the pointer for edge scrolling.</summary>
    private void ListDragOver(ListBox list, DragEventArgs e, string format)
    {
        bool ours = e.Data.Contains(format);
        e.DragEffects = ours ? DragDropEffects.Move : DragDropEffects.None;
        if (!ours || list.FindDescendantOfType<ScrollViewer>() is not { } scroller) { StopDragScroll(); return; }
        _dragScrollList = list;
        _dragScroller = scroller;
        _dragScrollPoint = e.GetPosition(scroller);
        if (_dragScrollTimer is null)
        {
            _dragScrollEdge = Settings.Checked(nameof(Settings.GeneralSection.DragScrollEdgePixels), _settings.General.DragScrollEdgePixels,
                Settings.DefaultDragScrollEdgePixels, v => double.IsFinite(v) && v is > 0 and <= 200);
            _dragScrollRows = Settings.Checked(nameof(Settings.GeneralSection.DragScrollRowsPerSecond), _settings.General.DragScrollRowsPerSecond,
                Settings.DefaultDragScrollRowsPerSecond, v => double.IsFinite(v) && v is > 0 and <= 200);
            _dragScrollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, OnDragScrollTick);
        }
        if (!_dragScrollTimer.IsEnabled)
        {
            _dragScrollTick = Stopwatch.GetTimestamp();
            _dragScrollTimer.Start();
        }
    }

    /// <summary>The pointer left the row under it; the next DragOver restarts scrolling if it is still over the list.</summary>
    private void OnListDragLeave(object? sender, RoutedEventArgs e) => StopDragScroll();

    private void StopDragScroll()
    {
        _dragScrollTimer?.Stop();
        _dragScroller = null;
        _dragScrollList = null;
    }

    private void OnDragScrollTick(object? sender, EventArgs e)
    {
        try
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = (now - _dragScrollTick) / (double)Stopwatch.Frequency;
            _dragScrollTick = now;
            if (_dragScroller is not { } scroller || _dragScrollList is not { } list) { StopDragScroll(); return; }
            double height = scroller.Bounds.Height;
            double edge = Math.Min(_dragScrollEdge, height / 2);
            if (edge <= 0) return;
            double y = _dragScrollPoint.Y;
            // Depth into the band, 0 at its inner border and 1 at the list's edge; negative scrolls up.
            double depth = y < edge ? -Math.Min((edge - y) / edge, 1) : y > height - edge ? Math.Min((y - (height - edge)) / edge, 1) : 0;
            if (depth == 0) return;
            double rowHeight = list.ItemCount > 0 && scroller.Extent.Height > 0 ? scroller.Extent.Height / list.ItemCount : 24;
            double max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
            double next = Math.Clamp(scroller.Offset.Y + depth * _dragScrollRows * rowHeight * seconds, 0, max);
            scroller.Offset = new Vector(scroller.Offset.X, next);
        }
        catch (Exception ex)
        {
            StopDragScroll();
            CrashLog.Write("List drag scroll", ex);
        }
    }
}
