using Avalonia.Input;

namespace Editor;

/// <summary>Where a shortcut works. A more specific scope wins over Everywhere on the same key.</summary>
public enum ShortcutScope { Everywhere, AnimationsTab }

/// <summary>A key plus modifiers. Text form: "Ctrl+Shift+O", "Ctrl+0", "+", "Ctrl+Tab".</summary>
public readonly record struct Chord(Key Key, KeyModifiers Modifiers)
{
    /// <summary>Numpad +/- count as the main ones, so one binding covers both.</summary>
    public static Key Normalize(Key key) => key switch { Key.Add => Key.OemPlus, Key.Subtract => Key.OemMinus, _ => key };

    /// <summary>Keys that are only modifiers, or Escape (cancels capture), can't be a shortcut.</summary>
    public static bool Bindable(Key key) => key is not (Key.None or Key.Escape or Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Meta");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.OemPlus => "+",
        Key.OemMinus => "-",
        Key.PageUp => "Page Up",
        Key.PageDown => "Page Down",
        _ => key.ToString(),
    };

    public static bool TryParse(string text, out Chord chord)
    {
        chord = default;
        text = text.Trim();
        if (text.Length == 0) return false;
        // The key is whatever follows the last separator; a trailing "+" is the plus key itself.
        string keyText;
        string modsText;
        if (text.EndsWith('+')) { keyText = "+"; modsText = text[..^1].TrimEnd('+'); }
        else
        {
            int cut = text.LastIndexOf('+');
            keyText = cut < 0 ? text : text[(cut + 1)..];
            modsText = cut < 0 ? "" : text[..cut];
        }
        var mods = KeyModifiers.None;
        foreach (var part in modsText.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= KeyModifiers.Control; break;
                case "shift": mods |= KeyModifiers.Shift; break;
                case "alt": mods |= KeyModifiers.Alt; break;
                case "meta" or "win" or "cmd": mods |= KeyModifiers.Meta; break;
                default: return false;
            }
        }
        keyText = keyText.Trim();
        Key key;
        if (keyText.Length == 1 && char.IsDigit(keyText[0])) key = Key.D0 + (keyText[0] - '0');
        else if (keyText == "+") key = Key.OemPlus;
        else if (keyText == "-") key = Key.OemMinus;
        else if (!Enum.TryParse(keyText.Replace(" ", ""), ignoreCase: true, out key)) return false;
        key = Normalize(key);
        if (!Bindable(key)) return false;
        chord = new Chord(key, mods);
        return true;
    }

    /// <summary>Whether a key press is this chord. "+" and "-" also match with Shift added, since many layouts
    /// shift for them (so Ctrl++ also takes Ctrl+= and Ctrl+Shift+=).</summary>
    public bool Matches(Key key, KeyModifiers modifiers)
    {
        key = Normalize(key);
        if (key != Key) return false;
        if (modifiers == Modifiers) return true;
        return Key is Key.OemPlus or Key.OemMinus && !Modifiers.HasFlag(KeyModifiers.Shift)
            && modifiers == (Modifiers | KeyModifiers.Shift);
    }
}

/// <param name="Id">Key in settings.json Shortcuts.</param>
/// <param name="InTextBoxes">Also fires while a text or number box has focus (Save, Find, tab switching).</param>
public sealed record ShortcutCommand(string Id, string Section, string Label, ShortcutScope Scope, string? Default, bool InTextBoxes = false)
{
    public Chord? DefaultChord => Default is { } text && Chord.TryParse(text, out var c) ? c : null;
}

/// <summary>
/// Every command that can have a keyboard shortcut. OnKeyDown, the project-tab key handler,
/// the menus' gesture text, Help > Keyboard Shortcuts and Preferences > Shortcuts all read
/// this table, so a binding lives in one place. Built-in navigation (list arrows, Delete,
/// Escape, Enter/Tab in tables, Shift-drag, Ctrl/Shift-click) isn't a command and stays fixed.
/// </summary>
public static class Shortcuts
{
    public const string TabsSection = "Tabs";

    public static readonly ShortcutCommand[] All =
    [
        new("Open", "File", "Open (current tab's file)", ShortcutScope.Everywhere, "Ctrl+O"),
        new("OpenDef", "File", "Open .def", ShortcutScope.Everywhere, "Ctrl+Shift+O"),
        new("Save", "File", "Save (current tab's file)", ShortcutScope.Everywhere, "Ctrl+S", InTextBoxes: true),
        new("SaveAs", "File", "Save As (current tab's file)", ShortcutScope.Everywhere, "Ctrl+Shift+S", InTextBoxes: true),
        new("NewTab", TabsSection, "New tab", ShortcutScope.Everywhere, "Ctrl+T", InTextBoxes: true),
        new("CloseTab", TabsSection, "Close tab", ShortcutScope.Everywhere, "Ctrl+W", InTextBoxes: true),
        new("NextTab", TabsSection, "Next tab", ShortcutScope.Everywhere, "Ctrl+Tab", InTextBoxes: true),
        new("PreviousTab", TabsSection, "Previous tab", ShortcutScope.Everywhere, "Ctrl+Shift+Tab", InTextBoxes: true),
        new("Undo", "Edit", "Undo", ShortcutScope.Everywhere, "Ctrl+Z"),
        new("Redo", "Edit", "Redo", ShortcutScope.Everywhere, "Ctrl+Y"),
        new("SelectAll", "Edit", "Select all sprites, sounds, frames or palette colors", ShortcutScope.Everywhere, "Ctrl+A"),
        new("CopyFrames", "Edit", "Copy the selected frames", ShortcutScope.AnimationsTab, "Ctrl+C"),
        new("PasteFrames", "Edit", "Paste frames after the current frame", ShortcutScope.AnimationsTab, "Ctrl+V"),
        new("Find", "Edit", "Filter (current tab's list)", ShortcutScope.Everywhere, "Ctrl+F", InTextBoxes: true),
        new("ActualSize", "View", "Actual size", ShortcutScope.Everywhere, "Ctrl+0"),
        new("ZoomToFit", "View", "Zoom to fit", ShortcutScope.Everywhere, "Ctrl+9"),
        new("Mask", "View", "Mask", ShortcutScope.Everywhere, "Ctrl+M"),
        new("Bounds", "View", "Sprite bounds", ShortcutScope.Everywhere, "Ctrl+B"),
        new("Guides", "View", "Axis guides", ShortcutScope.Everywhere, "Ctrl+G"),
        new("ZoomIn", "View", "Zoom in", ShortcutScope.Everywhere, "Ctrl++"), // the usual Ctrl+Plus/Minus/0 family
        new("ZoomOut", "View", "Zoom out", ShortcutScope.Everywhere, "Ctrl+-"),
    ];

    public static ShortcutCommand? Find(string id) => All.FirstOrDefault(c => c.Id == id);

    public static string ScopeText(ShortcutScope scope) => scope switch
    {
        ShortcutScope.AnimationsTab => "Animations tab",
        _ => "Everywhere",
    };

    /// <summary>Two scopes can be active at once (Everywhere overlaps everything), so a key can't serve both.</summary>
    public static bool Overlap(ShortcutScope a, ShortcutScope b) => a == b || a == ShortcutScope.Everywhere || b == ShortcutScope.Everywhere;

    /// <summary>
    /// The bindings in force: settings.json Shortcuts holds only changed ones ("" = removed).
    /// Unreadable entries and unknown ids are logged and ignored.
    /// </summary>
    public static Dictionary<string, Chord?> Resolve(IReadOnlyDictionary<string, string>? changed)
    {
        var map = All.ToDictionary(c => c.Id, c => c.DefaultChord);
        if (changed is null) return map;
        foreach (var (id, text) in changed)
        {
            if (!map.ContainsKey(id)) { CrashLog.Error("Settings", $"Shortcuts: unknown command {id}; ignored"); continue; }
            if (string.IsNullOrWhiteSpace(text)) { map[id] = null; continue; }
            if (Chord.TryParse(text, out var chord)) map[id] = chord;
            else CrashLog.Error("Settings", $"Shortcuts: {id} = \"{text}\" isn't a valid shortcut; using the default");
        }
        return map;
    }

    /// <summary>What settings.json stores for these bindings: only the ones that differ from the default.</summary>
    public static Dictionary<string, string> Changed(IReadOnlyDictionary<string, Chord?> bindings)
    {
        var changed = new Dictionary<string, string>();
        foreach (var command in All)
            if (bindings.TryGetValue(command.Id, out var chord) && chord != command.DefaultChord)
                changed[command.Id] = chord?.ToString() ?? "";
        return changed;
    }
}
