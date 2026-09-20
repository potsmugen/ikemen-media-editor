using Avalonia.Media;
using Avalonia.Styling;

namespace Editor;

/// <summary>One theme's colors plus whether native Fluent chrome should follow it as light or dark.</summary>
public sealed record ThemeDef(string Name, string Panel, string List, string Field, string Hover, string Text, string Muted, string Dim, string Separator, string Canvas, string Accent, string Selection, string Active, string ActiveText, ThemeVariant Variant);

/// <summary>
/// Every theme is a plain file in the themes folder, same "external file beats
/// a recompile" pattern as Strings.LoadOverrides and Settings/action-names.txt:
/// MyTheme.txt holding Panel = / List = / Field = / Text = / Muted = / Dim = /
/// Separator = / Canvas = / Accent = / Selection = / Active = / ActiveText = / Variant = (Dark|Light) lines,
/// the file name (minus .txt) becomes the theme's name in the menu. Canvas is the
/// backdrop behind sprite pixels. Separator is
/// the line between panels; it has its own key rather than being derived from Dim,
/// because a border has to carry enough contrast against Panel to be visible and
/// no single blend is right for every theme. Dark is the default and is written
/// into the folder from
/// <see cref="Fallback"/>, the only place its colors are written down; the
/// alternates ship as .txt files beside the exe and are copied in. Every file
/// starts with the comment block in <see cref="Header"/>. A missing folder, a
/// bad line, an unparseable color, or a missing key is silently skipped — a
/// broken theme file should never stop the app from starting.
/// </summary>
public static class Themes
{
    /// <summary>
    /// The theme a fresh profile starts on, and the guaranteed fallback when the
    /// themes folder is missing, empty or unusable. This is the only place the
    /// default theme's colors are written down: Dark is not shipped as a .txt
    /// like the alternates are, it is written into the user's folder from here
    /// on first run. That way there is no second copy to drift out of step.
    /// </summary>
    public static readonly ThemeDef Fallback =
        new("Dark", "#36393f", "#2f3136", "#40444b", "#484d55", "#f2f3f5", "#b9bbbe", "#949ba4", "#4f545c", "#2b2d31", "#168fe8", "#454b58", "#168fe8", "#ffffff", ThemeVariant.Dark);

    /// <summary>The alternate themes shipped as plain files next to the exe (see the copy rule in Editor.csproj).</summary>
    private static string ShippedFolder => Path.Combine(AppContext.BaseDirectory, "themes");

    /// <summary>Where theme files live: the shipped themes/ folder, which also holds your own. Created if missing.</summary>
    public static string Folder
    {
        get
        {
            Directory.CreateDirectory(ShippedFolder);
            return ShippedFolder;
        }
    }

    /// <summary>
    /// Writes the default theme into the themes folder, then copies in each shipped
    /// theme, overwriting what is there. A fresh install gets ready-to-copy files
    /// instead of a bare folder, and "Open Themes Folder" always has something to
    /// show. Overwriting is deliberate: these are the themes we ship, so a new build
    /// has to be able to reach the people running it -- a required key added to the
    /// format leaves every older copy unloadable otherwise, and a theme tweak would
    /// otherwise never reach anyone who launched the app once. Personalization goes
    /// through a copy under a new name, which is what the file header tells people to
    /// do, and those are never in ShippedFiles, so a theme of your own is not touched.
    /// Both steps log and continue on failure — <see cref="Fallback"/> means
    /// the app can always start on a usable theme even if none of this
    /// succeeds.
    /// </summary>
    private static void SeedDefaults(string folder)
    {
        var defaultPath = Path.Combine(folder, Fallback.Name + ".txt");
        try { WriteIfChanged(defaultPath, ToFileContents(Fallback)); }
        catch (Exception ex)
        {
            CrashLog.Write("SeedDefaults", ex);
        }

        foreach (var source in ShippedFiles())
        {
            var path = Path.Combine(folder, Path.GetFileName(source));
            // Folder is the shipped folder itself, so a file never needs copying onto itself.
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) continue;
            try { WriteIfChanged(path, File.ReadAllText(source)); }
            catch (Exception ex)
            {
                CrashLog.Write("SeedDefaults", ex);
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="contents"/> only when the file does not already have it.
    /// Seeding runs on every start, so this keeps a launch with nothing new to seed from
    /// touching the files at all.
    /// </summary>
    private static void WriteIfChanged(string path, string contents)
    {
        if (File.Exists(path) && File.ReadAllText(path) == contents) return;
        File.WriteAllText(path, contents);
    }

    /// <summary>The theme files shipped beside the exe. The default is not among them — it is written from <see cref="Fallback"/>, so the shipped set holds only the alternates.</summary>
    private static IEnumerable<string> ShippedFiles()
    {
        try
        {
            return Directory.Exists(ShippedFolder)
                ? Directory.GetFiles(ShippedFolder, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                : [];
        }
        catch (Exception ex)
        {
            CrashLog.Write("ShippedFiles", ex);
            return [];
        }
    }

    /// <summary>Writes a theme file. Spaced "Key = value" because these are read and edited by people, and the parser trims either side of the "=".</summary>
    private static string ToFileContents(ThemeDef t) =>
        Header +
        $"Panel   = {t.Panel}\n" +
        $"List    = {t.List}\n" +
        $"Field   = {t.Field}\n" +
        $"Hover   = {t.Hover}\n" +
        $"Text    = {t.Text}\n" +
        $"Muted   = {t.Muted}\n" +
        $"Dim     = {t.Dim}\n" +
        $"Separator = {t.Separator}\n" +
        $"Canvas  = {t.Canvas}\n" +
        $"Accent  = {t.Accent}\n" +
        $"Selection = {t.Selection}\n" +
        $"Active = {t.Active}\n" +
        $"ActiveText = {t.ActiveText}\n" +
        $"Variant = {(t.Variant == ThemeVariant.Light ? "Light" : "Dark")}\n";

    /// <summary>The comment block every theme file starts with, so a written one and a copied one are indistinguishable. Keep in step with the .txt files beside the exe.</summary>
    private const string Header =
        "# Theme colors for the editor.\n" +
        "#\n" +
        "# Copy this file, give it a new name, and edit the colors to make your own.\n" +
        "# The filename without \".txt\" is what appears in Preferences > Theme.\n" +
        "# Anything you save here is used as-is; the app never rewrites your files.\n" +
        "#\n" +
        "# Colors are #RRGGBB.\n" +
        "#\n" +
        "# Panel       window, menu bar, and panel backgrounds\n" +
        "# List        list boxes\n" +
        "# Field       input backgrounds (text boxes, number boxes)\n" +
        "# Hover       button hover backgrounds\n" +
        "# Text        primary text\n" +
        "# Muted       secondary text: labels and table column headers\n" +
        "# Dim         disabled text\n" +
        "# Separator   the line between panels: splitters and panel borders\n" +
        "# Canvas      backdrop behind sprite pixels\n" +
        "# Accent      filled slider tracks and checkboxes\n" +
        "# Selection   selected table rows\n" +
        "# Active      checked toggle button background\n" +
        "# ActiveText  checked toggle button text\n" +
        "# Variant     Dark or Light; also sets the color of native window chrome\n" +
        "\n";

    /// <summary>Seeds the default theme file if missing, then loads every valid *.txt in <paramref name="folder"/>, keyed by name (case-insensitive). Order: Dark first (guaranteed present), then every other theme found, alphabetically.</summary>
    public static List<ThemeDef> LoadAll(string folder)
    {
        SeedDefaults(folder);

        var byName = new Dictionary<string, ThemeDef>(StringComparer.OrdinalIgnoreCase) { [Fallback.Name] = Fallback };
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.GetFiles(folder, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length > 0 && TryParse(file, name, out var theme)) byName[name] = theme;
            }
        }

        var result = new List<ThemeDef> { byName[Fallback.Name] };
        result.AddRange(byName.Values.Where(t => !t.Name.Equals(Fallback.Name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    private static bool TryParse(string path, string name, out ThemeDef theme)
    {
        theme = null!;
        string? panel = null, list = null, field = null, hover = null, text = null, muted = null, dim = null,
                    separator = null, canvas = null, accent = null, selection = null, active = null, activeText = null;
        string? variant = null;

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex)
        {
            CrashLog.Write("Themes.TryParse", ex);
            return false;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            switch (key.ToLowerInvariant())
            {
                case "panel": panel = value; break;
                case "list": list = value; break;
                case "field": field = value; break;
                case "hover": hover = value; break;
                case "text": text = value; break;
                case "muted": muted = value; break;
                case "dim": dim = value; break;
                case "separator": separator = value; break;
                case "canvas": canvas = value; break;
                case "accent": accent = value; break;
                case "selection": selection = value; break;
                case "active": active = value; break;
                case "activetext": activeText = value; break;
                case "variant": variant = value; break;
            }
        }

        if (panel is null || list is null || field is null || hover is null || text is null || muted is null || dim is null
            || separator is null || canvas is null || accent is null || selection is null || active is null || activeText is null
            || variant is null) return false;
        foreach (var c in (string[])[panel, list, field, hover, text, muted, dim, separator, canvas, accent, selection, active, activeText])
            if (!Color.TryParse(c, out _)) return false; // one bad color and the whole file is skipped, not partially applied

        if (!variant.Equals("Dark", StringComparison.OrdinalIgnoreCase)
            && !variant.Equals("Light", StringComparison.OrdinalIgnoreCase)) return false;

        theme = new ThemeDef(name, panel, list, field, hover, text, muted, dim, separator, canvas, accent, selection, active, activeText,
            variant.Equals("Light", StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Light : ThemeVariant.Dark);
        return true;
    }
}
