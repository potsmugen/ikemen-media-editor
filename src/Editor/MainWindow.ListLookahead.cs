using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Editor;

/// <summary>Keyboard steps keep the next list row in view. Avalonia 11.3 rebuilds every visible
/// row when ScrollIntoView targets an unrealized row; with the next row realized, that never happens.</summary>
public partial class MainWindow
{
    private int _listNavDirection; // -1 up, 1 down, 0 no navigation key down
    private bool _listLookaheadQueued;

    private void WireListLookahead()
    {
        WireListNavKeys(this);
        foreach (var list in new ListBox[] { SpriteList, PaletteList, ActionList, FrameTableList, SoundList })
            list.SelectionChanged += (_, _) => QueueLookahead(list);
    }

    /// <summary>Tracks the navigation key direction; floating panel windows wire it too.</summary>
    private void WireListNavKeys(Interactive target)
    {
        target.AddHandler(KeyDownEvent, (_, e) => _listNavDirection = e.Key switch
        {
            Key.Up or Key.PageUp or Key.Home => -1,
            Key.Down or Key.PageDown or Key.End => 1,
            _ => 0,
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        target.AddHandler(KeyDownEvent, (_, _) => _listNavDirection = 0, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void QueueLookahead(ListBox list)
    {
        if (_listNavDirection == 0 || _listLookaheadQueued) return;
        _listLookaheadQueued = true;
        int dir = _listNavDirection;
        // Loaded runs after this step's layout, once rows and focus have settled.
        Dispatcher.UIThread.Post(() =>
        {
            _listLookaheadQueued = false;
            try { RevealNextRow(list, dir); }
            catch (Exception ex) { CrashLog.Write("RevealNextRow", ex); }
        }, DispatcherPriority.Loaded);
    }

    private void RevealNextRow(ListBox list, int dir)
    {
        // The focused row is the moving end of a Shift range; without list focus it's the selection.
        int index = TopLevel.GetTopLevel(list)?.FocusManager?.GetFocusedElement() is Visual v && v.FindAncestorOfType<ListBoxItem>(true) is { } focused
            && focused.FindAncestorOfType<ListBox>() == list ? list.IndexFromContainer(focused) : list.SelectedIndex;
        if (index + dir < 0 || index + dir >= list.ItemCount || !list.IsEffectivelyVisible) return;
        if (list.ContainerFromIndex(index) is not Control item) return; // a long jump; ScrollIntoView handled it
        if (list.FindDescendantOfType<ScrollViewer>() is not { } scroller) return;
        if (item.TranslatePoint(default, scroller) is not { } top) return;

        // Rows are uniform, so the next row sits one row height away.
        double h = item.Bounds.Height, nextTop = top.Y + dir * h;
        double shift = dir > 0 ? Math.Max(0, nextTop + h - scroller.Viewport.Height) : Math.Min(0, nextTop);
        if (shift != 0) scroller.Offset = scroller.Offset.WithY(scroller.Offset.Y + shift);
    }
}
