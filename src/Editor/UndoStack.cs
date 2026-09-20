namespace Editor;

/// <summary>
/// A plain undo stack. Each entry carries the two closures that move between
/// before and after, which keeps callers short and avoids a command class per
/// operation. Entries are tagged with the file kinds they touch, so a file
/// undone back to where it was saved counts as unchanged again.
/// </summary>
public sealed class UndoStack
{
    private sealed class Entry(string name, Action undo, Action redo, string? mergeKey, DateTime when)
    {
        public string Name = name;
        public Action Undo = undo;
        public Action Redo = redo;
        public string? MergeKey = mergeKey;
        public DateTime When = when;
        public readonly HashSet<string> Kinds = [];
        public object? View;
    }

    /// <summary>A save point no undo or redo can reach again.</summary>
    private static readonly Entry Unreachable = new("", () => { }, () => { }, null, default);

    /// <summary>How many steps are kept (settings.json UndoLimit).</summary>
    public int Limit { get; set; } = Settings.DefaultUndoLimit;

    /// <summary>How long two edits of the same thing count as one action (settings.json UndoMergeSeconds).</summary>
    public TimeSpan MergeWindow { get; set; } = TimeSpan.FromSeconds(Settings.DefaultUndoMergeSeconds);

    private readonly List<Entry> _done = [];
    private readonly List<Entry> _undone = [];
    /// <summary>Per kind: the top entry touching it when saved; null means no entry (the stack's base).</summary>
    private readonly Dictionary<string, Entry?> _saved = [];
    /// <summary>The entry the last Do/Record pushed or merged into, waiting for Tag.</summary>
    private Entry? _untagged;
    private bool _applying;

    /// <summary>Called when an entry is pushed: where the edit was made, so undo can go back there.</summary>
    public Func<object?>? CaptureView { get; set; }

    /// <summary>Raised whenever the stack changes, so menus can be re-enabled.</summary>
    public event Action? Changed;

    public bool CanUndo => _done.Count > 0;
    public bool CanRedo => _undone.Count > 0;
    public string? UndoName => CanUndo ? _done[^1].Name : null;
    public string? RedoName => CanRedo ? _undone[^1].Name : null;
    public object? UndoView => CanUndo ? _done[^1].View : null;
    public object? RedoView => CanRedo ? _undone[^1].View : null;

    /// <summary>
    /// Runs an action and records how to reverse it. When mergeKey matches the
    /// previous entry and little time has passed, the two collapse into one, so
    /// nudging a spinner repeatedly does not fill the stack.
    /// </summary>
    public void Do(string name, Action apply, Action undo, string? mergeKey = null)
    {
        apply();
        var now = DateTime.UtcNow;
        if (mergeKey is not null && _done.Count > 0 && _done[^1] is { } prev
            && prev.MergeKey == mergeKey && now - prev.When < MergeWindow)
            Merge(prev, apply, now); // keeps the older undo, which restores the state before the run began
        else
            Push(new Entry(name, undo, apply, mergeKey, now));
        _undone.Clear();
        Changed?.Invoke();
    }

    /// <summary>Records an action already performed. Adjacent edits with the same merge key share one undo item.</summary>
    public void Record(string name, Action undo, Action redo, string? mergeKey = null)
    {
        var now = DateTime.UtcNow;
        if (mergeKey is not null && _done.Count > 0 && _done[^1] is { } previous
            && previous.MergeKey == mergeKey)
            Merge(previous, redo, now);
        else
            Push(new Entry(name, undo, redo, mergeKey, now));
        _undone.Clear();
        Changed?.Invoke();
    }

    private void Push(Entry e)
    {
        e.View = CaptureView?.Invoke();
        _done.Add(e);
        _untagged = e;
        if (_done.Count <= Limit) return;
        var dropped = _done[0];
        _done.RemoveAt(0);
        // The base moves past this entry: a kind saved at the old base can't get back there; one saved at this entry now sits at the base.
        foreach (var kind in _saved.Keys.ToList())
        {
            if (_saved[kind] is null && dropped.Kinds.Contains(kind)) _saved[kind] = Unreachable;
            else if (ReferenceEquals(_saved[kind], dropped)) _saved[kind] = null;
        }
    }

    /// <summary>Replaces the top entry, so a save point on the old one no longer matches.</summary>
    private void Merge(Entry prev, Action redo, DateTime now)
    {
        var merged = new Entry(prev.Name, prev.Undo, redo, prev.MergeKey, now);
        merged.Kinds.UnionWith(prev.Kinds);
        merged.View = prev.View;
        _done[^1] = merged;
        _untagged = merged;
    }

    /// <summary>Called when a file kind is marked changed. Returns false for a change no entry covers.</summary>
    public bool Tag(string kind)
    {
        if (_applying) return true;
        if (_untagged is null) return false;
        _untagged.Kinds.Add(kind);
        return true;
    }

    /// <summary>A change outside the stack: undo can't bring this kind back to its saved state.</summary>
    public void Untracked(string kind) => _saved[kind] = Unreachable;

    /// <summary>The kind was just saved or loaded.</summary>
    public void MarkSaved(string kind) => _saved[kind] = Top(kind);

    /// <summary>False only when undo/redo has brought the kind back to where it was saved.</summary>
    public bool Differs(string kind) => !_saved.TryGetValue(kind, out var s) || !ReferenceEquals(s, Top(kind));

    private Entry? Top(string kind)
    {
        for (int i = _done.Count - 1; i >= 0; i--)
            if (_done[i].Kinds.Contains(kind)) return _done[i];
        return null;
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var e = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        _untagged = null;
        _applying = true;
        try { e.Undo(); } finally { _applying = false; }
        _undone.Add(e);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var e = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);
        _untagged = null;
        _applying = true;
        try { e.Redo(); } finally { _applying = false; }
        _done.Add(e);
        Changed?.Invoke();
    }

    /// <summary>Every save point becomes unreachable; callers mark clean kinds saved again.</summary>
    public void Clear(Action? beforeChanged = null)
    {
        _done.Clear();
        _undone.Clear();
        _untagged = null;
        foreach (var kind in _saved.Keys.ToList()) _saved[kind] = Unreachable;
        beforeChanged?.Invoke();
        Changed?.Invoke();
    }
}
