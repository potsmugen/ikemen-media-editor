using Air;
using Sff;
using Snd;

namespace Editor;

public partial class MainWindow
{
    /// <summary>The active tab's state. The members below forward to it, so code written for one file set reads unchanged.</summary>
    private Document _doc = new();

    internal SffFile? _file { get => _doc.File; set => _doc.File = value; }
    private string? _path { get => _doc.Path; set => _doc.Path = value; }
    private bool _dirty { get => _doc.Dirty; set => _doc.Dirty = value; }
    private string? _characterName { get => _doc.CharacterName; set => _doc.CharacterName = value; }
    private List<Palette> _standalonePalettes => _doc.StandalonePalettes;
    private Dictionary<Palette, string> _standalonePaletteKinds => _doc.StandalonePaletteKinds;
    private List<Palette> _standalonePalettesPendingSff => _doc.StandalonePalettesPendingSff;
    private int _standalonePaletteOverride { get => _doc.StandalonePaletteOverride; set => _doc.StandalonePaletteOverride = value; }
    private Sprite? _current { get => _doc.Current; set => _doc.Current = value; }
    private int _paletteOverride { get => _doc.PaletteOverride; set => _doc.PaletteOverride = value; }
    private int _spriteSelectionAnchor { get => _doc.SpriteSelectionAnchor; set => _doc.SpriteSelectionAnchor = value; }
    private int? _onionGroup { get => _doc.OnionGroup; set => _doc.OnionGroup = value; }
    private int? _onionIndex { get => _doc.OnionIndex; set => _doc.OnionIndex = value; }

    private AirFile? _air { get => _doc.Air; set => _doc.Air = value; }
    private string? _airPath { get => _doc.AirPath; set => _doc.AirPath = value; }
    private bool _airEmbeddedInDef { get => _doc.AirEmbeddedInDef; set => _doc.AirEmbeddedInDef = value; }
    private bool _airShowsSuggestedNames { get => _doc.AirShowsSuggestedNames; set => _doc.AirShowsSuggestedNames = value; }
    private byte[]? _airDiskHash { get => _doc.AirDiskHash; set => _doc.AirDiskHash = value; }
    private List<AnimationAction> _airDefSourceActions { get => _doc.AirDefSourceActions; set => _doc.AirDefSourceActions = value; }
    private AnimationAction? _action { get => _doc.Action; set => _doc.Action = value; }
    private bool _airDirty { get => _doc.AirDirty; set => _doc.AirDirty = value; }
    private int _tick { get => _doc.Tick; set => _doc.Tick = value; }
    private string? _airError { get => _doc.AirError; set => _doc.AirError = value; }
    private int? _animOnionAction { get => _doc.AnimOnionAction; set => _doc.AnimOnionAction = value; }
    private int? _animOnionFrame { get => _doc.AnimOnionFrame; set => _doc.AnimOnionFrame = value; }

    private SndFile? _snd { get => _doc.Snd; set => _doc.Snd = value; }
    private string? _sndPath { get => _doc.SndPath; set => _doc.SndPath = value; }
    private bool _sndDirty { get => _doc.SndDirty; set => _doc.SndDirty = value; }
    private string? _sndError { get => _doc.SndError; set => _doc.SndError = value; }

    private UndoStack _undo => _doc.Undo;
    private Dictionary<string, string> _recoverySourceHashes => _doc.RecoverySourceHashes;
    private Dictionary<(string Kind, string Target), (string? Source, int Group, int Index)> _recoveryOrigins => _doc.RecoveryOrigins;
}
