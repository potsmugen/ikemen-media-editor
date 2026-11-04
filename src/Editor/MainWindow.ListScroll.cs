using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Editor;

/// <summary>Every automatic move to a list row puts it in the middle of the view, so the
/// Sprite, Palette, Animation, Sound and frame tables behave the same.</summary>
public partial class MainWindow
{
    /// <summary>Scrolls the list so the row sits in the middle of the visible area,
    /// clamped to the ends of the list. A row already fully in view stays where it is.</summary>
    private void CenterListRow(ListBox list, int row)
    {
        if (row < 0 || row >= list.ItemCount) return;
        if (list.FindDescendantOfType<ScrollViewer>() is { } view && list.ContainerFromIndex(row) is Control shown
            && shown.TranslatePoint(new Point(0, 0), view) is { } at && at.Y >= 0 && at.Y + shown.Bounds.Height <= view.Viewport.Height)
            return;
        // Bring the row into view first so its container is realized, then center it.
        list.ScrollIntoView(row);
        list.UpdateLayout();
        if (list.ContainerFromIndex(row) is not Control item) return;
        if (list.FindDescendantOfType<ScrollViewer>() is not { } scroller) return;
        if (item.TranslatePoint(new Point(0, 0), scroller) is not { } top) return;

        double rowHeight = item.Bounds.Height;
        double target = scroller.Offset.Y + top.Y + rowHeight / 2 - scroller.Viewport.Height / 2;
        double max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        // Keep the first visible row whole. Centering at an arbitrary pixel offset can
        // leave the top row half hidden after a table rebuild (for example, Sort or Duplicate).
        double alignedMax = rowHeight > 0 ? Math.Floor(max / rowHeight) * rowHeight : max;
        double alignedTarget = rowHeight > 0 ? Math.Round(target / rowHeight) * rowHeight : target;
        scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(alignedTarget, 0, alignedMax));
        list.UpdateLayout();
    }

    /// <summary>Centers the row now and again once layout settles, so a jump that switches
    /// tabs still lands in the middle when the list wasn't realized yet. The row is looked
    /// up both times, since the list may be rebuilt in between.</summary>
    private void ScrollListRowIntoView(ListBox list, Func<int> rowOf)
    {
        CenterListRow(list, rowOf());
        Dispatcher.UIThread.Post(() =>
        {
            try { CenterListRow(list, rowOf()); }
            catch (Exception ex) { CrashLog.Write("ScrollListRowIntoView", ex); }
        }, DispatcherPriority.Background);
    }
}
