namespace Editor;

// Drag to reorder in the sprite, palette and sound lists: pressing a selected row drags the
// whole selection, and a drop moves it as one block in list order.
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
}
