namespace Def;

/// <summary>
/// A character's, stage's, screenpack's, lifebar's, or storyboard's .def,
/// read for the file names it points at. This parser does not write definitions;
/// the animation editor separately patches only embedded [Begin Action] blocks.
/// Other script editing remains out of scope. Opening a character loads its
/// sprites, animations, sounds and palettes together instead of asking for four
/// files separately — and likewise for the others, each from its own section/key
/// pair: stage ([BGdef] spr=),
/// screenpack ([Files] spr=/snd=), lifebar ([Files] sff=/snd=), storyboard
/// ([SceneDef] spr=). See each *SpritePath field for the details.
/// </summary>
public sealed class DefFile
{
    /// <summary>The folder the .def sits in; every path in it is relative to this.</summary>
    public string Directory = "";

    public string? Name;
    public string? DisplayName;
    public string? Author;
    public string? MugenVersion;

    /// <summary>Paths from [Files], resolved against the .def's folder.</summary>
    public string? SpritePath;
    public string? AnimationPath;
    public string? SoundPath;

    /// <summary>
    /// A stage's own sprite file, from [BGdef]'s spr key. Stages don't use
    /// [Files] the way characters do — this is a separate field, not folded
    /// into SpritePath, so a caller can tell a stage .def from a character
    /// one by which of the two is set.
    /// </summary>
    public string? StageSpritePath;

    /// <summary>
    /// A screenpack's own sprite/sound files, from [Files]'s spr/snd keys —
    /// still [Files], but a different pair of keys than a character's
    /// sprite=/sound=, so the two never collide in the same section.
    /// </summary>
    public string? ScreenpackSpritePath;
    public string? ScreenpackSoundPath;

    /// <summary>
    /// A lifebar's own sprite/(announcer) sound files, from [Files]'s
    /// sff/snd keys — sff rather than a character's sprite=, so it can't be
    /// confused with one. Doesn't cover the common-effects files
    /// (fightfx.sff/.air, common.snd) [Files] can also carry — out of scope
    /// for now.
    /// </summary>
    public string? LifebarSpritePath;
    public string? LifebarSoundPath;

    /// <summary>
    /// A storyboard's own sprite file, from [SceneDef]'s spr key. No sound
    /// counterpart: a storyboard's [Scene N] blocks reference bgm= mp3s
    /// directly rather than a shared .snd file.
    /// </summary>
    public string? StoryboardSpritePath;

    /// <summary>pal1 to pal12, in order, with the ones that were absent left out.</summary>
    public List<(int Number, string Path)> Palettes = [];

    /// <summary>References found only by ignoring letter case (Linux/macOS): as written, and the file used.</summary>
    public List<(string Written, string Found)> CaseMatches = [];

    /// <summary>References with several case-insensitive matches and no exact one; left unresolved (missing).</summary>
    public List<string> CaseAmbiguous = [];

    /// <summary>Everything read, section by section, for anything not modeled above.</summary>
    public Dictionary<string, Dictionary<string, string>> Sections = new(StringComparer.OrdinalIgnoreCase);

    public static DefFile Open(string path) =>
        Parse(File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path)) ?? "");

    public static DefFile Parse(string text, string directory)
    {
        var def = new DefFile { Directory = directory };
        var section = "";

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw;
            int semi = line.IndexOf(';');
            if (semi >= 0) line = line[..semi];
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line[0] == '[')
            {
                int close = line.IndexOf(']');
                section = close > 1 ? line[1..close].Trim() : line[1..].Trim();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"');
            if (key.Length == 0) continue;

            if (!def.Sections.TryGetValue(section, out var keys))
                def.Sections[section] = keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            keys[key] = value;
        }

        def.ReadKnownKeys();
        return def;
    }

    private void ReadKnownKeys()
    {
        if (Sections.TryGetValue("Info", out var info))
        {
            info.TryGetValue("name", out Name!);
            info.TryGetValue("displayname", out DisplayName!);
            info.TryGetValue("author", out Author!);
            info.TryGetValue("mugenversion", out MugenVersion!);
        }
        if (Sections.TryGetValue("BGdef", out var bgdef))
            StageSpritePath = Resolve(bgdef, "spr");

        if (Sections.TryGetValue("SceneDef", out var scenedef))
            StoryboardSpritePath = Resolve(scenedef, "spr");

        if (!Sections.TryGetValue("Files", out var files)) return;

        SpritePath = Resolve(files, "sprite");
        AnimationPath = Resolve(files, "anim");
        SoundPath = Resolve(files, "sound");
        ScreenpackSpritePath = Resolve(files, "spr");
        ScreenpackSoundPath = Resolve(files, "snd");
        LifebarSpritePath = Resolve(files, "sff");
        LifebarSoundPath = Resolve(files, "snd");

        for (int i = 1; i <= 12; i++)
            if (Resolve(files, $"pal{i}") is { } p)
                Palettes.Add((i, p));
    }

    /// <summary>
    /// Turns a value into a full path. Files are named relative to the .def, and
    /// .def files commonly use backslashes regardless of the platform. When the
    /// exact path is missing, a unique match ignoring letter case is used
    /// (CaseMatches); several matches leave it missing (CaseAmbiguous).
    /// </summary>
    private string? Resolve(Dictionary<string, string> files, string key)
    {
        if (!files.TryGetValue(key, out var value) || value.Length == 0) return null;
        var normalized = value.Replace('\\', Path.DirectorySeparatorChar)
                              .Replace('/', Path.DirectorySeparatorChar);
        var path = Path.IsPathRooted(normalized) ? normalized : Path.Combine(Directory, normalized);
        if (File.Exists(path)) return path;

        var found = MatchIgnoringCase(path, out bool ambiguous);
        if (found is not null)
        {
            CaseMatches.Add((path, found));
            return found;
        }
        if (ambiguous) CaseAmbiguous.Add(path);
        return path;
    }

    /// <summary>
    /// Walks the path one segment at a time from its root; a segment that
    /// doesn't exist as written is replaced by the one entry whose name
    /// matches ignoring case. Null when a segment has no match or several.
    /// </summary>
    public static string? MatchIgnoringCase(string path, out bool ambiguous)
    {
        ambiguous = false;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return null;
            var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (int i = 0; i < parts.Length; i++)
            {
                bool last = i == parts.Length - 1;
                var exact = Path.Combine(current, parts[i]);
                if (last ? File.Exists(exact) : System.IO.Directory.Exists(exact)) { current = exact; continue; }

                var entries = last ? System.IO.Directory.EnumerateFiles(current) : System.IO.Directory.EnumerateDirectories(current);
                var matches = entries
                    .Where(e => string.Equals(Path.GetFileName(e), parts[i], StringComparison.OrdinalIgnoreCase))
                    .Take(2).ToList();
                if (matches.Count != 1) { ambiguous = matches.Count > 1; return null; }
                current = matches[0];
            }
            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null; // unreadable folder or odd path: treat as missing
        }
    }

    /// <summary>The referenced files that are not actually on disk.</summary>
    public IEnumerable<string> MissingFiles()
    {
        foreach (var p in new[]
        {
            SpritePath, AnimationPath, SoundPath, StageSpritePath,
            ScreenpackSpritePath, ScreenpackSoundPath,
            LifebarSpritePath, LifebarSoundPath, StoryboardSpritePath,
        })
            if (p is not null && !File.Exists(p)) yield return p;
        foreach (var (_, p) in Palettes)
            if (!File.Exists(p)) yield return p;
    }
}
