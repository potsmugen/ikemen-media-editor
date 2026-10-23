using System.Globalization;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sff;

namespace Editor;

public partial class MainWindow
{
    // ---- file -------------------------------------------------------------

    /// <param name="recordRecent">False when opening as part of a character:
    /// OpenCharacterFile records the .def itself, and recording the .sff too
    /// was a leftover from when the app opened sprites only — recents filled
    /// with an entry that bypasses the .def and reopens the SFF alone.</param>
    public void OpenFile(string path, bool recordRecent = true, SffFile? loaded = null)
    {
        try
        {
            _file = loaded ?? SffFile.Open(path);
            DiscardRecoveryForDiscardedWork();
        }
        catch (Exception e)
        {
            CrashLog.Write("OpenFile", e);
            StatusText.Text = $"Could not open {Path.GetFileName(path)}.";
            ShowPopup("Could Not Open File", $"Could not open {Path.GetFileName(path)}: {e.Message}");
            return;
        }

        if (_file is null)
        {
            RestorePendingStandalonePalettes();
            return;
        }
        _path = path;
        ApplyLoadedSff();

        if (recordRecent)
        {
            _settings.AddRecent(path);
            _settings.Save();
            RebuildRecentMenu();
        }

        int failed = _file.Sprites.Count(s => s.DecodeError is not null);
        var summary = $"SFF {_file.Version}: {_file.Sprites.Count} sprites, {_file.Palettes.Count} palettes";
        if (failed > 0) summary += $", {failed} failed to decode";
        if (_file.Version.Hi == 1) summary += ". Saving converts to SFFv2.";
        StatusText.Text = summary;
        if (_file.V1SharedPalette)
            ShowPopup("Global Shared Palette",
                "This SFF file uses a global shared palette: sprites marked to reuse a palette use the first one.\n"
                + "Saving it in the editor will convert it to the standard \u201cuse previous palette\u201d format.");
    }

    /// <summary>Opens the file passed on the command line (Windows "Open with"), routed by
    /// extension the same way the matching menu command opens it.</summary>
    internal async Task OpenLaunchFile(string path)
    {
        try
        {
            CrashLog.Trace($"[launch] opening {path}");
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".imesession": await LoadSessionFile(path); break;
                case ".snd": await OpenSndFromPath(path); break;
                case ".air": await OpenAirPath(path); break;
                case ".def": await OpenDefWithTabPrompt(path); break;
                case ".act" or ".pal" or ".rgba": await ImportPaletteFiles([(Path.GetFileName(path), path)]); break;
                default: OpenFile(path); break;
            }
        }
        catch (Exception ex)
        {
            await Failed("OpenLaunchFile", ex);
        }
    }

    /// <summary>Reads an SFF on a background thread under the busy lock. Null when it can't be read; OpenFile then reports the error.</summary>
    private async Task<SffFile?> ReadSffInBackgroundAsync(string path)
    {
        try
        {
            return await RunFileWorkAsync($"Opening {Path.GetFileName(path)}…", () => SffFile.Open(path));
        }
        catch (Exception e)
        {
            CrashLog.Write("OpenFile", e);
            return null;
        }
    }

    /// <summary>Loads an SFF on a background thread under the busy lock, then opens it.</summary>
    private async Task OpenFileInBackground(string path)
    {
        SffFile loaded;
        try
        {
            loaded = await RunFileWorkAsync($"Opening {Path.GetFileName(path)}…", () => SffFile.Open(path));
        }
        catch (Exception e)
        {
            CrashLog.Write("OpenFile", e);
            StatusText.Text = $"Could not open {Path.GetFileName(path)}.";
            ShowPopup("Could Not Open File", $"Could not open {Path.GetFileName(path)}: {e.Message}");
            return;
        }
        OpenFile(path, loaded: loaded);
    }

    /// <summary>Starts a blank .sff (no sprites or palettes) in place of whatever's
    /// open — the "start from scratch" counterpart to Open. Asks which version first,
    /// since a brand new file has nothing on disk to infer one from.</summary>
    private async void OnNewSff(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!await ConfirmDiscard()) return;
            if (await Dialogs.PromptSffVersion(this) is not { } version) return;

            DiscardRecoveryForDiscardedWork();
            _file = new SffFile { Version = version };
            _path = null;
            ApplyLoadedSff();
            StatusText.Text = $"New sprite file (SFF {version}). Add sprites, then Save.";
            SwitchToTab(0);
        }
        catch (Exception ex)
        {
            await Failed("OnNewSff", ex);
        }
    }

    /// <summary>
    /// Resets editor state for whatever now sits in _file — a freshly opened .sff or
    /// a brand new blank one: clears selection/undo/filter, rebuilds the palette and
    /// sprite lists, and refreshes the title and per-tab status. Shared by OpenFile
    /// and New.
    /// </summary>
    private void ApplyLoadedSff()
    {
        TransferStandalonePalettesToSff();
        CrashLog.Trace($"[file] sff now: {_path ?? "(new, unsaved)"} — {_file?.Sprites.Count ?? 0} sprites, {_file?.Palettes.Count ?? 0} palettes");
        // A "share pixels with..." pick left running from a previous file
        // would otherwise complete against this new file's sprites once the
        // list below selects row 0 — the sprite it was started for no longer
        // belongs to anything. That was the likely cause of the editor going
        // unresponsive after opening a second character. Palette link picks
        // need the same guard against a row in the new file.
        CancelPick(restoreSelection: false);
        CancelPaletteLinkPick(restoreSelection: false);

        _dirty = false;
        _paletteOverride = -1;
        _undo.Clear(() =>
        {
            if (_file is not null) _dirty = false;
            MarkCleanKindsSaved();
        });
        // Suppressed: an unguarded FilterBox.Text reset used to trigger a
        // full ApplyFilter/RebuildSpriteTable pass on its own, then the
        // explicit ApplyFilter() below repeated the identical 1591-row
        // rebuild a second time — doubling an already expensive rebuild.
        _suppressEvents = true;
        FilterBox.Text = "";
        _suppressEvents = false;

        RebuildPaletteList();
        PaletteBox.SelectedIndex = 0;

        ApplyFilter();
        SpriteCanvas.ResetView();
        UpdateTitle();
        UpdateSffInfo();
        FocusTabList();
    }

    /// <summary>
    /// Sprite and palette totals for the status bar, mirroring the Sounds
    /// tab's corner summary. Both come from the one SffFile, so one update
    /// covers both the Sprites and Palettes tabs.
    /// </summary>
    private void UpdateSffInfo()
    {
        if (SffStatusText is null || PaletteStatusText is null) return;
        if (_file is null) { SffStatusText.Text = ""; PaletteStatusText.Text = ""; return; }

        string fileName = _path is null ? "Unsaved SFF" : Path.GetFileName(_path);
        int failed = _file.Sprites.Count(s => s.DecodeError is not null);
        int embeddedPalettes = _file.Palettes.Count(p => p.Source == PaletteSource.Embedded);
        int externalPalettes = _file.Palettes.Count(p => p.Source == PaletteSource.External);
        SffStatusText.Text = $"{fileName}: {_file.Sprites.Count} sprite{(_file.Sprites.Count == 1 ? "" : "s")}"
            + $", {embeddedPalettes} SFF palette{(embeddedPalettes == 1 ? "" : "s")}"
            + (failed > 0 ? $", {failed} failed" : "");
        PaletteStatusText.Text = $"{fileName}: {embeddedPalettes} embedded palette{(embeddedPalettes == 1 ? "" : "s")}, "
            + $"{externalPalettes} external palette{(externalPalettes == 1 ? "" : "s")}";
    }

    /// <summary>The open Media Health Report (it isn't modal); reopening replaces it with a fresh one.</summary>
    private Window? _healthReport;

    /// <summary>Quick, non-mutating checks for the most common cross-file media problems. Counts first,
    /// then one line per problem; those lines jump to the sprite, palette, action/frame or sound.</summary>
    private async void OnShowHealthReport(object? sender, RoutedEventArgs e)
    {
        try
        {
            var lines = new List<(string Text, Action? Jump)>();
            var details = new List<(string Text, Action? Jump)>();
            void Section(string heading, IEnumerable<(string Text, Action Jump)> items)
            {
                var list = items.ToList();
                if (list.Count == 0) return;
                details.Add(("", null));
                details.Add((heading, null));
                details.AddRange(list.Select(x => (x.Text, (Action?)x.Jump)));
            }

            if (_file is { } file)
            {
                var bad = file.Sprites.Where(s => s.DecodeError is not null).ToList();
                var duplicateSprites = file.Sprites.GroupBy(s => (s.Group, s.Index)).SelectMany(g => g.Skip(1)).ToList();
                // Only the SFF's own palettes: a character's .act files (external) share keys with them by design.
                var duplicatePaletteKeys = file.Palettes.Where(p => p.Source == PaletteSource.Embedded)
                    .GroupBy(p => (p.Group, p.Index)).SelectMany(g => g.Skip(1)).ToList();
                var embeddedPalettes = file.Palettes.Where(p => p.Source == PaletteSource.Embedded).ToList();
                var duplicatePaletteColors = file.FindDuplicatePalettes(embeddedPalettes)
                    .SelectMany(set => set.Skip(1).Select(p => (Palette: p, Original: set[0]))).ToList();
                // Group 1 is where a character keeps its selectable palettes.
                // The game allocates the one the player picked at 1,1, so an
                // SFF sprite need never reference these — an unreferenced one
                // is still used in-game and isn't dead weight.
                var unused = embeddedPalettes
                    .Where(p => !p.IsCharacterPalette && !file.Sprites.Any(s => ReferenceEquals(s.Palette, p))).ToList();
                lines.Add(($"SFF: {file.Sprites.Count} sprites, {embeddedPalettes.Count} palettes", null));
                lines.Add(($"  Decode failures: {bad.Count}", null));
                lines.Add(($"  Duplicate sprite keys: {duplicateSprites.Count}", null));
                lines.Add(($"  Duplicate SFF palette keys: {duplicatePaletteKeys.Count}", null));
                lines.Add(($"  Duplicate embedded palette colors: {duplicatePaletteColors.Count}", null));
                lines.Add(($"  Unused embedded palettes: {unused.Count} (group 1 not counted)", null));
                Section("Decode failures:", bad.Select(s => ($"  Sprite {s.Group},{s.Index}: {s.DecodeError}", (Action)(() => JumpToSprite(s)))));
                Section("Duplicate sprite keys (the later ones):", duplicateSprites.Select(s => ($"  Sprite {s.Group},{s.Index}", (Action)(() => JumpToSprite(s)))));
                Section("Duplicate SFF palette keys (the later ones):", duplicatePaletteKeys.Select(p => ($"  Palette {PaletteKey(file, p)}", (Action)(() => JumpToPalette(p)))));
                Section("Duplicate embedded palette colors (later palettes match the first):", duplicatePaletteColors.Select(x =>
                    ($"  Palette {PaletteKey(file, x.Palette)} matches {PaletteKey(file, x.Original)}", (Action)(() => JumpToPalette(x.Palette)))));
                Section("Unused embedded palettes:", unused.Select(p => ($"  Palette {PaletteKey(file, p)}", (Action)(() => JumpToPalette(p)))));
            }
            if (_air is { } air)
            {
                List<(Air.AnimationAction Action, int Frame, Air.AnimationElement Element)> missing = _file is null ? []
                    : air.Actions.SelectMany(a => a.Elements.Select((e, i) => (Action: a, Frame: i, Element: e)))
                        .Where(x => !x.Element.NoSprite && _file.Find(x.Element.Group, x.Element.Index) is null).ToList();
                lines.Add(($"AIR: {air.Actions.Count} actions, {air.Actions.Sum(a => a.Elements.Count)} frames", null));
                lines.Add(("  Encoding: " + air.TextEncoding switch
                {
                    Air.AirEncoding.Utf8Bom => "UTF-8 with byte-order mark",
                    Air.AirEncoding.Legacy => $"not UTF-8, read as {air.LegacyEncodingName} (a guess); saving converts it to UTF-8",
                    _ => "UTF-8",
                }, null));
                if (_file is not null) lines.Add(($"  Missing sprite references: {missing.Count}", null)); // needs the .sff open
                var lint = air.Actions.Where(a => a.ChangesOnSave).ToList();
                lines.Add(($"  Clsn lines numbered out of order: {lint.Sum(a => a.MisnumberedClsnLines)}", null));
                lines.Add(($"  Clsn counts not matching their box lines: {lint.Sum(a => a.MismatchedClsnCounts)}", null));
                lines.Add(($"  Comments with no place of their own (moved after the action's last frame on save): {lint.Sum(a => a.MovedComments)}", null));
                var both = air.Actions.SelectMany(a => a.FramesWithDefaultAndOwnClsn().Select(i => (Action: a, Frame: i))).ToList();
                lines.Add(($"  Frames with both a Clsn default and their own Clsn (invalid): {both.Count}", null));

                Section("Missing sprite references (action, frame: sprite):", missing.Select(x =>
                    ($"  Action {x.Action.Number}, frame {x.Frame + 1}: {x.Element.Group},{x.Element.Index}", (Action)(() => JumpToFrame(x.Action, x.Element)))));
                Section("Frames with both a Clsn default and their own Clsn (action, frame):", both.Select(x =>
                    ($"  Action {x.Action.Number}, frame {x.Frame + 1}", (Action)(() => JumpToFrame(x.Action, x.Action.Elements[x.Frame])))));
                Section("Written differently on save:", lint.Select(a => ($"  Action {a.Number}: " + string.Join(", ", new[]
                {
                    a.MisnumberedClsnLines > 0 ? $"{a.MisnumberedClsnLines} Clsn lines misnumbered" : null,
                    a.MismatchedClsnCounts > 0 ? $"{a.MismatchedClsnCounts} Clsn counts off" : null,
                    a.MovedComments > 0 ? $"{a.MovedComments} comments moved" : null,
                }.Where(p => p is not null)), (Action)(() => JumpToAction(a)))));
            }
            if (_snd is { } snd)
            {
                var unreadable = snd.Sounds.Where(s => s.Problem is not null).ToList();
                var duplicates = snd.Duplicates().ToList();
                lines.Add(($"SND: {snd.Sounds.Count} sounds", null));
                lines.Add(($"  Unreadable headers: {unreadable.Count}", null));
                lines.Add(($"  Duplicate sound keys: {duplicates.Count}", null));
                Section("Unreadable headers:", unreadable.Select(s => ($"  Sound {s.Group},{s.Index}: {s.Problem}", (Action)(() => JumpToSound(s)))));
                Section("Duplicate sound keys (the later ones):", duplicates.Select(s => ($"  Sound {s.Group},{s.Index}", (Action)(() => JumpToSound(s)))));
            }
            if (lines.Count == 0) lines.Add(("No media is loaded.", null));

            _healthReport?.Close();
            _healthReport = Dialogs.ShowReport(this, "Media Health Report", [.. lines, .. details]);
            _healthReport.Closed += (s, _) => { if (ReferenceEquals(s, _healthReport)) _healthReport = null; };
        }
        catch (Exception ex)
        {
            await Failed("OnShowHealthReport", ex);
        }
    }

    // ---- Health report jumps: each checks the item is still there (the report is a snapshot) ----

    private void JumpToSprite(Sprite sprite)
    {
        if (_file is null || _file.PositionOf(sprite) is not (var pos and >= 0)) { StatusText.Text = "That sprite is no longer in the file."; return; }
        Tabs.SelectedIndex = 0;
        SelectSprite(pos);
    }

    private void JumpToPalette(Palette palette)
    {
        if (_file is null || !_file.Palettes.Contains(palette)) { StatusText.Text = "That palette is no longer in the file."; return; }
        Tabs.SelectedIndex = 1;
        SelectPalette(palette);
    }

    private void JumpToAction(Air.AnimationAction action)
    {
        if (_air is null || !_air.Actions.Contains(action)) { StatusText.Text = "That action is no longer in the file."; return; }
        Tabs.SelectedIndex = 2;
        SelectAction(action);
    }

    /// <summary>By element, not index, so it still lands on the right frame after frames were added or moved.</summary>
    private void JumpToFrame(Air.AnimationAction action, Air.AnimationElement element)
    {
        JumpToAction(action);
        if (_action != action) return;
        int frame = action.Elements.IndexOf(element);
        if (frame < 0) { StatusText.Text = "That frame is no longer in the action."; return; }
        FrameList.SelectedIndex = frame;
    }

    private void JumpToSound(Snd.SoundEntry sound)
    {
        if (_snd is null || _snd.Sounds.IndexOf(sound) is not (var index and >= 0)) { StatusText.Text = "That sound is no longer in the file."; return; }
        Tabs.SelectedIndex = 3;
        int row = _visibleSounds.IndexOf(index);
        if (row < 0 && SoundFilterBox.Text is { Length: > 0 })
        {
            SoundFilterBox.Text = ""; // it's hidden by the filter, same as the sprite list's jumps
            ApplySoundFilter();
            row = _visibleSounds.IndexOf(index);
        }
        if (row < 0) return;
        SoundList.SelectedIndex = row;
        SoundList.ScrollIntoView(row);
    }

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!await ConfirmDiscard(resolveStandalone: false)) return;

            var files = await PickOpen(new FilePickerOpenOptions
            {
                Title = "Open SFF",
                AllowMultiple = false,
                FileTypeFilter = [SffType],
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } p) return;
            if (!await ConfirmStandalonePalettesForSff()) return;
            await OpenFileInBackground(p);
            if (_file is not null) SwitchToTab(0);
        }
        catch (Exception ex)
        {
            await Failed("OnOpen", ex);
        }
    }

    /// <summary>
    /// Opens a character, stage, screenpack, lifebar or storyboard .def with every file it
    /// points at; the kind is detected (see DetectDefKind). Embedded animation edits use the
    /// Animations tab's definition-source workflow.
    /// </summary>
    private async void OnOpenDef(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await PickOpen(new FilePickerOpenOptions
            {
                Title = "Open .def",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Definitions") { Patterns = ["*.def"] }],
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            await OpenDefWithTabPrompt(path);
        }
        catch (Exception ex)
        {
            await Failed("OnOpenDef", ex);
        }
    }

    /// <summary>
    /// Loads every file a character's .def points at. Reached through OpenDefAs,
    /// never the sprite-only OpenFile (which can't parse a .def).
    /// </summary>
    private async Task OpenCharacterFile(string path)
    {
        Def.DefFile def;
        try
        {
            def = Def.DefFile.Open(path);
        }
        catch (Exception ex)
        {
            CrashLog.Write("OpenCharacterFile", ex);
            await Dialogs.ShowMessage(this, "Could Not Read the Character Definition", ex.Message);
            return;
        }

        if (def.SpritePath is { } candidateSff && File.Exists(candidateSff))
        {
            if (!await ConfirmStandalonePalettesForSff()) return;
        }
        else if (_file is null && !await ResolveStandalonePalettesBeforeOpen()) return;
        ResetAllState();
        var loaded = new List<string>();
        var failed = new List<string>();

        if (def.SpritePath is { } sff && File.Exists(sff))
        {
            OpenFile(sff, recordRecent: false, loaded: await ReadSffInBackgroundAsync(sff));
            if (_file is not null) loaded.Add(Path.GetFileName(sff));
        }
        if (def.AnimationPath is { } air && File.Exists(air) && LoadAir(air)) loaded.Add(Path.GetFileName(air));
        if (def.SoundPath is { } snd && File.Exists(snd) && LoadSnd(snd, await ReadSndInBackgroundAsync(snd))) loaded.Add(Path.GetFileName(snd));

        // Character .act palettes are external resources, separate from the
        // palettes stored in the SFF. Keep both in the list; the sprite only
        // receives an embedded copy when the user assigns an external palette.
        int palettes = 0;
        foreach (var (number, palPath) in def.Palettes)
        {
            if (!File.Exists(palPath)) continue;
            try
            {
                var pal = ActFile.Open(palPath);
                pal.Index = number;
                _file?.Palettes.Add(pal);
                palettes++;
            }
            catch (Exception ex)
            {
                CrashLog.Write("OpenCharacterFile", ex);
                failed.Add(Path.GetFileName(palPath));
            }
        }
        if (palettes > 0)
        {
            RebuildPaletteList();
            RefreshSprite();
        }
        failed.AddRange(def.MissingFiles().Select(Path.GetFileName)!);

        var who = def.DisplayName ?? def.Name ?? Path.GetFileNameWithoutExtension(path);
        _characterName = who;
        _doc.DefPath = path;
        _doc.DefName = string.IsNullOrWhiteSpace(def.Name) ? null : def.Name.Trim();
        UpdateTitle();
        StatusText.Text = $"{who}: loaded {string.Join(", ", loaded)}"
            + (palettes > 0 ? $", {palettes} .act palettes" : "")
            + (failed.Count > 0 ? $". Missing: {string.Join(", ", failed.Distinct())}" : "")
            + CaseMatchNote(def);
        _settings.AddRecent(path);
        _settings.Save();
        RebuildRecentMenu();
        RestorePendingStandalonePalettes();
        // A .def opens several formats at once, so there's no single relevant tab: stay put.
    }

    private enum DefKind { Character, Stage, Other }

    /// <summary>
    /// Tells a .def's kind by its sprite line: [Files] sprite= (character), [BGdef] spr= (stage),
    /// else [Files] spr=/sff= or [SceneDef] spr= (screenpack/lifebar/storyboard, see
    /// OpenOtherDefFile). Null, after a popup, when it can't be read or matches none.
    /// </summary>
    private async Task<DefKind?> DetectDefKind(string path)
    {
        Def.DefFile def;
        try
        {
            def = Def.DefFile.Open(path);
        }
        catch (Exception ex)
        {
            CrashLog.Write("DetectDefKind", ex);
            await Dialogs.ShowMessage(this, "Could Not Read the Definition", ex.Message);
            return null;
        }
        if (def.SpritePath is not null) return DefKind.Character;
        if (def.StageSpritePath is not null) return DefKind.Stage;
        if (def.ScreenpackSpritePath is not null || def.LifebarSpritePath is not null || def.StoryboardSpritePath is not null)
            return DefKind.Other;

        CrashLog.Error("DetectDefKind", $"no character, stage, screenpack, lifebar or storyboard sprite line: {path}");
        await Dialogs.ShowMessage(this, "Could Not Open the Definition",
            $"{Path.GetFileName(path)} isn't a character, stage, screenpack, lifebar or storyboard .def: "
            + "it has none of their sprite lines ([Files] sprite=, spr= or sff=, [BGdef] spr=, [SceneDef] spr=).");
        return null;
    }

    private Task OpenDefAs(string path, DefKind kind) => kind switch
    {
        DefKind.Stage => OpenStageFile(path),
        DefKind.Other => OpenOtherDefFile(path),
        _ => OpenCharacterFile(path),
    };

    /// <summary>Detects and opens a .def into the current tab with no tab prompt (Load Session).</summary>
    private async Task OpenDefFile(string path)
    {
        if (await DetectDefKind(path) is { } kind) await OpenDefAs(path, kind);
    }

    /// <summary>Open .def, Open Recent and "Open with": detects the kind first, so an unreadable or
    /// unknown .def leaves the tab untouched, then asks New Tab / Current Tab as usual.</summary>
    private async Task OpenDefWithTabPrompt(string path)
    {
        if (await DetectDefKind(path) is not { } kind) return;
        if (!await PrepareTabForProject(path)) return;
        await RunProjectOpen(() => OpenDefAs(path, kind));
    }

    /// <summary>Logs a .def's references found only by ignoring letter case; returns the status-bar note (empty if none).</summary>
    private static string CaseMatchNote(Def.DefFile def)
    {
        foreach (var (written, found) in def.CaseMatches)
            CrashLog.Trace($"[def] {written} not found; using {found} (letter case differs)");
        foreach (var p in def.CaseAmbiguous)
            CrashLog.Error("Def", $"{p} not found, and several files match it ignoring letter case; treated as missing");
        int n = def.CaseMatches.Count;
        return n == 0 ? "" : $". {n} path{(n == 1 ? "" : "s")} matched by ignoring letter case";
    }

    /// <summary>
    /// Loads a stage: its SFF (from [BGdef]'s spr key — a different section
    /// than a character's [Files], see DefFile.StageSpritePath) with whatever
    /// palettes it carries itself, since stages don't use external .act files
    /// the way characters do. If the .def embeds one or more [Begin Action]
    /// blocks for animated background elements — MUGEN/Ikemen stages define
    /// those directly in the .def, not a separate .air — those load too;
    /// AirParser only reacts to lines it recognizes, so handing it the whole
    /// .def text finds them and safely ignores every [BGdef]/[BG ...] config
    /// line around them. No sound: stages don't have one, and starting from
    /// ResetAllState() means a previous character's sounds don't linger
    /// either.
    /// </summary>
    private async Task OpenStageFile(string path)
    {
        byte[] bytes;
        string text;
        Air.AirEncoding encoding;
        string? legacyName;
        Def.DefFile def;
        try
        {
            bytes = await File.ReadAllBytesAsync(path);
            (text, encoding, legacyName) = Air.AirFile.Decode(bytes);
            def = Def.DefFile.Parse(text, Path.GetDirectoryName(Path.GetFullPath(path)) ?? "");
        }
        catch (Exception ex)
        {
            CrashLog.Write("OpenStageFile", ex);
            await Dialogs.ShowMessage(this, "Could Not Read the Stage Definition", ex.Message);
            return;
        }

        if (def.StageSpritePath is null)
        {
            await Dialogs.ShowMessage(this, "Could Not Read the Stage Definition",
                "No [BGdef] spr= line found — this doesn't look like a stage .def.");
            return;
        }

        if (File.Exists(def.StageSpritePath))
        {
            if (!await ConfirmStandalonePalettesForSff()) return;
        }
        else if (_file is null && !await ResolveStandalonePalettesBeforeOpen()) return;
        ResetAllState();

        var loaded = new List<string>();
        var failed = new List<string>();

        if (File.Exists(def.StageSpritePath))
        {
            OpenFile(def.StageSpritePath, recordRecent: false, loaded: await ReadSffInBackgroundAsync(def.StageSpritePath));
            if (_file is not null) loaded.Add(Path.GetFileName(def.StageSpritePath));
        }
        else failed.Add(Path.GetFileName(def.StageSpritePath));

        var air = Air.AirParser.Parse(text);
        if (air.Actions.Count > 0)
        {
            air.TextEncoding = encoding;
            air.LegacyEncodingName = legacyName;
            LoadAirFromEmbeddedText(air, path, bytes, showSuggestedNames: false);
            loaded.Add($"{air.Actions.Count} background action{(air.Actions.Count == 1 ? "" : "s")}");
        }

        var who = def.DisplayName ?? def.Name ?? Path.GetFileNameWithoutExtension(path);
        _characterName = who;
        _doc.DefPath = path;
        _doc.DefName = string.IsNullOrWhiteSpace(def.Name) ? null : def.Name.Trim();
        UpdateTitle();
        StatusText.Text = $"{who}: loaded {string.Join(", ", loaded)}"
            + (failed.Count > 0 ? $". Missing: {string.Join(", ", failed.Distinct())}" : "")
            + CaseMatchNote(def);
        _settings.AddRecent(path);
        _settings.Save();
        RebuildRecentMenu();
        RestorePendingStandalonePalettes();
        // A .def opens several formats at once, so there's no single relevant tab: stay put.
    }

    /// <summary>
    /// Loads whichever of screenpack/lifebar/storyboard the .def turns out
    /// to be (DetectDefKind has ruled out character and stage): ScreenpackSpritePath,
    /// then LifebarSpritePath, then StoryboardSpritePath. One shared method rather
    /// than three near-identical ones (they only differed in which DefFile field to
    /// read and whether a sound file exists).
    ///
    /// Loads the sprite (+ sound, for screenpack/lifebar) plus any
    /// [Begin Action] blocks the .def embeds, same shape as OpenStageFile.
    /// Doesn't follow nested references (a screenpack's select.def/
    /// fight.def/storyboards, a lifebar's common-effects files) — that's
    /// still the open, undecided part.
    /// </summary>
    private async Task OpenOtherDefFile(string path)
    {
        byte[] bytes;
        string text;
        Air.AirEncoding encoding;
        string? legacyName;
        Def.DefFile def;
        try
        {
            bytes = await File.ReadAllBytesAsync(path);
            (text, encoding, legacyName) = Air.AirFile.Decode(bytes);
            def = Def.DefFile.Parse(text, Path.GetDirectoryName(Path.GetFullPath(path)) ?? "");
        }
        catch (Exception ex)
        {
            CrashLog.Write("OpenOtherDefFile", ex);
            await Dialogs.ShowMessage(this, "Could Not Read the Definition", ex.Message);
            return;
        }

        string kind;
        string spritePath;
        string? soundPath = null;
        if (def.ScreenpackSpritePath is { } spr)
        {
            kind = "screenpack";
            spritePath = spr;
            soundPath = def.ScreenpackSoundPath;
        }
        else if (def.LifebarSpritePath is { } sff)
        {
            kind = "lifebar";
            spritePath = sff;
            soundPath = def.LifebarSoundPath;
        }
        else if (def.StoryboardSpritePath is { } scene)
        {
            kind = "storyboard";
            spritePath = scene;
        }
        else
        {
            await Dialogs.ShowMessage(this, "Could Not Read the Definition",
                "Doesn't look like a screenpack, lifebar, or storyboard .def — no [Files] spr=/sff= or [SceneDef] spr= line found.");
            return;
        }

        if (File.Exists(spritePath))
        {
            if (!await ConfirmStandalonePalettesForSff()) return;
        }
        else if (_file is null && !await ResolveStandalonePalettesBeforeOpen()) return;
        ResetAllState();

        var loaded = new List<string>();
        var failed = new List<string>();

        if (File.Exists(spritePath))
        {
            OpenFile(spritePath, recordRecent: false, loaded: await ReadSffInBackgroundAsync(spritePath));
            if (_file is not null) loaded.Add(Path.GetFileName(spritePath));
        }
        else failed.Add(Path.GetFileName(spritePath));

        if (soundPath is not null)
        {
            if (File.Exists(soundPath) && LoadSnd(soundPath, await ReadSndInBackgroundAsync(soundPath))) loaded.Add(Path.GetFileName(soundPath));
            else if (!File.Exists(soundPath)) failed.Add(Path.GetFileName(soundPath));
        }

        var air = Air.AirParser.Parse(text);
        if (air.Actions.Count > 0)
        {
            air.TextEncoding = encoding;
            air.LegacyEncodingName = legacyName;
            LoadAirFromEmbeddedText(air, path, bytes, showSuggestedNames: false);
            loaded.Add($"{air.Actions.Count} {kind} action{(air.Actions.Count == 1 ? "" : "s")}");
        }

        var who = def.DisplayName ?? def.Name ?? Path.GetFileNameWithoutExtension(path);
        _characterName = who;
        _doc.DefPath = path;
        _doc.DefName = string.IsNullOrWhiteSpace(def.Name) ? null : def.Name.Trim();
        UpdateTitle();
        StatusText.Text = $"{who} ({kind}): loaded {string.Join(", ", loaded)}"
            + (failed.Count > 0 ? $". Missing: {string.Join(", ", failed.Distinct())}" : "")
            + CaseMatchNote(def);
        _settings.AddRecent(path);
        _settings.Save();
        RebuildRecentMenu();
        RestorePendingStandalonePalettes();
        // A .def opens several formats at once, so there's no single relevant tab: stay put.
    }

    /// <summary>
    /// Clears every loaded file back to nothing, after confirming nothing
    /// unsaved would be lost. This is the whole answer to "can I work on a
    /// different character": the editor holds one character's files at a time
    /// rather than several in parallel tabs. Named "Close All" rather than
    /// "Close Character" since it applies just as much to loose files opened
    /// without a .def.
    /// </summary>
    private async void OnCloseAll(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!await ConfirmDiscardEverything()) return;
            ResetAllState();
            StatusText.Text = "Closed. Open a character or a single file to continue.";
        }
        catch (Exception ex)
        {
            await Failed("OnCloseAll", ex);
        }
    }

    /// <summary>
    /// The actual clearing behind "Close All" — pulled out so OpenStageFile
    /// can start from the same clean slate before loading a stage (which, on
    /// its own, would leave a previous character's sounds sitting around: a
    /// stage's .def never mentions sound at all, so nothing would otherwise
    /// overwrite or clear them). No confirmation dialog here; callers that
    /// need one do it themselves first, same as OnCloseAll does below.
    /// </summary>
    private void ResetAllState()
    {
        DiscardRecoveryForDiscardedWork();
        CrashLog.Trace("[file] all files closed");
        CancelPick(restoreSelection: false);
        CancelPaletteLinkPick(restoreSelection: false);
        _paintBaseline = null;
        _paintBaselineSprite = null;
        ClearPaintHistory();
        PaintCommitButton.IsEnabled = PaintResetButton.IsEnabled = false;
        _soundEditBaseline = null;
        _soundEditSound = null;
        if (SoundCommitButton is not null) SoundCommitButton.IsEnabled = false;
        if (SoundResetButton is not null) SoundResetButton.IsEnabled = false;
        _characterName = null;
        _doc.DefPath = null;
        _doc.DefName = null;
        _file = null;
        _path = null;
        _standalonePalettes.Clear();
        _standalonePaletteOverride = -1;
        _editingPalette = null;
        _editingView = null;
        _dirty = false;
        _paletteOverride = -1;
        _current = null;
        _undo.Clear();
        foreach (var palette in _standalonePalettesPendingSff)
            _undo.MarkSaved(StandalonePaletteKindFor(palette));
        SpriteList.ItemsSource = null;
        PaletteBox.ItemsSource = null;
        PaletteList.ItemsSource = null;
        SpriteCanvas.Show(null, null, false);
        SpriteCanvas.ShowOnion(null, null);
        PalettePreview.Show(null, null, false);
        PalettePreview.ShowOnion(null, null);
        ShowSpriteSwatches(null);
        BigSwatches.Show(null);

        _air = null;
        _airPath = null;
        _airEmbeddedInDef = false;
        _airDiskHash = null;
        _airDefSourceActions = [];
        _airDirty = false;
        _action = null;
        Pause();
        ActionList.ItemsSource = null;
        FrameList.ItemsSource = null;
        AnimCanvas.Show(null, null, false);
        AnimCanvas.ShowOnion(null, null);
        // ShowFrame() no-ops once _action is null, so the last frame's boxes
        // and selection would otherwise keep floating over the now-empty
        // canvas — that stray Clsn overlay was the actual "leftover" bug.
        AnimCanvas.Boxes = [];
        AnimCanvas.SelectBox(-1);
        AnimCanvas.InvalidateVisual();
        TimelineView.Show([], -1);
        AirStatusText.Text = "";
        UpdateFrameButtons();

        _snd = null;
        _sndPath = null;
        _sndDirty = false;
        MarkCleanKindsSaved();
        SoundList.ItemsSource = null;
        SndStatusText.Text = "";
        ShowSound();

        UpdateSffInfo();
        UpdateTitle();
        SyncSaveMenu();
    }

    private async void OnSaveAll(object? sender, RoutedEventArgs e)
    {
        try
        {
            await SaveAllInTab();
        }
        catch (Exception ex)
        {
            await Failed("OnSaveAll", ex);
        }
    }

    /// <summary>Saves every unsaved file of the current tab (Save All, and Save when closing a tab or quitting).</summary>
    private async Task SaveAllInTab()
    {
        if (HasPendingColorAdjust())
        {
            var pending = await Dialogs.AskAboutPendingColorAdjust(this);
            if (pending == PendingAdjustChoice.Cancel) return;
            if (pending == PendingAdjustChoice.Commit) CommitPendingColorAdjust();
            else DiscardPendingColorAdjust();
        }
        var saved = new List<string>();
        if (_dirty && _file is not null && (_path is null || !File.Exists(_path))) await AskVersionAndSaveAs();
        else if (_dirty && _file is not null && await SaveSprites()) saved.Add("sprites");
        var linted = LintedActions();
        var encoding = EncodingChangeNote(done: true);
        bool airSaved = false;
        if (_airDirty)
        {
            await SaveAir(askPath: _airPath is null || !File.Exists(_airPath));
            airSaved = !_airDirty;
        }
        if (airSaved) saved.Add("animations");
        if (_sndDirty)
        {
            await SaveSnd(askPath: _sndPath is null || !File.Exists(_sndPath));
            if (!_sndDirty) saved.Add("sounds");
        }
        // Temp palettes are saved or exported deliberately, never by Save All.
        int temps = (_file?.Palettes ?? _standalonePalettes).Count(p => p.Dirty && IsTempPalette(p));
        foreach (var palette in _file?.Palettes.Where(p => p.Source == PaletteSource.External && p.Dirty && !IsTempPalette(p)).ToList() ?? [])
        {
            if (palette.SourcePath is null || !File.Exists(palette.SourcePath))
            {
                await Dialogs.ShowMessage(this, "Palette Not Saved", $"Palette {PaletteLabel(palette)} has no available source file. Use Save As from the Palettes tab.");
                continue;
            }
            if (!await SavePaletteDocument(palette)) continue;
            saved.Add($"palette {PaletteLabel(palette)}");
        }
        foreach (var palette in _standalonePalettes.Where(p => p.Dirty && !IsTempPalette(p)).ToList())
        {
            await SaveStandalonePalette(palette, askPath: palette.SourcePath is null || !File.Exists(palette.SourcePath));
            if (!palette.Dirty) saved.Add(Path.GetFileName(palette.SourcePath) ?? "palette");
        }

        string tempNote = temps == 0 ? ""
            : $" {temps} temp palette{(temps == 1 ? "" : "s")} not saved; save or export {(temps == 1 ? "it" : "them")} to keep {(temps == 1 ? "it" : "them")}.";
        StatusText.Text = (saved.Count == 0 ? "Nothing to save." : $"Saved {string.Join(", ", saved)}.") + tempNote;
        SyncSaveMenu();
        if (airSaved) await ReportClsnRewrite(linted, encoding);
    }

    /// <summary>Grays out the saves that would do nothing.</summary>
    private void SyncSaveMenu()
    {
        if (SaveSpritesItem is { } a) a.IsEnabled = _dirty && _file is not null;
        if (SaveAnimationsItem is { } b) b.IsEnabled = _airDirty;
        if (SaveSoundsItem is { } c) c.IsEnabled = _sndDirty;
    }

    private async Task<bool> ConfirmDiscardEverything(bool resolveStandalone = true)
    {
        if (!await ResolvePendingPaintBeforeSwitch()) return false;
        if (!await ResolvePendingSoundEditsBeforeSwitch()) return false;
        DiscardPendingColorAdjust(); // no dialog — Commit is the only way to keep a slider preview
        if (resolveStandalone && _file is null && _standalonePalettes.Count > 0)
        {
            if (!await ResolveStandalonePalettesBeforeOpen()) return false;
        }
        else if (resolveStandalone && HasDirtyStandalonePalettes())
        {
            var choice = await Dialogs.PromptUnsavedStandalonePalettes(this, _standalonePalettes.Count(p => p.Dirty));
            if (choice == StandalonePaletteOpenChoice.Cancel) return false;
            if (choice == StandalonePaletteOpenChoice.Save)
                foreach (var palette in _standalonePalettes.Where(p => p.Dirty).ToList())
                    await SaveStandalonePalette(palette, askPath: palette.SourcePath is null || !File.Exists(palette.SourcePath));
            if (HasDirtyStandalonePalettes()) return false;
        }
        if (!_dirty && !_airDirty && !_sndDirty && !HasDirtyExternalPalettes() && !HasDirtyStandalonePalettes()) return true;
        var what = new List<string>();
        if (_dirty) what.Add("sprites");
        if (_airDirty) what.Add("animations");
        if (_sndDirty) what.Add("sounds");
        if (HasDirtyExternalPalettes()) what.Add("external palettes");
        if (HasDirtyStandalonePalettes()) what.Add("standalone palettes");
        return await Dialogs.Confirm(this, "Unsaved Changes",
            $"There are unsaved changes to the {string.Join(", ", what)}.", "Discard Them");
    }

    /// <summary>The _Sprites menu's own "Save" entry — always the .sff,
    /// regardless of which tab is showing. Ctrl+S itself is handled
    /// separately in OnKeyDown so it can be tab-aware; see SaveCurrentTab().
    /// </summary>
    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSprites();
        }
        catch (Exception ex)
        {
            await Failed("OnSave", ex);
        }
    }

    /// <summary>Ctrl+S: saves whichever tab is active (sprites, animations,
    /// the selected palette's .act, or sounds), not just sprites. Each save
    /// routine already no-ops safely when there's nothing loaded for it.
    /// </summary>
    private void SaveCurrentTab()
    {
        var args = new RoutedEventArgs();
        switch (Tabs?.SelectedIndex)
        {
            case 1: OnSaveAct(null, args); break;
            case 2: OnSaveAir(null, args); break;
            case 3: OnSaveSnd(null, args); break;
            default: _ = SaveSprites(); break;
        }
    }

    /// <summary>Ctrl+Shift+S: Save As for whichever tab is active (the palette tab's is Export).</summary>
    private async Task SaveAsCurrentTab()
    {
        switch (Tabs?.SelectedIndex)
        {
            case 1:
                if (_file is null && ActivePalette() is { } standalone) await SaveStandalonePalette(standalone, askPath: true);
                else OnExportPalette(null, new RoutedEventArgs());
                break;
            case 2: await SaveAir(askPath: true); break;
            case 3: await SaveSnd(askPath: true); break;
            default: await AskVersionAndSaveAs(); break;
        }
    }

    private async Task<bool> SaveSprites()
    {
        if (_file is null) return false;
        if (_path is null || !File.Exists(_path)) { await AskVersionAndSaveAs(); return false; }

        try
        {
            if (!await ConfirmSffVersionIssues(_file.Version)) return false;
            if (!await ConfirmRecoveredSourceWrite(_path)) return false;
            var file = _file!;
            string path = _path!;
            var version = file.Version;
            bool clamp = !_settings.General.DebugMode;
            int backupCount = _settings.Files.BackupCount;
            var backup = await RunFileWorkAsync($"Saving {Path.GetFileName(path)}…", () =>
            {
                string? made = Backups.Make(path, backupCount);
                file.Save(path, version, clamp: clamp);
                return made;
            });
            ResolveRecoveredItem("sff", _path);
            MarkClean();
            StatusText.Text = $"Saved {Path.GetFileName(_path)}"
                + (backup is null ? "" : $" (previous version kept as {Path.GetFileName(backup)})") + TruncationWarning();
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("SaveSprites", ex);
            StatusText.Text = $"Could not save {Path.GetFileName(_path)}.";
            await Dialogs.ShowMessage(this, "Save Failed", ex.Message);
            return false;
        }
    }

    // Save keeps the file's version; Save As always asks (preselecting the current one).
    private async void OnSaveAs(object? sender, RoutedEventArgs e)
    {
        try
        {
            await AskVersionAndSaveAs();
        }
        catch (Exception ex)
        {
            await Failed("OnSaveAs", ex);
        }
    }

    private async Task AskVersionAndSaveAs()
    {
        if (_file is null) return;
        if (await Dialogs.PromptSffVersion(this, _file.Version, "Save sprites as", "Save as SFF version:", "Next") is { } v)
            await SaveAs(v);
    }

    private async Task SaveAs(SffVersion version)
    {
        if (_file is null) return;

        var embed = await AskConversion(version);
        if (embed is null) return;

        var file = await PickSave(new FilePickerSaveOptions
        {
            Title = $"Save as SFF {version}",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_path ?? "character") + ".sff",
            DefaultExtension = "sff",
            FileTypeChoices = [SffType],
        });
        if (file?.TryGetLocalPath() is not { } path) return;

        // Session changes made for the save; undone in reverse if it fails.
        var rollback = new List<Action>();
        try
        {
            if (!await ConfirmSffVersionIssues(version))
            {
                for (int i = rollback.Count - 1; i >= 0; i--) rollback[i]();
                return;
            }
            Backups.Make(path, _settings.Files.BackupCount);
            bool rekeyed = !SffVersions.IsV1(version) && SffVersions.IsV1(_file.Version);

            // The session becomes the saved file: the .acts embed here with
            // their resolved keys, sprites on the old main palette (sprite
            // 0,0's) move to 1,1, and the old main is dropped when an .act
            // duplicates it.
            var moved = embed.Where(e => e.Index != e.Palette.Index)
                .Select(e => $"{Path.GetFileName(e.Palette.SourcePath)} → 1,{e.Index}").ToList();
            foreach (var (pal, index) in embed)
            {
                var (g, i, src) = (pal.Group, pal.Index, pal.Source);
                rollback.Add(() => (pal.Group, pal.Index, pal.Source) = (g, i, src));
                (pal.Group, pal.Index, pal.Source) = (1, index, PaletteSource.Embedded);
            }
            var main = _file.Sprites.FirstOrDefault(s => s.Group == 0 && s.Index == 0)?.Palette;
            var mainTarget = main is null ? null : embed.FirstOrDefault(e => e.Index == 1).Palette;
            if (mainTarget is not null)
                foreach (var s in _file.Sprites)
                    if (ReferenceEquals(s.Palette, main))
                    {
                        rollback.Add(() => s.Palette = main);
                        s.Palette = mainTarget;
                    }
            // Internal palettes duplicating the main become links to 1,1 — but
            // only when 1,1 really carries the main's colors, so a link never
            // changes what its sprites show.
            bool mainDuplicated = main is not null && mainTarget is not null
                && mainTarget.Colors.SequenceEqual(main.Colors);
            int linked = 0;
            if (mainDuplicated)
                foreach (var p in _file.Palettes)
                    if (!ReferenceEquals(p, main) && p.Source == PaletteSource.Embedded
                        && p.SourcePath is null && p.LinksTo is null
                        && p.Colors.SequenceEqual(main!.Colors))
                    {
                        rollback.Add(() => p.LinksTo = null);
                        p.LinksTo = mainTarget;
                        linked++;
                    }
            // The old main goes when an .act duplicates it; palettes linked to
            // it follow that .act, which has the same colors.
            var mainCopy = main is null || mainTarget is null ? null
                : embed.Select(e => e.Palette).FirstOrDefault(a => a.Colors.SequenceEqual(main.Colors));
            bool mainDropped = mainCopy is not null && !_file.Sprites.Any(s => ReferenceEquals(s.Palette, main));
            if (mainDropped)
            {
                foreach (var p in _file.Palettes)
                    if (ReferenceEquals(p.LinksTo, main))
                    {
                        rollback.Add(() => p.LinksTo = main);
                        p.LinksTo = mainCopy;
                    }
                int at = _file.Palettes.IndexOf(main!);
                rollback.Add(() => _file.Palettes.Insert(at, main!));
                _file.Palettes.RemoveAt(at);
            }
            // Counted after the drop: the palettes the writer actually rekeys.
            int sffPalettes = rekeyed
                ? _file.Palettes.Count(p => p.Source == PaletteSource.Embedded && p.SourcePath is null) : 0;

            bool toV1 = SffVersions.IsV1(version) && !SffVersions.IsV1(_file.Version);
            string? recoveredSource = _path;
            if (!await ConfirmRecoveredSourceWrite(recoveredSource))
            {
                for (int i = rollback.Count - 1; i >= 0; i--) rollback[i]();
                return;
            }
            var fileToSave = _file!;
            bool clampToSave = !_settings.General.DebugMode;
            await RunFileWriteAsync($"Saving {Path.GetFileName(path)}…", () => fileToSave.Save(path, version, clamp: clampToSave));
            rollback.Clear(); // on disk now; the session keeps the changes
            _file.Version = version; // what's on disk now; plain Save keeps it from here
            int droppedV1 = toV1 ? MatchSessionToV1() : 0;
            bool mainTo11 = false;
            if (rekeyed)
            {
                // Same keys and order on the session side, so a later plain
                // Save matches; the .acts stay embedded (they have a source file).
                foreach (var (p, key) in SffWriter.V1ConvertedKeys(_file))
                {
                    (p.Group, p.Index) = key;
                    mainTo11 |= key == (1, 1);
                }
                _file.Palettes = _file.Palettes.OrderBy(p => p.Group).ThenBy(p => p.Index).ToList();
            }
            if (rekeyed || toV1) // palette labels switch between keys and S numbers
            {
                RebuildPaletteList();
                RebuildSpriteTable(); // the sprite table shows palette keys
            }
            _path = path;
            if (recoveredSource is not null) ResolveRecoveredItem("sff", recoveredSource);
            MarkClean();
            _settings.AddRecent(path);
            _settings.Save();
            RebuildRecentMenu();
            var size = new FileInfo(path).Length;
            UpdateSffInfo();
            var parts = new List<string>();
            if (sffPalettes > 0) parts.Add($"{sffPalettes} SFF palette{(sffPalettes == 1 ? "" : "s")} rekeyed to their sprites' groups"
                + (mainTo11 ? "; sprite 0,0's palette is 1,1" : ""));
            if (embed.Count > 0)
                parts.Add($"Embedded {embed.Count} external .act palette{(embed.Count == 1 ? "" : "s")}"
                    + (moved.Count > 0 ? $" (renumbered to avoid conflicts: {string.Join(", ", moved)})" : ""));
            if (mainTarget is not null)
                parts.Add("Sprites that used the main palette now use 1,1"
                    + (linked > 0 ? $"; {linked} duplicate palette{(linked == 1 ? "" : "s")} linked to it" : ""));
            if (mainDropped) parts.Add("The old main palette was dropped as a duplicate");
            if (droppedV1 > 0) parts.Add($"{droppedV1} palette{(droppedV1 == 1 ? "" : "s")} no sprite used {(droppedV1 == 1 ? "was" : "were")} dropped");
            StatusText.Text = $"Saved {Path.GetFileName(path)} as SFF {version}, {size / 1024:N0} KB."
                + string.Concat(parts.Select(x => $" {x}.")) + TruncationWarning();
        }
        catch (Exception ex)
        {
            for (int i = rollback.Count - 1; i >= 0; i--) rollback[i]();
            CrashLog.Write("SaveAs", ex);
            await Dialogs.ShowMessage(this, $"Could Not Save as SFF {version}", ex.Message);
        }
    }

    /// <summary>
    /// Save As across SFF versions: a popup says what the conversion changes
    /// and, v1 → v2, offers to embed the external .act palettes the save would
    /// otherwise drop. Null = cancel the save; empty = save without them;
    /// otherwise the palettes with their resolved group-1 indexes.
    /// </summary>
    private async Task<List<(Palette Palette, int Index)>?> AskConversion(SffVersion version)
    {
        if (_file is null) return [];
        bool fromV1 = SffVersions.IsV1(_file.Version), toV1 = SffVersions.IsV1(version);
        if (fromV1 == toV1) return [];

        if (toV1)
        {
            if (!await CanWriteV1()) return null;
            var notes = new List<string> { $"Saving converts this file to SFF {version}; the session becomes the saved file." };
            notes.AddRange(V1ConversionLosses());
            return await Dialogs.PromptSffConversion(this, "Convert to SFFv1", notes, 0, false) is null ? null : [];
        }

        var embeddable = SffWriter.EmbeddableExternals(_file);
        // Embedding repoints the main palette's sprites when an .act lands on 1,1.
        bool repoints = embeddable.Any(e => e.Index == 1)
            && _file.Sprites.Any(s => s.Group == 0 && s.Index == 0 && s.Palette is not null);
        var embed = await Dialogs.PromptSffConversion(this, "Convert to SFFv2",
            [$"Saving converts this file to SFF {version}: the session becomes the saved file, and the SFF's own palettes take their sprites' groups, and sprite 0,0's takes 1,1 unless an .act does."],
            embeddable.Count, repoints);
        if (embed is null) return null;
        return embed == true ? embeddable : [];
    }

    /// <summary>
    /// SffV1Writer refuses true-color and undecoded sprites; says so before any
    /// file picker. False = can't write v1.
    /// </summary>
    private async Task<bool> CanWriteV1()
    {
        if (_file is null) return false;
        var bad = _file.Sprites.Where(s => s.SharesWith is null && s.Indexed is null)
            .Select(s => $"{s.Group},{s.Index}").ToList();
        if (bad.Count == 0) return true;
        CrashLog.Error("CanWriteV1", $"SFFv1 save refused: {bad.Count} true-color or undecoded sprite(s)");
        await Dialogs.ShowMessage(this, "Can't Save as SFFv1",
            $"SFFv1 holds 8-bit sprites only; {bad.Count} sprite{(bad.Count == 1 ? " is" : "s are")} true color or undecoded: "
            + string.Join(", ", bad.Take(5)) + (bad.Count > 5 ? ", …" : "") + ".");
        return false;
    }

    /// <summary>What writing this v2 file as v1 loses, for the warning popups.</summary>
    private List<string> V1ConversionLosses()
    {
        var notes = new List<string>();
        if (_file is null) return notes;
        var used = _file.Sprites.Select(s => s.Palette).ToHashSet();
        int dropped = _file.Palettes.Count(p => p.Source == PaletteSource.Embedded && !used.Contains(p));
        if (dropped > 0)
            notes.Add($"{dropped} palette{(dropped == 1 ? "" : "s")} no sprite uses will be dropped: SFFv1 stores palettes only inside sprites.");
        var lost = SffWriter.V1LinksLosingPalette(_file);
        if (lost.Count > 0)
            notes.Add($"{lost.Count} linked sprite{(lost.Count == 1 ? "" : "s")} will show the linked sprite's palette instead of "
                + $"{(lost.Count == 1 ? "its" : "their")} own: an SFFv1 link can only use that one or the previous sprite's ("
                + string.Join(", ", lost.Take(5)) + (lost.Count > 5 ? ", …" : "") + ").");
        notes.Add("Palette group/index keys, links and alpha are not stored in SFFv1.");
        return notes;
    }

    /// <summary>
    /// Sprites > Save Like SprMaker: pick SprMaker 0.97 (v1) or SprMaker 2.01 (v2), then
    /// that tool's options, and write a copy built the way the tool would. The
    /// open file is not changed; saving over it offers to reload it.
    /// </summary>
    private async void OnSaveLikeSprMaker(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_file is null) return;
            if (!await ResolvePendingPaintBeforeSwitch()) return;
            var file = _file;
            if (await Dialogs.PromptSprMakerTool(this, _settings.Files.SprMakerUseV2) is not { } v2) return;
            _settings.Files.SprMakerUseV2 = v2;
            _settings.Save();

            bool fromV1 = SffVersions.IsV1(file.Version);
            var notes = new List<string> { "Options apply to the saved file only; the open file is not changed." };
            if (v2 && fromV1)
                notes.Add("This SFFv1 file is converted: its own palettes take their sprites' groups (sprite 0,0's takes 1,1), and external .act palettes no sprite uses are not saved.");
            if (!v2 && !fromV1)
            {
                if (!await CanWriteV1()) return;
                notes.Add("This SFFv2 file is converted to SFFv1.");
                notes.AddRange(V1ConversionLosses());
            }

            SprMakerOptions? o1 = null;
            SprMaker2Options? o2 = null;
            if (v2)
            {
                if (await Dialogs.PromptSprMaker2Options(this, (_settings.Files.SprMaker2 ?? new()).Copy(), notes) is not { } o) return;
                _settings.Files.SprMaker2 = o2 = o;
            }
            else
            {
                if (await Dialogs.PromptSprMakerOptions(this, (_settings.Files.SprMaker ?? new()).Copy(), notes) is not { } o) return;
                _settings.Files.SprMaker = o1 = o;
            }
            _settings.Save();
            if (!ReferenceEquals(_file, file)) return; // file changed while the dialogs were open

            var picked = await PickSave(new FilePickerSaveOptions
            {
                Title = v2 ? "Save like SprMaker 2.01" : "Save like SprMaker 0.97",
                SuggestedFileName = Path.GetFileNameWithoutExtension(_path ?? "character") + ".sff",
                DefaultExtension = "sff",
                FileTypeChoices = [SffType],
            });
            if (picked?.TryGetLocalPath() is not { } path) return;
            if (!ReferenceEquals(_file, file)) return;

            SprMakerReport report;
            try
            {
                bool quiet = o2 is { QuietMode: true } || o1 is { QuietMode: true };
                bool verboseReport = o2 is { VerboseMode: true } || o1 is { SummaryMode: false };
                bool veryVerboseReport = o2 is { VeryVerboseMode: true };
                bool clampSave = !_settings.General.DebugMode;
                int backupCount = _settings.Files.BackupCount;
                report = await RunFileWorkAsync($"Saving {Path.GetFileName(path)} like {(v2 ? "SprMaker 2.01" : "SprMaker 0.97")}…", () =>
                {
                    Backups.Make(path, backupCount);
                    var written = o2 is not null
                        ? SprMakerSave.Save(file, path, o2, clamp: clampSave)
                        : SprMakerSave.Save(file, path, o1!, clamp: clampSave);
                    if (!quiet)
                        File.WriteAllText(Path.ChangeExtension(path, ".txt"),
                            written.ToText(o2 is not null, verboseReport, veryVerboseReport) + Environment.NewLine);
                    return written;
                });
            }
            catch (Exception ex)
            {
                CrashLog.Write("SaveLikeSprMaker", ex);
                StatusText.Text = $"Could not save {Path.GetFileName(path)}.";
                await Dialogs.ShowMessage(this, $"Could Not Save Like {(v2 ? "SprMaker 2.01" : "SprMaker 0.97")}", ex.Message);
                return;
            }

            var size = new FileInfo(path).Length;
            var parts = new List<string>();
            if (report.Linked > 0) parts.Add($"{report.Linked} duplicate sprite{(report.Linked == 1 ? "" : "s")} linked");
            if (report.Palettes > 0)
                parts.Add($"{report.Palettes} duplicate palette{(report.Palettes == 1 ? "" : "s")} {(report.PalettesDiscarded > 0 ? "discarded" : "linked")}");
            if (report.Cropped > 0)
                parts.Add($"{report.Cropped} sprite{(report.Cropped == 1 ? "" : "s")} cropped"
                    + (report.AxesFollowed > 0 ? $" ({report.AxesFollowed} linked sprite{(report.AxesFollowed == 1 ? "" : "s")} followed)" : ""));
            StatusText.Text = $"Saved {Path.GetFileName(path)} like {(v2 ? "SprMaker 2.01" : "SprMaker 0.97")}, {size / 1024:N0} KB."
                + string.Concat(parts.Select(x => $" {x}.")) + TruncationWarning();

            var pathCase = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (_path is not null && Path.GetFullPath(path).Equals(Path.GetFullPath(_path), pathCase)
                && await Dialogs.Confirm(this, "Reload Sprites",
                    "The open sprites no longer match the file you saved over. Reload them from the file? Unsaved changes and undo history go.",
                    "Reload"))
                ReloadSavedSff(path);
        }
        catch (Exception ex)
        {
            await Failed("OnSaveLikeSprMaker", ex);
        }
    }

    /// <summary>
    /// Reopens the sprite file after a Save Like SprMaker over it. External
    /// palettes no sprite used were not saved, so they are carried over.
    /// </summary>
    private void ReloadSavedSff(string path)
    {
        if (_file is null) return;
        var used = _file.Sprites.Select(s => s.Palette).ToHashSet();
        var externals = _file.Palettes.Where(p => p.Source == PaletteSource.External && !used.Contains(p)).ToList();
        SffFile reloaded;
        try { reloaded = SffFile.Open(path); }
        catch (Exception ex)
        {
            CrashLog.Write("ReloadSavedSff", ex);
            ShowPopup("Could Not Reload File", $"Could not reload {Path.GetFileName(path)}: {ex.Message}");
            return;
        }
        reloaded.Palettes.AddRange(externals);
        _file = reloaded;
        _path = path;
        ApplyLoadedSff();
    }

    /// <summary>
    /// After a v2 → v1 save the session matches the file: palettes no sprite
    /// uses are dropped and links are resolved into plain copies.
    /// </summary>
    private int MatchSessionToV1()
    {
        if (_file is null) return 0;
        foreach (var p in _file.Palettes)
            if (p.LinksTo is not null)
            {
                var colors = p.Colors.ToArray();
                p.LinksTo = null;
                p.Colors = colors;
            }
        var used = _file.Sprites.Select(s => s.Palette).ToHashSet();
        return _file.Palettes.RemoveAll(p => p.Source == PaletteSource.Embedded && !used.Contains(p));
    }

    /// <summary>
    /// SFFv2 only: below 2.0.1.0, sprites still stored as PNG are re-encoded
    /// (lossless) in the saved file, after asking. False = cancel.
    /// </summary>
    private async Task<bool> ConfirmSffVersionIssues(SffVersion version)
    {
        if (_file is null || SffVersions.IsV1(version)) return true;

        int png = SffVersions.SupportsPng(version) ? 0 : SffWriter.StoredAsPng(_file);
        return png == 0 || await Dialogs.Confirm(this, "PNG Sprites",
            $"{png} sprite(s) are stored as PNG, which SFF {version} can't hold. "
            + "Re-encode them as LZ5/RLE8 in the saved file? No pixels change.", "Re-encode");
    }

    /// <summary>
    /// Values past the SFF's on-disk fields (u16 group/number, s16 axis) can
    /// only come from Debug mode. Saving with Debug on wraps them (the writers'
    /// casts); with Debug off it clamps them to the field's range. Either way
    /// the save message says so rather than letting the file lie.
    /// </summary>
    private string TruncationWarning()
    {
        if (_file is null) return "";
        int over = _file.Sprites.Count(s => s.Group is < 0 or > 65535 || s.Index is < 0 or > 65535)
                 + _file.Palettes.Count(p => p.Group is < 0 or > 65535 || p.Index is < 0 or > 65535);
        int axisOver = _file.Sprites.Count(s =>
            s.AxisX is < short.MinValue or > short.MaxValue || s.AxisY is < short.MinValue or > short.MaxValue);
        var parts = new List<string>();
        if (over > 0) parts.Add($"{over} group/number value(s) outside 0..65535");
        if (axisOver > 0) parts.Add($"{axisOver} axis value(s) outside -32768..32767");
        return parts.Count == 0 ? "" : $"   |   WARNING: {string.Join(" and ", parts)} did not fit the file and were "
            + (_settings.General.DebugMode ? "wrapped (Debug mode)" : "clamped to fit");
    }

    private async void OnFileDropped(object? sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath() is not { } p) return;
            if (IsSessionPath(p)) { if (!_projectOpening) await LoadSessionFile(p); return; }
            if (IsSndPath(p)) { await OpenSndFromPath(p); return; }
            bool opensSff = Path.GetExtension(p).Equals(".sff", StringComparison.OrdinalIgnoreCase);
            if (!await ConfirmDiscard(resolveStandalone: !opensSff)) return;
            if (opensSff && !await ConfirmStandalonePalettesForSff()) return;
            await OpenFileInBackground(p);
        }
        catch (Exception ex)
        {
            await Failed("OnFileDropped", ex);
        }
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    // Open and save pickers each remember their own last folder (persisted in Settings).
    internal async Task<IStorageFolder?> StartFolder(bool save = false)
    {
        string? d = save ? _settings.Files.LastSaveDirectory ?? _settings.Files.LastDirectory : _settings.Files.LastDirectory;
        return d is not null && Directory.Exists(d) ? await StorageProvider.TryGetFolderFromPathAsync(d) : null;
    }

    private void RememberFolder(string? dir, bool save)
    {
        if (string.IsNullOrEmpty(dir)) return;
        if (save) _settings.Files.LastSaveDirectory = dir; else _settings.Files.LastDirectory = dir;
        _settings.Save();
    }

    /// <summary>Open picker; owner parents it to another window (e.g. a dialog) instead of this one.</summary>
    internal async Task<IReadOnlyList<IStorageFile>> PickOpen(FilePickerOpenOptions o, TopLevel? owner = null)
    {
        o.SuggestedStartLocation ??= await StartFolder();
        CrashLog.Trace($"[picker] open: {o.Title}");
        var files = await (owner ?? this).StorageProvider.OpenFilePickerAsync(o);
        CrashLog.Trace($"[picker] closed: {(files.Count > 0 ? files[0].TryGetLocalPath() : "canceled")}");
        if (files.Count > 0) RememberFolder(Path.GetDirectoryName(files[0].TryGetLocalPath()), save: false);
        return files;
    }

    internal async Task<IStorageFile?> PickSave(FilePickerSaveOptions o)
    {
        o.SuggestedStartLocation = await StartFolder(save: true);
        CrashLog.Trace($"[picker] save: {o.Title}");
        var file = await StorageProvider.SaveFilePickerAsync(o);
        CrashLog.Trace($"[picker] closed: {file?.TryGetLocalPath() ?? "canceled"}");
        if (file is not null) RememberFolder(Path.GetDirectoryName(file.TryGetLocalPath()), save: true);
        return file;
    }

    // ---- recent files -----------------------------------------------------

    /// <summary>
    /// Delete while Open Recent is showing removes the pointer-hovered entry, or
    /// the arrow-highlighted one when nothing is hovered. Only the list entry goes.
    /// </summary>
    private bool TryRemoveRecent()
    {
        if (!RecentMenu.IsSubMenuOpen || RecentMenu.ItemsSource is not List<MenuItem> rows) return false;
        int index = rows.FindIndex(r => r.IsPointerOver);
        if (index < 0) index = rows.FindIndex(r => r.IsFocused || r.IsKeyboardFocusWithin);
        if (index < 0 || rows[index].Tag is not string path) return false;

        _settings.RemoveRecent(path);
        _settings.Save();
        CrashLog.Trace($"[recent] removed from list: {path}");
        RebuildRecentMenu();
        StatusText.Text = $"Removed {Path.GetFileName(path)} from recent files";

        // Keep the menu open and move the highlight to the entry that took its place.
        if (RecentMenu.ItemsSource is List<MenuItem> left && left.Count > 0)
        {
            var next = left[Math.Min(index, left.Count - 1)];
            Dispatcher.UIThread.Post(() => next.Focus());
        }
        return true;
    }

    private void RebuildRecentMenu()
    {
        var items = new List<MenuItem>();
        foreach (var path in _settings.Files.RecentFiles)
        {
            // "__" shows a literal underscore; a single one would be taken as the access key.
            var item = new MenuItem { Header = path.Replace("_", "__"), Tag = path };
            item.Click += async (_, _) =>
            {
                if (!File.Exists(path))
                {
                    _settings.Files.RecentFiles.Remove(path);
                    _settings.Save();
                    RebuildRecentMenu();
                    MainMenu.Close();
                    CrashLog.Error("OpenRecent", $"file not found: {path}");
                    StatusText.Text = $"{path} is no longer there";
                    ShowPopup("File Not Found", $"{path} is no longer there, so it was removed from Open Recent.");
                    return;
                }

                if (IsSessionPath(path))
                {
                    MainMenu.Close();
                    if (!_projectOpening) await LoadSessionFile(path);
                    return;
                }
                MainMenu.Close(); // before any prompt, so the menu doesn't sit over it
                if (Path.GetExtension(path).Equals(".def", StringComparison.OrdinalIgnoreCase))
                {
                    await OpenDefWithTabPrompt(path);
                    return;
                }
                bool opensSff = Path.GetExtension(path).Equals(".sff", StringComparison.OrdinalIgnoreCase);
                if (!await ConfirmDiscard(resolveStandalone: !opensSff)) return;
                if (opensSff && !await ConfirmStandalonePalettesForSff()) return;
                await OpenFileInBackground(path);
                MainMenu.Close();
            };
            items.Add(item);
        }
        RecentMenu.ItemsSource = items;
        RecentMenu.IsEnabled = items.Count > 0;
    }

    // ---- unsaved changes --------------------------------------------------

    private void MarkDirty()
    {
        if (!_undo.Tag(SffKind)) _undo.Untracked(SffKind);
        if (_dirty) return;
        _dirty = true;
        UpdateTitle();
        SyncSaveMenu();
    }

    private void MarkClean()
    {
        _dirty = false;
        _undo.MarkSaved(SffKind);
        UpdateTitle();
        UpdateStatus();
        SyncSaveMenu();
    }

    private const string SffKind = "sff", AirKind = "air", SndKind = "snd";

    /// <summary>After Undo.Clear: the kinds with no unsaved changes are at their save point.</summary>
    private void MarkCleanKindsSaved()
    {
        if (!_dirty) _undo.MarkSaved(SffKind);
        if (!_airDirty) _undo.MarkSaved(AirKind);
        if (!_sndDirty) _undo.MarkSaved(SndKind);
    }

    /// <summary>After undo/redo: a file back at its save point is unchanged again; pending edits keep it changed.</summary>
    private void SyncDirtyFromUndo()
    {
        _dirty = _file is not null && (_undo.Differs(SffKind) || (_paintBaseline is not null && PaintSessionHasChanges()));
        _airDirty = _air is not null && _undo.Differs(AirKind);
        _sndDirty = _snd is not null && (_undo.Differs(SndKind) || _soundEditBaseline is not null);
        foreach (var palette in _standalonePalettes)
            palette.Dirty = _undo.Differs(StandalonePaletteKindFor(palette));
        UpdateTitle();
        UpdateAirInfo();
        UpdateSndInfo();
        SyncSaveMenu();
    }


    private void UpdateTitle()
    {
        SyncTabDimming();
        SyncTabHeaders();

        bool anyDirty = _dirty || _airDirty || _sndDirty || HasDirtyExternalPalettes() || HasDirtyStandalonePalettes();
        string? name = _characterName
            ?? (_path is not null ? Path.GetFileName(_path)
                : _airPath is not null ? Path.GetFileName(_airPath)
                : _sndPath is not null ? Path.GetFileName(_sndPath)
                : null);
        Title = (anyDirty ? "* " : "") + (name is null ? "Ikemen Media Editor" : $"{name} - Ikemen Media Editor");
        RebuildProjectTabStrip();
    }

    /// <summary>
    /// The limits live in settings.json rather than a dialog on purpose —
    /// they're tuning knobs, not choices to make per session. Seeding the
    /// file first means the schema is visible even on a fresh install.
    /// </summary>
    private void OnOpenSettingsFile(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(Settings.FilePath)) _settings.Save();
            Process.Start(new ProcessStartInfo(Settings.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CrashLog.Write("OnOpenSettingsFile", ex);
            StatusText.Text = $"Could not open the settings file: {ex.Message}";
        }
    }

    private void OnOpenCrashLogsFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CrashLog.LogDir);
            Process.Start(new ProcessStartInfo(CrashLog.LogDir) { UseShellExecute = true });
            StatusText.Text = $"Crash logs: {CrashLog.LogDir}";
        }
        catch (Exception ex)
        {
            CrashLog.Write("OnOpenCrashLogsFolder", ex);
            StatusText.Text = $"Could not open the crash logs folder: {ex.Message}";
        }
    }

    /// <summary>
    /// A tab's dim state just mirrors whether the file that feeds it is
    /// loaded — dimmed tabs stay selectable, so you can sit on Sounds and
    /// open a .snd from the menu without a detour. Runs from UpdateTitle,
    /// which every open/close flow already calls (the .air/.snd ones via
    /// UpdateAirInfo/UpdateSndInfo, before their SwitchToTab).
    /// </summary>
    private void SyncTabDimming()
    {
        SetTabDimmed(SpritesTab, _file is null);
        SetTabDimmed(PalettesTab, _file is null && _standalonePalettes.Count == 0);
        SetTabDimmed(AnimationsTab, _air is null);
        SetTabDimmed(SoundsTab, _snd is null);
    }

    private static void SetTabDimmed(TabItem? tab, bool empty)
    {
        if (tab is null) return;
        if (empty) tab.Classes.Add("empty");
        else tab.Classes.Remove("empty");
    }

    /// <summary>
    /// A tab's header carries a '*' while the file that feeds it has unsaved
    /// changes — Sprites and Palettes share the .sff's state, Animations the
    /// .air's, Sounds the .snd's. Runs from UpdateTitle, so every dirty/clean
    /// transition already reaches it.
    /// </summary>
    private void SyncTabHeaders()
    {
        SetTabHeader(SpritesTab, "Sprites", _dirty);
        SetTabHeader(AnimationsTab, "Animations", _airDirty);
        SetTabHeader(PalettesTab, "Palettes", _dirty || HasDirtyExternalPalettes() || HasDirtyStandalonePalettes());
        SetTabHeader(SoundsTab, "Sounds", _sndDirty);
    }

    private static void SetTabHeader(TabItem? tab, string name, bool dirty)
    {
        if (tab is null) return;
        tab.Header = dirty ? name + "*" : name;
    }

    /// <summary>Returns false when the user decided not to lose their changes.</summary>
    private async Task<bool> ConfirmDiscard(bool resolveStandalone = true)
    {
        if (!await ResolvePendingPaintBeforeSwitch()) return false;
        DiscardPendingColorAdjust(); // no dialog — Commit is the only way to keep a slider preview
        if (resolveStandalone && _file is null && _standalonePalettes.Count > 0
            && !await ResolveStandalonePalettesBeforeOpen()) return false;
        if (!_dirty || _file is null) return true;
        var choice = await Dialogs.AskToSave(this, Path.GetFileName(_path ?? "This file"));
        switch (choice)
        {
            case SaveChoice.Save:
                await SaveSprites(); // finishes the save (or Save As) before checking
                return !_dirty;
            case SaveChoice.Discard: return true;
            default: return false;
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        try
        {
            // A load, save or multi-selection action is running; closing now could cut a write short.
            if (_busy)
            {
                e.Cancel = true;
                base.OnClosing(e);
                ShowPopup("Still Working", "Wait for the current action to finish, then close the window.");
                return;
            }

            _settings.Layout.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _settings.Layout.WindowWidth = Width;
                _settings.Layout.WindowHeight = Height;
            }
            SavePanelSizes();
            _settings.Save();

            if (_forceClose || !_docs.Any(DocumentHasUnsavedWork))
            {
                _recoveryTimer?.Stop();
                if (!_preserveRecoveryPackage && !RecoveryTestMode && !HasDirtyExternalPalettes())
                    RecoveryStore.DiscardActive();
                RecoveryStore.MarkCleanShutdown();
                base.OnClosing(e);
                // Owned windows close with this one, but a hidden one has no owner; don't re-dock into a closing window.
                CloseFloatingPanels();
                return;
            }

            // The dialog is async, so cancel this attempt and close again once answered.
            e.Cancel = true;
            base.OnClosing(e);
            if (await ConfirmDiscardAllTabs())
            {
                _recoveryTimer?.Stop();
                foreach (var doc in _docs) DiscardRecoveryForTab(doc); // every tab's work was just discarded
                RecoveryStore.MarkCleanShutdown();
                _forceClose = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            await Failed("OnClosing", ex);
        }
    }
}
