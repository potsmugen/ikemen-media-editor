using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Editor;

/// <summary>
/// Runs keyboard shortcuts from the Shortcuts table (Shortcuts.cs) with the bindings in force,
/// and keeps the menus' gesture text in step with them.
/// </summary>
public partial class MainWindow
{
    /// <summary>Command id to its key, or null when it has none.</summary>
    private Dictionary<string, Chord?> _shortcuts = Shortcuts.Resolve(null);
    private Dictionary<string, Action>? _shortcutActions;

    /// <summary>Reads settings.json Shortcuts and refreshes the menus. Called at startup and after Preferences.</summary>
    private void LoadShortcuts()
    {
        _shortcuts = Shortcuts.Resolve(_settings.Shortcuts);
        ApplyMenuGestures();
    }

    private Dictionary<string, Action> ShortcutActions => _shortcutActions ??= new()
    {
        ["Open"] = OpenCurrentTab,
        ["OpenDef"] = () => OnOpenDef(this, new RoutedEventArgs()),
        ["Save"] = SaveCurrentTab,
        ["SaveAs"] = () => _ = SaveAsCurrentTab(), // each branch logs its own failures
        ["NewTab"] = () => RunTabShortcut("NewTab", AddProjectTab),
        ["CloseTab"] = () => RunTabShortcut("CloseTab", () => CloseProjectTab(_doc)),
        ["NextTab"] = () => RunTabShortcut("NextTab", () => StepProjectTab(1)),
        ["PreviousTab"] = () => RunTabShortcut("PreviousTab", () => StepProjectTab(-1)),
        ["Undo"] = () => OnUndo(this, new RoutedEventArgs()),
        ["Redo"] = () => OnRedo(this, new RoutedEventArgs()),
        ["SelectAll"] = SelectAllCurrentList,
        ["CopyFrames"] = CopyFrames,
        ["PasteFrames"] = PasteFrames,
        ["Find"] = () => OnFocusFind(this, new RoutedEventArgs()),
        ["ActualSize"] = () => OnResetView(this, new RoutedEventArgs()),
        ["ZoomToFit"] = () => OnZoomFit(this, new RoutedEventArgs()),
        ["Mask"] = () => OnToggleMask(this, new RoutedEventArgs()),
        ["Bounds"] = () => OnToggleBounds(this, new RoutedEventArgs()),
        ["Guides"] = () => OnToggleGuides(this, new RoutedEventArgs()),
        ["ZoomIn"] = () => ActiveCanvas?.StepZoom(1),
        ["ZoomOut"] = () => ActiveCanvas?.StepZoom(-1),
    };

    /// <summary>Ctrl+O for whichever tab is active, like Save: .sff, palette files, .air or .def, .snd.</summary>
    private void OpenCurrentTab()
    {
        var args = new RoutedEventArgs();
        switch (Tabs?.SelectedIndex)
        {
            case 1: OnImportPaletteFiles(null, args); break;
            case 2: OnOpenAir(null, args); break;
            case 3: OnOpenSnd(null, args); break;
            default: OnOpen(null, args); break;
        }
    }

    private async void RunTabShortcut(string name, Func<Task> run)
    {
        try { await run(); }
        catch (Exception ex) { await Failed(name, ex); }
    }

    private async Task StepProjectTab(int step)
    {
        if (_docs.Count < 2) return;
        int at = _docs.IndexOf(_doc) + step;
        await SwitchToDocument(_docs[(at + _docs.Count) % _docs.Count]);
    }

    private bool ShortcutScopeActive(ShortcutScope scope) => scope switch
    {
        ShortcutScope.AnimationsTab => Tabs?.SelectedIndex == 2,
        _ => true,
    };

    /// <summary>The command this key press runs, if any: the most specific active scope wins, then table order.</summary>
    private ShortcutCommand? MatchShortcut(KeyEventArgs e)
    {
        ShortcutCommand? best = null;
        foreach (var command in Shortcuts.All)
        {
            if (_shortcuts.GetValueOrDefault(command.Id) is not { } chord || !chord.Matches(e.Key, e.KeyModifiers)) continue;
            if (!ShortcutScopeActive(command.Scope)) continue;
            if (best is null || (best.Scope == ShortcutScope.Everywhere && command.Scope != ShortcutScope.Everywhere)) best = command;
        }
        return best;
    }

    /// <summary>Runs the matched command. Tab commands are left to the project-tab handler, which sees keys first.</summary>
    private bool RunShortcut(ShortcutCommand command, KeyEventArgs e)
    {
        if (!ShortcutActions.TryGetValue(command.Id, out var run)) return false;
        CrashLog.Trace($"[shortcut] {command.Id}");
        run();
        e.Handled = true;
        return true;
    }

    /// <summary>Menu gesture text from the bindings in force. Avalonia's InputGesture only shows the keys.</summary>
    private void ApplyMenuGestures()
    {
        if (OpenSffItem is null) return; // before InitializeComponent
        var items = new (string Id, MenuItem Item)[]
        {
            ("Open", OpenSffItem), ("Open", ImportPaletteItem), ("Open", OpenAirItem), ("Open", OpenSndItem),
            ("Save", SaveSpritesItem), ("Save", SaveAnimationsItem), ("Save", SaveSoundsItem),
            ("OpenDef", OpenDefItem), ("SaveAs", SaveAsSpritesItem),
            ("NewTab", NewTabItem), ("CloseTab", CloseTabItem), ("Undo", UndoItem), ("Redo", RedoItem),
            ("ZoomIn", ZoomInItem), ("ZoomOut", ZoomOutItem), ("ActualSize", ActualSizeItem), ("ZoomToFit", ZoomFitItem),
            ("Mask", MaskItem), ("Bounds", BoundsItem), ("Guides", GuidesItem),
        };
        foreach (var (id, item) in items)
            item.InputGesture = _shortcuts.GetValueOrDefault(id) is { } c ? new KeyGesture(c.Key, c.Modifiers) : null;
    }

    /// <summary>The menu bar's Alt+letter keys ("_File" gives F), with the menu's name, for Preferences' warning.</summary>
    private Dictionary<Key, string> MenuAccessKeys()
    {
        var keys = new Dictionary<Key, string>();
        foreach (var item in MainMenu.Items.OfType<MenuItem>())
        {
            if (item.Header is not string header) continue;
            int at = header.IndexOf('_');
            if (at < 0 || at + 1 >= header.Length) continue;
            char letter = char.ToUpperInvariant(header[at + 1]);
            if (letter is >= 'A' and <= 'Z' && Enum.TryParse<Key>(letter.ToString(), out var key))
                keys.TryAdd(key, header.Replace("_", ""));
        }
        return keys;
    }

    /// <summary>Help > Keyboard Shortcuts rows for the commands, by section in table order.</summary>
    private List<(string Section, List<(string Chord, string Text)> Items)> ShortcutHelpSections()
    {
        var sections = new List<(string Section, List<(string Chord, string Text)> Items)>();
        foreach (var command in Shortcuts.All)
        {
            int at = sections.FindIndex(s => s.Section == command.Section);
            if (at < 0) { sections.Add((command.Section, [])); at = sections.Count - 1; }
            string chord = _shortcuts.GetValueOrDefault(command.Id)?.ToString() ?? "None";
            string text = command.Scope == ShortcutScope.Everywhere ? command.Label : $"{command.Label} ({Shortcuts.ScopeText(command.Scope)})";
            sections[at].Items.Add((chord, text));
        }
        return sections;
    }
}
