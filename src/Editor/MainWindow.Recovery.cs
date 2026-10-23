using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Air;
using Avalonia.Threading;
using Sff;
using Snd;

namespace Editor;

public partial class MainWindow
{
    private DispatcherTimer? _recoveryTimer;
    private TimeSpan _recoveryInterval;
    private DateTimeOffset? _recoveryDueAt;
    internal bool RecoveryTestMode { get; set; }
    private bool _preserveRecoveryPackage;

    private void ResolveRecoveredItem(string kind, string? path, int? group = null, int? index = null)
    {
        if (path is not null && _recoveryOrigins.Remove((kind, path), out var original))
            RecoveryStore.Resolve(kind, original.Source, group ?? (original.Group == 0 ? null : original.Group),
                index ?? (original.Index == 0 ? null : original.Index), _doc.Id);
        else RecoveryStore.Resolve(kind, path, group, index, _doc.Id);
    }

    private void StartRecoveryTimer()
    {
        int minutes = Settings.Checked(nameof(Settings.FilesSection.RecoveryIntervalMinutes), _settings.Files.RecoveryIntervalMinutes,
            Settings.DefaultRecoveryIntervalMinutes, v => v >= 1 && v <= 1440);
        _recoveryInterval = RecoveryTestMode ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(minutes);
        // Checks at most once a minute. A snapshot is taken only when there is unsaved work and it is due.
        _recoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Min(60, _recoveryInterval.TotalSeconds)) };
        _recoveryTimer.Tick += async (_, _) =>
        {
            try { await RecoveryTick(); }
            catch (Exception ex) { CrashLog.Write("RecoveryTick", ex); } // log only: a dialog every tick would be worse
        };
        _recoveryTimer.Start();
    }

    /// <summary>Unsaved work in any project tab.</summary>
    private bool HasUnsavedWork() => _docs.Any(d => d.Dirty || d.AirDirty || d.SndDirty
        || d.File?.Palettes.Any(p => p.Source == PaletteSource.External && p.Dirty) == true);

    private async Task RecoveryTick()
    {
        if (_busy) return; // a load or save is reading the file on a background thread
        if (!HasUnsavedWork()) { _recoveryDueAt = null; return; }
        var now = DateTimeOffset.UtcNow;
        _recoveryDueAt ??= now + _recoveryInterval;
        if (now < _recoveryDueAt) return;
        _recoveryDueAt = now + _recoveryInterval;
        await CaptureRecoveryWithStatus();
    }

    /// <summary>The path a restored file was recovered from, so its snapshot replaces the old entry instead of duplicating it.</summary>
    private string? OriginalSource(string kind, string? path) =>
        path is not null && _recoveryOrigins.TryGetValue((kind, path), out var origin) ? origin.Source : path;

    internal void CaptureRecoveryForTest()
    {
        CaptureRecovery();
        RecoveryTestMode = true;
    }

    private bool _recoverySnapshotFailureShown;

    /// <summary>Shows "Making safety backup…" in the status bar while a snapshot with unsaved work runs.</summary>
    private async Task CaptureRecoveryWithStatus()
    {
        if (!HasUnsavedWork()) { CaptureRecovery(); return; }
        string previous = StatusText.Text ?? "";
        const string message = "Making safety backup…";
        StatusText.Text = message;
        // Let the status bar paint before the snapshot blocks the UI thread.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        CaptureRecovery();
        if (StatusText.Text == message) StatusText.Text = previous;
    }

    private void CaptureRecovery()
    {
        var snapshots = new List<(RecoverySnapshot Item, byte[] Data)>();
        var failed = new List<string>();
        string tempRoot = RecoveryStore.TempDirectory;
        try { Directory.CreateDirectory(tempRoot); }
        catch (Exception ex)
        {
            CrashLog.Write("CaptureRecovery.TempDirectory", ex);
            ShowRecoveryFailure([ex.Message]);
            return;
        }
        void Add(string kind, string title, string? sourcePath, bool embedded, Func<byte[]?, byte[]> getBytes,
            int group = 0, int index = 0, string? contextPath = null, string? contextHash = null, bool dirty = true,
            string? sessionVersion = null)
        {
            try
            {
                string? origin = OriginalSource(kind, sourcePath);
                byte[]? sourceBytes = origin is not null && File.Exists(origin) ? File.ReadAllBytes(origin) : null;
                byte[] data = getBytes(sourceBytes);
                string ext = kind switch { "sff" => ".sff", "air" => embedded ? ".def" : ".air", "snd" => ".snd", _ => ".rgba" };
                string fileName = $"{kind}-{Guid.NewGuid():N}{ext}";
                string? hash = sourceBytes is null ? null : Convert.ToHexString(SHA256.HashData(sourceBytes));
                snapshots.Add((new(kind, title, origin, hash, fileName, embedded, group, index, dirty,
                    contextPath, contextHash, sessionVersion,
                    Tab: _doc.Id, TabOrder: _docs.IndexOf(_doc), TabDef: _doc.DefPath, TabName: _doc.CustomName), data));
            }
            catch (Exception ex)
            {
                CrashLog.Write($"CaptureRecovery.{kind}", ex);
                failed.Add($"{title}: {ex.Message}");
            }
        }

        // Every tab with unsaved work. _doc is pointed at each in turn so the fields below read that
        // tab; nothing here touches the UI, and it is put back before anything else runs. Pending
        // paint, sound edits and color adjust belong to the active tab only.
        var activeDoc = _doc;
        try
        {
            foreach (var tab in _docs)
            {
                if (!DocumentHasUnsavedWork(tab)) continue;
                _doc = tab;
                CaptureTab(ReferenceEquals(tab, activeDoc));
            }
        }
        finally
        {
            _doc = activeDoc;
        }

        void CaptureTab(bool isActive)
        {
            // SFFv1 can't hold true-color sprites; such a session is snapshotted as v2 and set back to v1 on recovery.
            bool sffAsV2 = _file is not null && SffVersions.IsV1(_file.Version) && !SffWriter.FitsV1(_file);
            string? sessionVersion = sffAsV2 ? _file!.Version.ToString() : null;
            byte[] SffSnapshot()
            {
                var file = _file!;
                var version = file.Version;
                string temp = Path.Combine(tempRoot, Guid.NewGuid().ToString("N") + ".sff");
                try
                {
                    if (sffAsV2) file.Version = SffVersions.V2_1; // the v2 writer rekeys palettes only for a v1 file
                    file.Save(temp, file.Version, clamp: false);
                    return File.ReadAllBytes(temp);
                }
                finally
                {
                    file.Version = version;
                    if (File.Exists(temp)) File.Delete(temp);
                }
            }

            if (_dirty && _file is not null && (!isActive || _paintBaseline is null))
                Add("sff", _path is null ? "New sprite file" : Path.GetFileName(_path), _path, false, _ => SffSnapshot(),
                    sessionVersion: sessionVersion);
            if (_airDirty && _air is { } air && (!_airEmbeddedInDef || _airPath is not null))
            {
                bool embedded = _airEmbeddedInDef;
                var sourceActions = _airDefSourceActions;
                Add("air", _airPath is null ? "New animation file" : Path.GetFileName(_airPath), _airPath, embedded, sourceBytes =>
                {
                    if (embedded)
                    {
                        if (sourceBytes is null) throw new FileNotFoundException("The definition source is missing.", _airPath);
                        return EmbeddedAirDocument.ReplaceActions(sourceBytes, air, sourceActions, out _);
                    }
                    return AirFile.Encode(AirWriter.Write(air), air.TextEncoding);
                });
            }
            if (_sndDirty && _snd is { } snd && (!isActive || _soundEditBaseline is null))
                Add("snd", _sndPath is null ? "New sound file" : Path.GetFileName(_sndPath), _sndPath, false, _ =>
                {
                    string temp = Path.Combine(tempRoot, Guid.NewGuid().ToString("N") + ".snd");
                    try { snd.Save(temp); return File.ReadAllBytes(temp); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                });
            if (_file is not null)
            {
                var dirtyExternal = _file.Palettes.Where(p => p.Source == PaletteSource.External && p.Dirty
                    && !(isActive && HasPendingColorAdjust() && ReferenceEquals(p, _editingPalette))).ToList();
                if (dirtyExternal.Count > 0 && !_dirty)
                    Add("sff", _path is null ? "Sprite file for recovered palettes" : Path.GetFileName(_path),
                        _path, false, _ => SffSnapshot(), dirty: false, sessionVersion: sessionVersion);
                foreach (var pal in dirtyExternal)
                {
                    Add("act", pal.SourcePath is null ? $"New external palette {pal.Group},{pal.Index}" : Path.GetFileName(pal.SourcePath),
                        pal.SourcePath, false, _ =>
                        {
                            var bytes = new byte[pal.Colors.Length * 4];
                            for (int i = 0; i < pal.Colors.Length; i++)
                            {
                                var c = pal.Colors[i];
                                bytes[i * 4] = c.R; bytes[i * 4 + 1] = c.G; bytes[i * 4 + 2] = c.B; bytes[i * 4 + 3] = c.A;
                            }
                            return bytes;
                        }, pal.Group, pal.Index, OriginalSource("sff", _path), RecoveryStore.HashFile(OriginalSource("sff", _path)));
                }
            }
        }

        try
        {
            if (snapshots.Count > 0) RecoveryStore.Publish(snapshots);
        }
        catch (Exception ex)
        {
            CrashLog.Write("CaptureRecovery.Publish", ex);
            failed.Clear();
            failed.Add(ex.Message);
        }
        if (failed.Count > 0) ShowRecoveryFailure(failed);
    }

    /// <summary>The timer repeats every few minutes; say it once per session, not on every tick.</summary>
    private void ShowRecoveryFailure(List<string> reasons)
    {
        if (_recoverySnapshotFailureShown) return;
        _recoverySnapshotFailureShown = true;
        ShowPopup("Recovery Snapshot Failed",
            "Unsaved changes may not be recoverable after a crash:\n" + string.Join("\n", reasons));
    }

    /// <summary>
    /// Discarding a tab's work drops that tab's snapshots only; other tabs' snapshots, and a package
    /// the user kept for later, are left alone. Other unsaved work is then captured again soon.
    /// </summary>
    internal void DiscardRecoveryForDiscardedWork() => DiscardRecoveryForTab(_doc);

    private void DiscardRecoveryForTab(Document doc)
    {
        if (RecoveryTestMode) return;
        try { RecoveryStore.ResolveTab(doc.Id); }
        catch (Exception ex) { CrashLog.Write("DiscardRecoveryForTab", ex); }
        if (HasUnsavedWork()) _recoveryDueAt = DateTimeOffset.UtcNow; // the dropped tab may have held another file's snapshot
    }

    private async Task<bool> ConfirmRecoveredSourceWrite(string? path)
    {
        if (path is null || !_recoverySourceHashes.TryGetValue(path, out var expected)) return true;
        string? current = RecoveryStore.HashFile(path);
        if (current == expected) return true;
        bool overwrite = await Dialogs.Confirm(this, "File Changed Outside Media",
            $"{Path.GetFileName(path)} changed on disk after its recovered snapshot. Saving will overwrite it.", "Save");
        if (overwrite && current is not null) _recoverySourceHashes[path] = current;
        return overwrite;
    }

    /// <summary>Offers any unresolved recovery package. Returns true when the recovered session was restored.</summary>
    internal async Task<bool> OfferRecovery()
    {
        if (!RecoveryStore.HasPackage) return false;
        try
        {
            var manifest = RecoveryStore.ReadActive();
            if (manifest is null || manifest.Items.Count == 0) return false;
            var action = await Dialogs.PromptRecovery(this, manifest);
            if (action == RecoveryChoice.Discard)
            {
                RecoveryStore.DiscardActive();
                return false;
            }
            // An unresolved package stays until it is recovered and saved, or discarded.
            _preserveRecoveryPackage = true;
            if (action != RecoveryChoice.Recover) return false;
            await RestoreRecovery(manifest);
            return true;
        }
        catch (Exception ex)
        {
            await Failed("OfferRecovery", ex);
            return false;
        }
    }

    /// <summary>Snapshots from before tabs have no tab; they restore together into one.</summary>
    private const string UntabbedRecovery = "untabbed";

    /// <summary>One project tab per recovered tab, in their old order, labeled as before.</summary>
    private async Task RestoreRecovery(RecoveryManifest manifest)
    {
        var tabs = manifest.Items.GroupBy(i => i.Tab ?? UntabbedRecovery).OrderBy(g => g.Min(i => i.TabOrder)).ToList();
        foreach (var tab in tabs)
        {
            if (!IsEmptyDocument(_doc) && !await AddProjectTab()) break;
            _doc.Id = tab.Key; // so saving or discarding resolves these snapshots
            var first = tab.First();
            _doc.CustomName = first.TabName;
            if (first.TabDef is { } def && File.Exists(def))
            {
                _doc.DefPath = def;
                try
                {
                    var parsed = Def.DefFile.Open(def);
                    _doc.DefName = string.IsNullOrWhiteSpace(parsed.Name) ? null : parsed.Name.Trim();
                    _characterName = parsed.DisplayName ?? parsed.Name ?? Path.GetFileNameWithoutExtension(def);
                }
                catch (Exception ex) { CrashLog.Write("RestoreRecovery.Def", ex); }
            }
            RestoreRecoveryTab(tab.ToList());
        }
        RebuildProjectTabStrip();
        StatusText.Text = "Recovered unsaved files. Save them to keep the recovered changes.";
    }

    private void RestoreRecoveryTab(List<RecoverySnapshot> items)
    {
        foreach (var item in items)
        {
            string snapshot = RecoveryStore.SnapshotPath(item);
            bool same = RecoveryStore.SourceMatches(item);
            switch (item.Kind)
            {
                case "sff":
                    _file = OpenSffSnapshot(snapshot, item);
                    _path = same ? item.SourcePath : PersistRecoveredCopy(snapshot);
                    if (!same) _recoveryOrigins[("sff", _path!)] = (item.SourcePath, 0, 0);
                    if (same && item.SourcePath is not null && item.SourceHash is not null)
                        _recoverySourceHashes[item.SourcePath] = item.SourceHash;
                    ApplyLoadedSff();
                    _dirty = item.Dirty;
                    if (_dirty) _undo.Untracked(SffKind);
                    break;
                case "air":
                    if (item.Embedded)
                    {
                        string definitionPath = same ? item.SourcePath! : PersistRecoveredCopy(snapshot, ".def");
                        if (!same) _recoveryOrigins[("air", definitionPath)] = (item.SourcePath, 0, 0);
                        if (same && item.SourceHash is not null) _recoverySourceHashes[definitionPath] = item.SourceHash;
                        byte[] bytes = File.ReadAllBytes(definitionPath);
                        var (text, encoding, legacy) = AirFile.Decode(bytes);
                        var air = AirParser.Parse(text);
                        air.TextEncoding = encoding;
                        air.LegacyEncodingName = legacy;
                        LoadAirFromEmbeddedText(air, definitionPath, bytes, showSuggestedNames: false);
                        _airDefSourceActions = [.. air.Actions];
                        _airPath = definitionPath;
                        _airEmbeddedInDef = true;
                    }
                    else
                    {
                        LoadAir(snapshot);
                        _airPath = same ? item.SourcePath : PersistRecoveredCopy(snapshot, ".air");
                        if (!same && _airPath is not null) _recoveryOrigins[("air", _airPath)] = (item.SourcePath, 0, 0);
                        if (same && item.SourcePath is not null && item.SourceHash is not null)
                            _recoverySourceHashes[item.SourcePath] = item.SourceHash;
                    }
                    _airDirty = true;
                    _undo.Untracked(AirKind);
                    break;
                case "snd":
                    _snd = SndFile.Open(snapshot);
                    _sndPath = same ? item.SourcePath : PersistRecoveredCopy(snapshot, ".snd");
                    if (!same && _sndPath is not null) _recoveryOrigins[("snd", _sndPath)] = (item.SourcePath, 0, 0);
                    if (same && item.SourcePath is not null && item.SourceHash is not null)
                        _recoverySourceHashes[item.SourcePath] = item.SourceHash;
                    _sndDirty = true;
                    _undo.Untracked(SndKind);
                    SoundFilterBox.Text = "";
                    ApplySoundFilter();
                    ShowSound();
                    UpdateSndInfo();
                    break;
                case "act":
                    if (_file is null && item.ContextPath is not null)
                    {
                        var contextSff = items.FirstOrDefault(x => x.Kind == "sff"
                            && string.Equals(x.SourcePath, item.ContextPath, StringComparison.OrdinalIgnoreCase));
                        if (contextSff is not null)
                        {
                            string sffSnapshot = RecoveryStore.SnapshotPath(contextSff);
                            _file = OpenSffSnapshot(sffSnapshot, contextSff);
                            bool contextSame = RecoveryStore.SourceMatches(contextSff);
                            _path = contextSame ? contextSff.SourcePath : PersistRecoveredCopy(sffSnapshot);
                            ApplyLoadedSff();
                            _dirty = contextSff.Dirty;
                            if (_dirty) _undo.Untracked(SffKind);
                        }
                    }
                    RestoreActSnapshot(item, snapshot, same);
                    break;
                default:
                    CrashLog.Error("Recovery", $"Unknown recovery kind '{item.Kind}'");
                    break;
            }
        }
        UpdateTitle();
        UpdateAirInfo();
        UpdateSndInfo();
        SyncSaveMenu();
    }

    /// <summary>Opens an SFF snapshot, putting back the session's version when it was stored as v2.</summary>
    private static SffFile OpenSffSnapshot(string snapshot, RecoverySnapshot item)
    {
        var file = SffFile.Open(snapshot);
        if (item.SessionVersion?.Split('.') is { Length: 4 } q
            && byte.TryParse(q[0], NumberStyles.None, CultureInfo.InvariantCulture, out byte hi)
            && byte.TryParse(q[1], NumberStyles.None, CultureInfo.InvariantCulture, out byte lo1)
            && byte.TryParse(q[2], NumberStyles.None, CultureInfo.InvariantCulture, out byte lo2)
            && byte.TryParse(q[3], NumberStyles.None, CultureInfo.InvariantCulture, out byte lo3))
            file.Version = file.CompatVersion = new SffVersion(hi, lo1, lo2, lo3);
        else if (item.SessionVersion is not null)
            CrashLog.Error("Recovery", $"Unreadable session version '{item.SessionVersion}'; kept {file.Version}");
        return file;
    }

    private static string PersistRecoveredCopy(string snapshot, string? extension = null)
    {
        Directory.CreateDirectory(RecoveryStore.RecoveredDirectory);
        string ext = extension ?? Path.GetExtension(snapshot);
        string destination = Path.Combine(RecoveryStore.RecoveredDirectory,
            $"recovered-{Path.GetFileNameWithoutExtension(snapshot)}{ext}");
        File.Copy(snapshot, destination, overwrite: true);
        return destination;
    }

    private void RestoreActSnapshot(RecoverySnapshot item, string snapshot, bool same)
    {
        if (_file is null)
        {
            ShowPopup("Palette Not Recovered", $"Recovered palette {item.Title} was not applied because its sprite file is not open.");
            return;
        }
        byte[] data = File.ReadAllBytes(snapshot);
        if (data.Length % 4 != 0) throw new InvalidDataException("The recovered palette data is invalid.");
        var colors = new Rgba[data.Length / 4];
        for (int i = 0; i < colors.Length; i++) colors[i] = new(data[i * 4], data[i * 4 + 1], data[i * 4 + 2], data[i * 4 + 3]);
        var pal = _file.Palettes.FirstOrDefault(p => p.Source == PaletteSource.External
            && p.Group == item.Group && p.Index == item.Index);
        if (pal is null)
        {
            pal = new Palette { Group = item.Group, Index = item.Index, Source = PaletteSource.External };
            _file.Palettes.Add(pal);
        }
        pal.Colors = colors;
        pal.SourcePath = same ? item.SourcePath : PersistRecoveredCopy(snapshot, ".act");
        if (!same && pal.SourcePath is not null) _recoveryOrigins[("act", pal.SourcePath)] = (item.SourcePath, item.Group, item.Index);
        if (same && item.SourcePath is not null && item.SourceHash is not null)
            _recoverySourceHashes[item.SourcePath] = item.SourceHash;
        pal.Dirty = true;
        RebuildPaletteList();
        RefreshPaletteTab();
    }
}
