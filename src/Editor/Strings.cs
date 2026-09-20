namespace Editor;

/// <summary>
/// User-facing strings in one place, so a translator can produce a
/// replacement file without touching code or XAML. Defaults are English; an
/// override file, if present, replaces individual entries by name at
/// startup — the same "external text file beats a recompile" pattern
/// already used for action names (see Settings/action-names.txt).
///
/// Only the View menu is migrated to this so far — it's the demonstration
/// that the mechanism works end to end, not the finished job. The other
/// ~500 strings across the rest of the menus, buttons, tooltips and dialogs
/// still live as plain XAML/C# literals and would follow the same pattern:
/// add a field here, reference it from XAML as
/// {x:Static local:Strings.SomeName}, add a line to LoadOverrides' keys.
///
/// Not live-swappable while the app is running: XAML's x:Static reads a
/// field's value once, when the window's markup loads, so switching
/// language needs a restart. That's a deliberate, much simpler scope than a
/// fully reactive resource system, and one restart to change language is a
/// normal trade-off for a tool this size.
/// </summary>
public static class Strings
{
    // ---- View menu ------------------------------------------------------
    public static string ViewMenu = "_View";
    public static string ViewZoomIn = "Zoom _In";
    public static string ViewZoomOut = "Zoom _Out";
    public static string ViewActualSize = "Actual _Size";
    public static string ViewZoomToFit = "Zoom to _Fit";
    public static string ViewShowMask = "Show Transparency _Mask";
    public static string ViewSpriteBounds = "Sprite _Bounds";
    public static string ViewAxisGuides = "Full-canvas Axis _Guides";
    public static string ViewResetPanelSizes = "_Reset Panel Sizes";
    public static string ViewShowStatusBar = "Show _Status Bar";

    /// <summary>
    /// Loads "Name=Value" lines from the given file, overwriting the matching
    /// field above by reflection. A missing file, a bad line, or a name that
    /// doesn't match any field above is silently skipped — a broken or
    /// partial translation file should never stop the app from starting.
    /// Must run before InitializeComponent() (see MainWindow's constructor):
    /// XAML's x:Static reads these fields once, when the markup loads.
    /// </summary>
    public static void LoadOverrides(string path)
    {
        if (!File.Exists(path)) return;
        var type = typeof(Strings);
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var name = line[..eq].Trim();
                var value = line[(eq + 1)..]; // not trimmed: leading/trailing spaces can be meaningful in a label
                var field = type.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                field?.SetValue(null, value);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("LoadOverrides", ex);
        }
    }
}
