using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sff;

namespace Editor;

public partial class MainWindow
{
    /// <summary>What an open palette's file held when last read or written here. EngineAct: read as the
    /// engine reads a character .act (always reversed, no count trailer), not as an import.</summary>
    private sealed record PaletteDiskState(byte[] Hash, bool EngineAct);

    private readonly ConditionalWeakTable<Palette, PaletteDiskState> _paletteDiskStates = new();
    private bool _checkingPaletteDisk;

    /// <summary>Records the file's current bytes as the version this palette matches.</summary>
    private void RememberPaletteDisk(Palette pal, bool? engineAct = null)
    {
        if (pal.SourcePath is not { } path || !File.Exists(path)) return;
        try
        {
            bool engine = engineAct ?? (_paletteDiskStates.TryGetValue(pal, out var old) && old.EngineAct);
            _paletteDiskStates.AddOrUpdate(pal, new PaletteDiskState(SHA256.HashData(File.ReadAllBytes(path)), engine));
        }
        catch (Exception ex)
        {
            CrashLog.Write("RememberPaletteDisk", ex);
        }
    }

    /// <summary>Open palettes with their own file: standalone ones, or external ones beside the SFF.</summary>
    private List<Palette> PalettesWithFiles() =>
        (_file is null ? _standalonePalettes : _file.Palettes.Where(p => p.Source == PaletteSource.External))
        .Where(p => p.SourcePath is not null && p.LinksTo is null).ToList();

    /// <summary>Window lost focus: remember files not seen yet, so changes made elsewhere are caught.</summary>
    private void BaselinePaletteFiles()
    {
        foreach (var pal in PalettesWithFiles())
            if (!_paletteDiskStates.TryGetValue(pal, out _)) RememberPaletteDisk(pal);
    }

    /// <summary>
    /// Reloads open palettes whose file changed on disk, following common editor practice:
    /// silently when there are no unsaved changes here, otherwise only after asking. Returns how many reloaded
    /// </summary>
    private async Task<int> CheckPaletteFilesOnDisk()
    {
        if (_checkingPaletteDisk || _busy) return 0;
        _checkingPaletteDisk = true;
        try
        {
            var reloaded = new List<string>();
            foreach (var pal in PalettesWithFiles())
            {
                string path = pal.SourcePath!;
                if (!File.Exists(path)) continue; // deleted or renamed: keep it in memory, Save recreates it
                byte[] data;
                try { data = File.ReadAllBytes(path); }
                catch (IOException) { continue; } // still being written: the next check sees it
                var hash = SHA256.HashData(data);
                if (!_paletteDiskStates.TryGetValue(pal, out var known))
                {
                    _paletteDiskStates.Add(pal, new PaletteDiskState(hash, false));
                    continue;
                }
                if (hash.AsSpan().SequenceEqual(known.Hash)) continue;

                // Asking or not, the change counts as seen, so one change prompts once
                _paletteDiskStates.AddOrUpdate(pal, known with { Hash = hash });
                bool pending = ReferenceEquals(_editingPalette, pal) && _adjustSession is { Count: > 0 };
                if ((pal.Dirty || pending) && !await Dialogs.Confirm(this, "Palette Changed on Disk",
                        $"{Path.GetFileName(path)} changed on disk, and it has unsaved changes here. Reload it from disk?", "Reload"))
                    continue;

                Palette loaded;
                try
                {
                    loaded = known.EngineAct ? ActFile.Read(data, reversed: true, countTrailer: false) : PaletteFileImport.Read(path, data);
                }
                catch (Exception ex)
                {
                    CrashLog.Write($"CheckPaletteFilesOnDisk {path}", ex);
                    continue;
                }
                if (pending) DiscardPendingColorAdjust();
                ReloadPaletteFromDisk(pal, loaded, data, known.EngineAct);
                reloaded.Add(Path.GetFileName(path));
            }
            if (reloaded.Count > 0)
            {
                CrashLog.Trace($"[palettes] reloaded from disk: {string.Join(", ", reloaded)}");
                StatusText.Text = reloaded.Count == 1
                    ? $"Reloaded {reloaded[0]} from disk."
                    : $"Reloaded {reloaded.Count} palettes from disk.";
            }
            return reloaded.Count;
        }
        finally
        {
            _checkingPaletteDisk = false;
        }
    }

    /// <summary>One undo step; undoing it leaves the palette unsaved, since it no longer matches its file.</summary>
    private void ReloadPaletteFromDisk(Palette pal, Palette loaded, byte[] data, bool engineAct)
    {
        bool standalone = _standalonePalettes.Contains(pal);
        bool usesSourceData = loaded.SourceData is not null;
        var before = (Colors: pal.Colors.ToArray(), pal.SourceData, pal.SourceActReversed, pal.SourceActTransparent);
        var after = (Colors: loaded.Colors.ToArray(), SourceData: usesSourceData ? (byte[])data.Clone() : null,
            loaded.SourceActReversed, loaded.SourceActTransparent);

        void Apply((Rgba[] Colors, byte[]? SourceData, bool Reversed, ushort? Transparent) s, bool dirty)
        {
            pal.Colors = s.Colors.ToArray();
            (pal.SourceData, pal.SourceActReversed, pal.SourceActTransparent) = (s.SourceData, s.Reversed, s.Transparent);
            RetargetPaletteEditor(pal, pal);
            RefreshPaletteViews(pal);
            if (!standalone) pal.Dirty = dirty; // standalone palettes take Dirty from the undo save point
            RebuildPaletteList();
        }

        string kind = standalone ? StandalonePaletteKindFor(pal) : ExternalPaletteKind;
        Edit("reload palette", () => Apply(after, false), () => Apply(before, true), kind: kind);
        if (standalone)
        {
            _undo.MarkSaved(kind);
            pal.Dirty = false;
            RebuildPaletteList();
        }
        UpdateTitle();
    }
}
