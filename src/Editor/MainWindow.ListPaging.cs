using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Editor;

/// <summary>Page Up/Down move the selection in the row lists, as in Windows lists. Left to Avalonia,
/// the list's ScrollViewer took them and scrolled the view only, so the next arrow step jumped back
/// to the old row. Home and End already work through Avalonia.</summary>
public partial class MainWindow
{
    private void WireListPaging()
    {
        foreach (var list in new ListBox[] { SpriteList, PaletteList, ActionList, FrameTableList, SoundList })
            list.AddHandler(KeyDownEvent, (_, e) => OnListPagingKey(list, e), RoutingStrategies.Tunnel);
    }

    private void OnListPagingKey(ListBox list, KeyEventArgs e)
    {
        try
        {
            if (e.Key is not (Key.PageUp or Key.PageDown)) return;
            // Ctrl and Alt combinations keep their own meaning
            if ((e.KeyModifiers & ~KeyModifiers.Shift) != 0 || e.Source is TextBox) return;
            int count = list.ItemCount;
            if (count == 0 || list.FindDescendantOfType<ScrollViewer>() is not { } scroller) return;
            e.Handled = true;

            // Rows are uniform, so row positions follow from the extent
            double h = scroller.Extent.Height / count, off = scroller.Offset.Y, view = scroller.Viewport.Height;
            if (h <= 0) return;
            int first = Math.Clamp((int)Math.Ceiling(off / h - 0.001), 0, count - 1);
            int last = Math.Clamp((int)Math.Floor((off + view) / h + 0.001) - 1, first, count - 1);
            int step = Math.Max(1, last - first);
            int from = FocusedListRow(list);
            if (from < 0) from = e.Key == Key.PageDown ? first : last;

            // As in Windows lists: the first press goes to the edge of the view, the next one a page further
            int target = e.Key switch
            {
                Key.PageDown => from >= first && from < last ? last : from + step,
                _ => from > first && from <= last ? first : from - step,
            };
            target = Math.Clamp(target, 0, count - 1);

            // Scroll just enough to show the row, then focus it so the next arrow steps from here
            double top = target * h, max = Math.Max(0, scroller.Extent.Height - view);
            double next = top < off ? top : top + h > off + view ? top + h - view : off;
            if (next != off) scroller.Offset = scroller.Offset.WithY(Math.Clamp(next, 0, max));
            list.UpdateLayout();
            if (list.ContainerFromIndex(target) is not Control row)
            {
                list.ScrollIntoView(target);
                list.UpdateLayout();
                row = list.ContainerFromIndex(target) as Control ?? list;
            }
            row.Focus();

            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) ExtendListSelection(list, target, from);
            else if (list == SpriteList) SelectOnlySpriteRow(target);
            else list.SelectedIndex = target;
        }
        catch (Exception ex)
        {
            CrashLog.Write("OnListPagingKey", ex);
        }
    }

    /// <summary>The focused row in the list, else its selected row.</summary>
    private static int FocusedListRow(ListBox list) =>
        TopLevel.GetTopLevel(list)?.FocusManager?.GetFocusedElement() is Visual v && v.FindAncestorOfType<ListBoxItem>(true) is { } item
            && item.FindAncestorOfType<ListBox>() == list ? list.IndexFromContainer(item) : list.SelectedIndex;

    /// <summary>Shift+Page Up/Down: selects from the anchor to the target, like Shift+arrow.</summary>
    private void ExtendListSelection(ListBox list, int target, int from)
    {
        if (list == SpriteList)
        {
            // The sprite list keeps its own anchor for Shift+click; it counts only while still selected
            var selected = SelectedRows();
            int anchor = _spriteSelectionAnchor;
            if (anchor < 0 || anchor >= _visible.Count || !selected.Contains(anchor))
                _spriteSelectionAnchor = anchor = from >= 0 ? from : target;
            ApplySpriteRowSelection(Enumerable.Range(Math.Min(anchor, target), Math.Abs(anchor - target) + 1), suppressEvents: false);
            return;
        }

        var selection = list.Selection;
        int start = selection.AnchorIndex is var a and >= 0 && a < list.ItemCount ? a : from >= 0 ? from : target;
        int lo = Math.Min(start, target), hi = Math.Max(start, target);
        // Add the range before dropping the rest, so the selection never passes through empty
        selection.SelectRange(lo, hi);
        if (lo > 0) selection.DeselectRange(0, lo - 1);
        if (hi < list.ItemCount - 1) selection.DeselectRange(hi + 1, list.ItemCount - 1);
        selection.AnchorIndex = start;
    }
}
