namespace Editor;

/// <summary>A saved set of project tabs (.imesession, JSON): the files each tab had open. No edits, selection or zoom.</summary>
internal sealed class SessionFile
{
    public int Version { get; set; } = 1;
    /// <summary>Index into Tabs of the tab that was showing.</summary>
    public int ActiveTab { get; set; }
    public List<SessionTab> Tabs { get; set; } = [];
}

/// <summary>A tab opened from a .def stores only Def; otherwise its loose files.</summary>
internal sealed class SessionTab
{
    /// <summary>Name given with Rename, if any.</summary>
    public string? Name { get; set; }
    public string? Def { get; set; }
    public string? Sff { get; set; }
    /// <summary>An .air, or a .def holding embedded actions.</summary>
    public string? Air { get; set; }
    public string? Snd { get; set; }
    /// <summary>Standalone palette files (no SFF open in the tab).</summary>
    public List<string> Palettes { get; set; } = [];
}
