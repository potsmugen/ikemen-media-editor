using Air;
using Sff;
using Snd;

namespace Editor;

/// <summary>
/// What one editor tab owns: its SFF, AIR and SND, undo history, recovery
/// bookkeeping and selection. MainWindow reads the active one through _doc.
/// </summary>
internal sealed class Document
{
    /// <summary>Stable across reorders; tags this tab's recovery snapshots.</summary>
    public string Id = Guid.NewGuid().ToString("N");
    /// <summary>The .def this tab was opened from, if any; names the tab.</summary>
    public string? DefPath;
    /// <summary>The .def's name= field; names the tab ahead of the .def file name.</summary>
    public string? DefName;
    /// <summary>Name given with Rename; null shows the automatic .def/file name.</summary>
    public string? CustomName;

    // ---- sprites ------------------------------------------------------------
    public SffFile? File;
    public string? Path;
    public bool Dirty;
    /// <summary>Set when opened via a .def, so the title can show the character's own name rather than its sprite filename. Cleared on Close All.</summary>
    public string? CharacterName;
    public readonly List<Palette> StandalonePalettes = [];
    public readonly Dictionary<Palette, string> StandalonePaletteKinds = new(ReferenceEqualityComparer.Instance);
    public readonly List<Palette> StandalonePalettesPendingSff = [];
    public int StandalonePaletteOverride = -1;
    public Sprite? Current;
    /// <summary>-1 means the sprite's own palette.</summary>
    public int PaletteOverride = -1;
    public int SpriteSelectionAnchor = -1;
    // Null means "automatic" (previous sprite/frame); set means always use this one sprite.
    public int? OnionGroup;
    public int? OnionIndex;
    /// <summary>Sprites and Animations canvas zoom and pan when this tab was last left; null starts at 100%.</summary>
    public (int ZoomIndex, Avalonia.Point Pan)? SpriteView, AnimView;

    // ---- animations ---------------------------------------------------------
    public AirFile? Air;
    public string? AirPath;
    public bool AirEmbeddedInDef;
    /// <summary>Whether missing action names fall back to reserved-action suggestions. True for a
    /// standalone .air or a character definition, false for stages and other .defs (their embedded
    /// background actions aren't gameplay states the user would name).</summary>
    public bool AirShowsSuggestedNames;
    public byte[]? AirDiskHash;
    public List<AnimationAction> AirDefSourceActions = [];
    public AnimationAction? Action;
    public bool AirDirty;
    public int Tick;
    public string? AirError;
    public int? AnimOnionAction;
    /// <summary>1-based, as shown.</summary>
    public int? AnimOnionFrame;

    // ---- sounds -------------------------------------------------------------
    public SndFile? Snd;
    public string? SndPath;
    public bool SndDirty;
    public string? SndError;
    /// <summary>The selected sound, kept while another tab is shown.</summary>
    public SoundEntry? Sound;

    // ---- history and recovery -----------------------------------------------
    public readonly UndoStack Undo = new();
    public readonly Dictionary<string, string> RecoverySourceHashes = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<(string Kind, string Target), (string? Source, int Group, int Index)> RecoveryOrigins = new();
}
