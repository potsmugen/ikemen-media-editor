using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Air;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Def;
using Sff;

namespace Editor;

/// <summary>
/// The Animations tab: reads an .air, plays it back against the sprites from the
/// open .sff, edits collision boxes, and manages frames and actions.
/// </summary>
public partial class MainWindow
{
    private static readonly FilePickerFileType AirType =
        new("Animation files") { Patterns = ["*.air"] };
    private static readonly FilePickerFileType DefType =
        new("Definition files") { Patterns = ["*.def"] };
    private static readonly FilePickerFileType AnimationSourceType =
        new("Animation or definition files") { Patterns = ["*.air", "*.def"] };

    /// <summary>MUGEN runs at 60 ticks a second at normal speed.</summary>
    private static readonly TimeSpan TickLength = TimeSpan.FromSeconds(1.0 / 60);

    /// <summary>Settings.InfiniteAnimationTimeout in ticks.</summary>
    /// <summary>Playback polls the clock at about half a tick; late polls catch up instead of slowing playback.</summary>
    private static readonly TimeSpan PlaybackPollInterval = TimeSpan.FromMilliseconds(8);

    /// <summary>Most ticks one poll catches up; a longer stall skips the rest instead of fast-forwarding.</summary>
    private const int MaxCatchUpTicks = 30;

    private int InfiniteAnimationTimeoutTicks => Math.Max(1, (int)Math.Round(_settings.InfiniteAnimationTimeout / TickLength.TotalSeconds));

    // The convention this project follows: red for a frame's own Clsn1, blue
    // for its own Clsn2. Default-sourced boxes get their own color per kind
    // now (orange for Clsn1Default, teal for Clsn2Default) rather than sharing
    // one shade — the selected-box info line and the Make Default button's
    // label still say Clsn1 or Clsn2 when it matters.
    private static readonly Color AttackColor = Color.FromRgb(0xd8, 0x30, 0x30);
    private static readonly Color HurtColor = Color.FromRgb(0x20, 0x60, 0xf0);
    private static readonly Color Clsn1DefaultColor = Color.FromRgb(0xe0, 0x90, 0x20);
    private static readonly Color Clsn2DefaultColor = Color.FromRgb(0x00, 0x8a, 0x8a);

    private AirFile? _air;
    private string? _airPath;
    private bool _airEmbeddedInDef;
    /// <summary>Whether missing action names fall back to reserved-action suggestions. True for a
    /// standalone .air or a character definition, false for stages and other .defs (their embedded
    /// background actions aren't gameplay states the user would name).</summary>
    private bool _airShowsSuggestedNames;
    private byte[]? _airDiskHash;
    private List<AnimationAction> _airDefSourceActions = [];
    private AnimationAction? _action;
    private bool _airDirty;
    private DispatcherTimer? _clock;
    private int _tick;
    /// <summary>Playback time base: ticks due are counted from real elapsed time, not timer events.</summary>
    private readonly Stopwatch _playWatch = new();
    private long _playedTicks;
    /// <summary>Sprite lookup built on first use while playing, when sprites can't change.</summary>
    private Dictionary<(int, int), Sprite>? _playSprites;
    private SffFile? _playSpritesFile;
    private string? _airError;

    /// <summary>The action's boxes captured before a box drag/nudge.</summary>
    private ActionBoxState? _boxesBeforeDrag;
    private readonly Dictionary<ClsnKind, DefaultSegment> _dragDefaultSegments = [];

    private AnimationElement? CurrentElement =>
        _action is not null && FrameList.SelectedIndex >= 0 && FrameList.SelectedIndex < _action.Elements.Count
            ? _action.Elements[FrameList.SelectedIndex]
            : null;

    // ---- loading and saving -----------------------------------------------

    /// <summary>Starts a blank .air (no actions) in place of whatever's open — the
    /// "start from scratch" counterpart to Open. _airPath stays null, same as
    /// LoadAirFromEmbeddedText's other caller, so Save prompts for a location.</summary>
    private async void OnNewAir(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_airDirty && !await Dialogs.Confirm(this, "Unsaved animation changes",
                    "The animation file has unsaved changes.", "Discard them"))
                return;

            LoadAirFromEmbeddedText(new AirFile(), showSuggestedNames: true);
            StatusText.Text = "New animation file. Add actions, then Save.";
            SwitchToTab(2);
        }
        catch (Exception ex)
        {
            await Failed("OnNewAir", ex);
        }
    }

    private async void OnOpenAir(object? sender, RoutedEventArgs e)
    {
        try
        {
            CrashLog.Trace("OnOpenAir: start");
            if (_airDirty)
            {
                CrashLog.Trace("OnOpenAir: awaiting unsaved-changes confirm");
                if (!await Dialogs.Confirm(this, "Unsaved animation changes",
                        "The animation file has unsaved changes.", "Discard them"))
                {
                    CrashLog.Trace("OnOpenAir: canceled at unsaved-changes confirm");
                    return;
                }
            }

            CrashLog.Trace("OnOpenAir: awaiting file picker");
            var files = await PickOpen(new FilePickerOpenOptions
            {
                Title = "Open animation file",
                AllowMultiple = false,
                FileTypeFilter = [AnimationSourceType],
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            {
                CrashLog.Trace("OnOpenAir: no file chosen");
                return;
            }

            CrashLog.Trace($"OnOpenAir: LoadAir starting for {path}");
            if (!LoadAnimationSource(path))
            {
                CrashLog.Trace("OnOpenAir: LoadAir failed");
                await Dialogs.ShowMessage(this, "Could not read the animation file", _airError ?? "unknown problem");
                return;
            }
            CrashLog.Trace("OnOpenAir: LoadAir finished ok");

            StatusText.Text = _file is null
                ? "Animations loaded. Open the character's sprites too, so the frames have something to show."
                : $"Loaded {_air!.Actions.Count} actions from {Path.GetFileName(path)}";
            SwitchToTab(2);
            CrashLog.Trace("OnOpenAir: done");
        }
        catch (Exception ex)
        {
            // async void: an exception here has nowhere else to go — log it
            // explicitly rather than relying on it reaching AppDomain intact.
            CrashLog.Write("OnOpenAir", ex);
            await Dialogs.ShowMessage(this, "Something went wrong opening the animation file", ex.Message);
        }
    }

    /// <summary>Loads a standalone .air or a definition containing embedded [Begin Action] blocks.</summary>
    private bool LoadAnimationSource(string path)
    {
        if (!Path.GetExtension(path).Equals(".def", StringComparison.OrdinalIgnoreCase))
            return LoadAir(path);

        try
        {
            var bytes = File.ReadAllBytes(path);
            var (text, encoding, legacyName) = AirFile.Decode(bytes);
            var air = AirParser.Parse(text);
            if (air.Actions.Count == 0)
                throw new InvalidDataException("The definition contains no [Begin Action] blocks.");
            air.TextEncoding = encoding;
            air.LegacyEncodingName = legacyName;
            // A .def opened here is a character (has [Files] sprite=) or a stage/other; only a
            // character's embedded actions get suggested names.
            var def = DefFile.Parse(text, Path.GetDirectoryName(Path.GetFullPath(path)) ?? "");
            LoadAirFromEmbeddedText(air, path, bytes, showSuggestedNames: def.SpritePath is not null);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("LoadAnimationSource", ex);
            _airError = ex.Message;
            return false;
        }
    }

    /// <summary>Loads an .air and fills the action list. Returns false on failure.</summary>
    internal bool LoadAir(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var (text, encoding, legacyName) = AirFile.Decode(bytes);
            _air = AirParser.Parse(text);
            _air.TextEncoding = encoding;
            _air.LegacyEncodingName = legacyName;
            _airPath = path;
            _airEmbeddedInDef = false;
            _airShowsSuggestedNames = true;
            _airDiskHash = SHA256.HashData(bytes);
            _airDefSourceActions = [];
            _airDirty = false;
            _undo.MarkSaved(AirKind);
            _airError = null;
        }
        catch (Exception ex)
        {
            CrashLog.Write("LoadAir", ex);
            _airError = ex.Message;
            return false;
        }
        ApplyLoadedAir();
        return true;
    }

    /// <summary>Fills the action list from a definition file or a newly created animation.</summary>
    internal void LoadAirFromEmbeddedText(AirFile air, string? defPath = null, byte[]? defBytes = null, bool showSuggestedNames = false)
    {
        _air = air;
        _airPath = defPath;
        _airEmbeddedInDef = defPath is not null;
        _airShowsSuggestedNames = showSuggestedNames;
        _airDiskHash = defBytes is not null ? SHA256.HashData(defBytes)
            : defPath is not null ? SHA256.HashData(File.ReadAllBytes(defPath)) : null;
        _airDefSourceActions = defPath is not null ? [.. air.Actions] : [];
        _airDirty = false;
        _undo.MarkSaved(AirKind);
        _airError = null;
        ApplyLoadedAir();
    }

    private void ApplyLoadedAir()
    {
        CrashLog.Trace("LoadAir: parsed, resetting state");
        Pause(); // stop any playback of the action being replaced before tearing it down
        _action = null;
        _tick = 0;
        AnimCanvas.Show(null, null, false);
        AnimCanvas.Boxes = new List<BoxOverlay>();
        AnimCanvas.GhostBoxes = new List<BoxOverlay>();
        AnimCanvas.ShowOnion(null, null);
        FrameList.ItemsSource = null;
        TimelineView?.Show([], -1);

        CrashLog.Trace("LoadAir: rebuilding action list");
        _suppressEvents = true;
        if (ActionFilterBox is not null) ActionFilterBox.Text = "";
        _suppressEvents = false;
        RebuildActionList();
        // Force the selection event to fire even if index 0 was already selected,
        // so a second .air actually replaces what's on screen.
        _suppressEvents = true;
        ActionList.SelectedIndex = -1;
        _suppressEvents = false;
        CrashLog.Trace($"LoadAir: selecting first action ({_air!.Actions.Count} total)");
        if (_air.Actions.Count > 0) ActionList.SelectedIndex = 0;
        UpdateAirInfo();
        CrashLog.Trace($"[air] encoding: {_air.TextEncoding} {_air.LegacyEncodingName}");
        foreach (var a in _air.Actions.Where(a => a.ChangesOnSave))
            CrashLog.Trace($"[air] action {a.Number}: changes on save: {a.MisnumberedClsnLines} Clsn lines misnumbered, "
                + $"{a.MismatchedClsnCounts} Clsn counts not matching their lines, {a.MovedComments} comments to move after the last frame");
        foreach (var a in _air.Actions)
            if (a.FramesWithDefaultAndOwnClsn() is { Count: > 0 } both)
                CrashLog.Trace($"[air] action {a.Number}: default and own Clsn on frame {string.Join(", ", both.Select(i => i + 1))}");
        CrashLog.Trace("LoadAir: done");
    }

    /// <summary>One row of the action list: a raw number, and a comment that is
    /// the action's real comment when it has one, or a bracketed reserved-action
    /// suggestion for a standalone .air or a character definition. The suggestion is never written to
    /// the file on its own — double-clicking it is what injects it, and only
    /// when there is no comment there to overwrite. Stages and other .defs get no suggestions.</summary>
    private sealed class ActionRow
    {
        public required AnimationAction Action { get; init; }
        public string NumberText => Action.Number.ToString(CultureInfo.InvariantCulture);
        /// <summary>The same for every row (see ActionNumberWidth).</summary>
        public double NumberWidth { get; init; }
        public string? SuggestedName { get; init; }
        public string CommentText => Action.Comments.Count > 0
            ? Action.Comments[0]
            : SuggestedName is { } s ? $"[{s}]" : "";
    }

    private void RebuildActionList()
    {
        if (_air is null) return;
        var current = ActionList.SelectedItem as ActionRow;
        var text = (ActionFilterBox?.Text ?? "").Trim();

        double numberWidth = ActionNumberWidth();
        if (ActionNumberHeader is not null) ActionNumberHeader.Width = numberWidth;
        var rows = _air.Actions
            .Where(a => text.Length == 0 || MatchesAction(a, text))
            .Select(a => new ActionRow
            {
                Action = a,
                NumberWidth = numberWidth,
                SuggestedName = SuggestedActionName(a.Number),
            })
            .ToList();

        _suppressEvents = true;
        ActionList.ItemsSource = rows;
        // Re-find by identity rather than the old row index, since filtering
        // (and reordering) both mean position no longer says which action it was.
        if (current is not null)
        {
            int idx = rows.FindIndex(r => ReferenceEquals(r.Action, current.Action));
            if (idx >= 0) ActionList.SelectedIndex = idx;
        }
        _suppressEvents = false;
    }

    /// <summary>
    /// One width for the Number column, header and rows alike, fitting the widest action number in
    /// the file (filtered out or not) and the header. Set explicitly: SharedSizeGroup did not keep the
    /// virtualized rows aligned (2- and 3-digit rows came out different widths).
    /// </summary>
    private double ActionNumberWidth()
    {
        double size = this.TryFindResource("UiFont13", out var v) && v is double d ? d : 13;
        double Measure(string s, FontWeight weight) => new Avalonia.Media.TextFormatting.TextLayout(s, new Typeface(FontFamily, weight: weight), size, null).Width;
        double widest = Measure("Number", FontWeight.Bold);
        foreach (var s in (_air?.Actions ?? []).Select(a => a.Number.ToString(CultureInfo.InvariantCulture)).Distinct())
            widest = Math.Max(widest, Measure(s, FontWeight.Normal));
        return Math.Ceiling(widest) + 8 + 1; // cell padding 4 + 4, right border 1
    }

    /// <summary>Matches on the action number (prefix) or its comment/suggested name (contains), same spirit as the sprite list's filter.</summary>
    private bool MatchesAction(AnimationAction a, string text) =>
        a.Number.ToString(CultureInfo.InvariantCulture).StartsWith(text, StringComparison.OrdinalIgnoreCase)
        || (a.Comments.Count > 0 && a.Comments[0].Contains(text, StringComparison.OrdinalIgnoreCase))
        || (SuggestedActionName(a.Number) is { } s && s.Contains(text, StringComparison.OrdinalIgnoreCase));

    private string? SuggestedActionName(int number) => _airShowsSuggestedNames ? ReservedActions.Name(number) : null;

    private void OnActionFilterChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        RebuildActionList();
    }

    private async void OnSaveAir(object? sender, RoutedEventArgs e) => await SaveAir(askPath: false);
    private async void OnSaveAirAs(object? sender, RoutedEventArgs e) => await SaveAir(askPath: true);

    /// <summary>Save (askPath false: reuse the current path if any) or Save As.</summary>
    internal async Task SaveAir(bool askPath)
    {
        try
        {
            if (_air is null) return;
            if (!await ConfirmClsnRewrite()) return;
            string? path = _airPath;
            if (path is null || !File.Exists(path) || askPath)
            {
                bool definition = _airEmbeddedInDef;
                var file = await PickSave(new FilePickerSaveOptions
                {
                    Title = definition ? "Save definition" : "Save animations",
                    DefaultExtension = definition ? "def" : "air",
                    FileTypeChoices = [definition ? DefType : AirType],
                });
                if (file?.TryGetLocalPath() is not { } chosen) return;
                path = chosen;
            }
            var linted = LintedActions();
            var encoding = EncodingChangeNote(done: true);
            if (await SaveAirTo(path))
            {
                StatusText.Text = $"Saved {Path.GetFileName(_airPath!)}";
                await ReportClsnRewrite(linted, encoding);
            }
            else if (_airError is not null)
                await Dialogs.ShowMessage(this, _airEmbeddedInDef ? "Could not save the definition" : "Could not save the animation file", _airError);
        }
        catch (Exception ex)
        {
            await Failed("SaveAir", ex);
        }
    }

    /// <summary>How many actions a save would write differently from the file without the user editing them.</summary>
    private int ActionsChangingOnSave() => _air?.Actions.Count(a => a.ChangesOnSave) ?? 0;

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    /// <summary>Actions a save would write differently without the user editing them, taken just before a save (which clears it).</summary>
    private List<(int Number, int Misnumbered, int Mismatched, int Moved)> LintedActions() => _air is null ? []
        : _air.Actions.Where(a => a.ChangesOnSave)
            .Select(a => (a.Number, a.MisnumberedClsnLines, a.MismatchedClsnCounts, a.MovedComments)).ToList();

    /// <summary>What a save changes that nobody edited, as sentence parts; past tense for the after-save report.</summary>
    private static List<string> SaveChangeParts(List<(int Number, int Misnumbered, int Mismatched, int Moved)> linted, bool done)
    {
        int mis = linted.Sum(x => x.Misnumbered), cnt = linted.Sum(x => x.Mismatched), moved = linted.Sum(x => x.Moved);
        var parts = new List<string>();
        if (mis > 0) parts.Add(done ? $"{Plural(mis, "Clsn line was", "Clsn lines were")} renumbered from 0"
            : $"renumber {Plural(mis, "Clsn line", "Clsn lines")} from 0");
        if (cnt > 0) parts.Add(done ? $"{Plural(cnt, "Clsn count was", "Clsn counts were")} rewritten to match the boxes"
            : $"rewrite {Plural(cnt, "Clsn count", "Clsn counts")} to match the boxes");
        if (moved > 0) parts.Add(done ? $"{Plural(moved, "comment was", "comments were")} moved after the last frame"
            : $"move {Plural(moved, "comment", "comments")} after the last frame");
        return parts;
    }

    /// <summary>For an old-encoding file: what saving converts, with a sample line to check the guess. Null otherwise.</summary>
    private string? EncodingChangeNote(bool done)
    {
        if (_airEmbeddedInDef || _air is not { TextEncoding: AirEncoding.Legacy } air) return null;
        var sample = AirWriter.Write(air).Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.Any(c => c > '\u007F'));
        return (done ? $"The file was converted from {air.LegacyEncodingName} (guessed) to UTF-8."
                     : $"This file isn't UTF-8; it was read as {air.LegacyEncodingName} (a guess). Saving converts it to UTF-8.")
            + (sample is null ? "" : $"{Environment.NewLine}Check this line reads right: {sample.Trim()}");
    }

    /// <summary>Warns before a save that will write things nobody edited differently from the file; false = canceled.</summary>
    private async Task<bool> ConfirmClsnRewrite()
    {
        var linted = LintedActions();
        var encoding = EncodingChangeNote(done: false);
        if (linted.Count == 0 && encoding is null) return true;
        var text = new List<string>();
        if (linted.Count > 0)
            text.Add($"Saving will {string.Join(", ", SaveChangeParts(linted, done: false))} in {Plural(linted.Count, "action", "actions")} "
                + $"({string.Join(", ", linted.Select(x => x.Number))}).");
        if (encoding is not null) text.Add(encoding);
        text.Add("Nothing else changes.");
        return await Dialogs.Confirm(this, "Saving will change the file", string.Join(Environment.NewLine + Environment.NewLine, text), "Save");
    }

    /// <summary>Tells, right after a save, what it wrote differently from the file without being edited.
    /// Pass the encoding note taken before the save (the save makes the file UTF-8).</summary>
    private async Task ReportClsnRewrite(List<(int Number, int Misnumbered, int Mismatched, int Moved)> linted, string? encoding)
    {
        if (linted.Count == 0 && encoding is null) return;
        foreach (var x in linted)
            CrashLog.Trace($"[air] saved action {x.Number}: {x.Misnumbered} Clsn lines renumbered, {x.Mismatched} Clsn counts rewritten, {x.Moved} comments moved");
        if (encoding is not null) CrashLog.Trace($"[air] {encoding}");
        var text = new List<string>();
        if (linted.Count > 0)
            text.Add($"In {Plural(linted.Count, "action", "actions")} ({string.Join(", ", linted.Select(x => x.Number))}), "
                + $"{string.Join(", ", SaveChangeParts(linted, done: true))}.");
        if (encoding is not null) text.Add(encoding);
        text.Add("Nothing else changed.");
        await Dialogs.ShowMessage(this, "Saved with changes", string.Join(Environment.NewLine + Environment.NewLine, text));
    }

    /// <summary>Forgets the Clsn lint (after a save wrote the file clean).</summary>
    private void ClearClsnLint()
    {
        if (_air is null) return;
        foreach (var a in _air.Actions)
        {
            a.MisnumberedClsnLines = a.MismatchedClsnCounts = a.MovedComments = 0; // comments now sit after the last frame
        }
    }

    /// <summary>Writes the animation file or patches embedded actions in a .def, backing up the file it replaces.</summary>
    internal async Task<bool> SaveAirTo(string? path)
    {
        if (_air is null || path is null) return false;
        if (!await ConfirmRecoveredSourceWrite(path)) return false;
        _airError = null;
        try
        {
            byte[]? sourceBytesForSave = null;
            string? trackedSourcePath = null;
            if (_airEmbeddedInDef || PathsEqual(path, _airPath))
            {
                if (_airPath is null) throw new InvalidOperationException("The animation source path is missing.");
                trackedSourcePath = _airPath;
                var current = await CurrentSourceBytesForSave(_airPath, _airEmbeddedInDef);
                if (!current.Continue) return false;
                sourceBytesForSave = current.Bytes;
            }

            byte[]? definitionToWrite = null;
            List<AnimationAction>? writtenDefOrder = null;
            if (_airEmbeddedInDef)
            {
                definitionToWrite = Air.EmbeddedAirDocument.ReplaceActions(sourceBytesForSave!, _air, _airDefSourceActions, out var order);
                writtenDefOrder = order;
            }

            Backups.Make(path, _settings.BackupCount);
            if (sourceBytesForSave is not null && trackedSourcePath is not null)
            {
                var currentHash = SHA256.HashData(File.ReadAllBytes(trackedSourcePath));
                if (!CryptographicOperations.FixedTimeEquals(currentHash, SHA256.HashData(sourceBytesForSave)))
                    throw new IOException("The source file changed again while the save was being prepared. No changes were written; try saving again.");
            }

            if (_airEmbeddedInDef)
            {
                Sff.AtomicFile.WriteAllBytes(path, definitionToWrite!);
                _airDefSourceActions = writtenDefOrder!;
                _airDiskHash = SHA256.HashData(definitionToWrite!);
            }
            else
            {
                _air.Save(path);
                _airDiskHash = SHA256.HashData(AirFile.Encode(AirWriter.Write(_air), _air.TextEncoding));
            }
            ClearClsnLint(); // the file on disk is clean now
            _airPath = path;
            _airDirty = false;
            _undo.MarkSaved(AirKind);
            ResolveRecoveredItem("air", trackedSourcePath ?? path);
            UpdateAirInfo();
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("SaveAirTo", ex);
            _airError = ex.Message;
            return false;
        }
    }

    private async Task<(bool Continue, byte[]? Bytes)> CurrentSourceBytesForSave(string path, bool definition)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The source file was removed. Use Save As to create another file.", path);

        byte[]? acceptedHash = _airDiskHash;
        while (true)
        {
            var bytes = File.ReadAllBytes(path);
            var hash = SHA256.HashData(bytes);
            if (acceptedHash is not null && CryptographicOperations.FixedTimeEquals(hash, acceptedHash))
                return (true, bytes);

            string message = definition
                ? $"{Path.GetFileName(path)} changed on disk since it was opened. Saving will replace its [Begin Action] blocks with the actions in Media and preserve the other current contents."
                : $"{Path.GetFileName(path)} changed on disk since it was opened. Saving will overwrite it with the animation currently in Media.";
            if (!await Dialogs.Confirm(this, "File changed outside Media", message, "Save"))
                return (false, null);
            acceptedHash = hash;
        }
    }

    private static bool PathsEqual(string a, string? b)
    {
        if (b is null) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparison);
    }

    /// <summary>
    /// The open .air's filename lives in the status bar's corner rather than
    /// its own label at the top of the action list — the list right below it
    /// already makes clear something's loaded, so a repeated filename there
    /// was just using up space for nothing. Action/frame totals follow the
    /// Sounds tab's "N sounds" style.
    /// </summary>
    private void UpdateAirInfo()
    {
        if (AirStatusText is null) return;
        if (_air is null) { AirStatusText.Text = ""; UpdateTitle(); return; }

        int frames = _air.Actions.Sum(a => a.Elements.Count);
        AirStatusText.Text = (_airDirty ? "* " : "") + (_airPath is null ? "Unsaved animation" : Path.GetFileName(_airPath))
            + $": {_air.Actions.Count} action{(_air.Actions.Count == 1 ? "" : "s")}"
            + $", {frames} frame{(frames == 1 ? "" : "s")}"
            + (ActionsChangingOnSave() is var changing and > 0 ? $", {changing} change on save" : "")
            + (_air.Actions.Sum(a => a.FramesWithDefaultAndOwnClsn().Count) is var both and > 0
                ? $", {both} invalid frame{(both == 1 ? "" : "s")} (default and own Clsn)" : "")
            + (_air.TextEncoding == AirEncoding.Legacy ? $", {_air.LegacyEncodingName} (becomes UTF-8 on save)" : "");
        UpdateTitle();
    }

    private void MarkAirDirty()
    {
        if (!_undo.Tag(AirKind)) _undo.Untracked(AirKind);
        _airDirty = true;
        UpdateAirInfo();
        SyncSaveMenu();
    }

    // ---- browsing -----------------------------------------------------------

    private void OnActionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _air is null || ActionList.SelectedItem is not ActionRow row) return;
        _action = row.Action;
        _tick = 0;
        ResetPlaybackCaches(); // keeps the bitmap cache to one action's sprites

        RebuildFrameList();
        // Suppressed: OnFrameSelected pauses, and playback should carry on into the new action.
        _suppressEvents = true;
        if (_action.Elements.Count > 0) FrameList.SelectedIndex = 0;
        _suppressEvents = false;
        if (_action.Elements.Count == 0) Pause();
        TimelineView?.ResetView();
        TimelineView?.SetPlayTick(_action.Elements.Count > 0 ? 0 : -1);
        ShowFrame();
        SyncTextFromAction();
        RebuildFrameTable();
    }

    /// <summary>
    /// The one place that writes an action's comment, so the name-column
    /// double-click and the "inject suggested name" path both go through the
    /// same undo and refresh logic.
    /// </summary>
    private void SetActionComment(AnimationAction action, List<string> after)
    {
        var before = action.Comments.ToList();
        if (before.SequenceEqual(after)) return;

        void Apply(List<string> c)
        {
            action.Comments = [.. c];
            RebuildActionList();
        }
        _undo.Do("action comment", () => Apply(after), () => Apply(before));
        MarkAirDirty();
    }

    private void RebuildFrameList()
    {
        if (_action is null) return;
        int keep = FrameList.SelectedIndex;
        _suppressEvents = true;
        // 1-indexed: the frame someone points to in conversation is "frame 1", not "frame 0".
        FrameList.ItemsSource = _action.Elements.Select((el, i) => new FrameRow
        {
            Number = (i + 1).ToString(CultureInfo.InvariantCulture),
            Sprite = $"{el.Group},{el.Index}",
            Duration = el.ShowsForever ? "hold" : el.Duration.ToString(CultureInfo.InvariantCulture),
            Flags = (el.FlipH ? "H" : "") + (el.FlipV ? "V" : ""),
        }).ToList();
        if (keep >= 0 && keep < _action.Elements.Count) FrameList.SelectedIndex = keep;
        _suppressEvents = false;
        UpdateTimeline();
    }

    /// <summary>One row of the frame list's table: sprite, duration and flip flags in their own columns instead of one packed string.</summary>
    private sealed class FrameRow
    {
        public required string Number { get; init; }
        public required string Sprite { get; init; }
        public required string Duration { get; init; }
        public required string Flags { get; init; }
    }

    private void OnFrameSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        Pause();
        ShowFrame();
        SyncFrameTableSelection();
    }

    /// <summary>
    /// Cheap now that the table is virtualized: just moves the highlight, not
    /// a full RebuildFrameTable, so Timeline clicks and keyboard nav keep the
    /// table's selected row in sync without extra rebuild cost. Rows are one per
    /// frame now (the Loopstart row became an "L" tag), so FrameRow == position.
    /// </summary>
    private void SyncFrameTableSelection()
    {
        if (!_frameTableActive || FrameTableList is null) return;
        int idx = FrameList.SelectedIndex;
        var rows = FrameTableList.ItemsSource as ICollection<FrameTableRow>;
        if (rows is null) return;
        int target = rows.ToList().FindIndex(r => r.FrameRow == idx);
        if (idx < 0 || target < 0 || target == FrameTableList.SelectedIndex) return;

        _suppressEvents = true;
        FrameTableList.SelectedIndex = target;
        _suppressEvents = false;
        FrameTableList.ScrollIntoView(target);
    }

    /// <summary>Draws the selected frame with its sprite and its boxes.</summary>
    private void RefreshFrameOffset()
    {
        if (CurrentElement is not { } element || AnimCanvas is null) return;
        AnimCanvas.ElementOffset = new Point(element.OffsetX, element.OffsetY);
        AnimCanvas.InvalidateVisual();
        var loop = _action?.LoopTime is { } t ? $"{t} ticks" : "no loop";
        FrameInfo.Text = _animSprite is null && !element.NoSprite
            ? $"Sprite {element.Group},{element.Index} is not in the loaded .sff"
            : $"frame {FrameList.SelectedIndex + 1} of {_action!.Elements.Count}   |   "
              + (element.NoSprite ? "no sprite   |   " : "")
              + $"offset {element.OffsetX},{element.OffsetY}   |   action {loop}";
        if (_frameTableActive) RefreshFrameTableRow(FrameList.SelectedIndex);
        if (_frameTextActive) SyncTextFromAction();
    }

    private void ShowFrame()
    {
        if (CurrentElement is not { } element || AnimCanvas is null) return;

        var sprite = FindAnimSprite(element.Group, element.Index);
        _animSprite = sprite; // null (missing sprite) means nothing to sync
        AnimCanvas.ElementOffset = new Point(element.OffsetX, element.OffsetY);
        AnimCanvas.FlipH = element.FlipH;
        AnimCanvas.FlipV = element.FlipV;
        AnimCanvas.ElementScaleX = element.ScaleX;
        AnimCanvas.ElementScaleY = element.ScaleY;
        AnimCanvas.ElementAngle = element.Angle;
        AnimCanvas.ElementBlend = element.Blend;
        AnimCanvas.Boxes = ShowBoxesItem?.IsChecked == true ? BoxesFor(element) : new List<BoxOverlay>();
        AnimCanvas.SelectBox(-1);
        AnimCanvas.Show(sprite, sprite?.Palette, _showMask, preserveZoom: true);

        ShowAnimOnion();

        var loop = _action?.LoopTime is { } t ? $"{t} ticks" : "no loop";
        FrameInfo.Text = sprite is null && !element.NoSprite
            ? $"Sprite {element.Group},{element.Index} is not in the loaded .sff"
            : $"frame {FrameList.SelectedIndex + 1} of {_action!.Elements.Count}   |   "
              + (element.NoSprite ? "no sprite   |   " : "")
              + $"offset {element.OffsetX},{element.OffsetY}   |   action {loop}";

        UpdateBoxInfo();
        UpdateTimeline();
    }

    private static List<BoxOverlay> BoxesFor(AnimationElement element)
    {
        var hurtColor = element.OwnHurtBoxes ? HurtColor : Clsn2DefaultColor;
        var attackColor = element.OwnAttackBoxes ? AttackColor : Clsn1DefaultColor;

        var boxes = new List<BoxOverlay>();
        for (int i = 0; i < element.Hurt.Count; i++)
        {
            var b = element.Hurt[i];
            boxes.Add(new BoxOverlay { X1 = b.X1, Y1 = b.Y1, X2 = b.X2, Y2 = b.Y2, Color = hurtColor, Tag = ClsnKind.Hurt, Index = i });
        }
        for (int i = 0; i < element.Attack.Count; i++)
        {
            var b = element.Attack[i];
            boxes.Add(new BoxOverlay { X1 = b.X1, Y1 = b.Y1, X2 = b.X2, Y2 = b.Y2, Color = attackColor, Tag = ClsnKind.Attack, Index = i });
        }
        return boxes;
    }


    /// <summary>
    /// Writes a default segment's boxes and refreshes every still-inheriting
    /// element in its range to match (BoxesFor/Timeline read each element's
    /// own baked copy directly, rather than looking the segment up
    /// themselves). Doesn't touch ownership, undo, or any of the usual
    /// post-edit refresh calls — callers handle those.
    /// </summary>
    private void ApplyDefaultSegmentBoxes(ClsnKind kind, DefaultSegment segment, List<ClsnBox> boxes)
    {
        if (_action is not { } action) return;
        segment.Boxes = [.. boxes];
        var segments = kind == ClsnKind.Attack ? action.DefaultAttackSegments : action.DefaultHurtSegments;
        int segIdx = segments.IndexOf(segment);
        int rangeEnd = segIdx + 1 < segments.Count ? segments[segIdx + 1].At : action.Elements.Count;
        for (int i = segment.At; i < rangeEnd && i < action.Elements.Count; i++)
        {
            var el = action.Elements[i];
            bool ownHere = kind == ClsnKind.Attack ? el.OwnAttackBoxes : el.OwnHurtBoxes;
            if (ownHere) continue;
            if (kind == ClsnKind.Attack) el.Attack = [.. boxes]; else el.Hurt = [.. boxes];
        }
    }

    /// <summary>Records an edit to a default segment as one undo step, applying it immediately.
    /// Undo restores whole-action snapshots, so it survives later edits that clone the segment lists.
    /// An edit that empties the segment removes the declaration (never "ClsnXDefault: 0").</summary>
    private void RecordDefaultSegmentEdit(string name, ClsnKind kind, DefaultSegment segment, List<ClsnBox> after)
    {
        if (_action is not { } action) return;
        var before = CaptureActionBoxes(action);
        ApplyDefaultSegmentBoxes(kind, segment, after);
        if (after.Count == 0)
            (kind == ClsnKind.Attack ? action.DefaultAttackSegments : action.DefaultHurtSegments).Remove(segment);
        var afterState = CaptureActionBoxes(action);
        _undo.Record(name, () => RestoreActionBoxes(action, before), () => RestoreActionBoxes(action, afterState));
        MarkAirDirty();
        RestoreActionBoxes(action, afterState);
    }

    private static bool SameBoxes(List<ClsnBox> a, List<ClsnBox> b) =>
        a.Count == b.Count && !a.Where((x, i) =>
            x.X1 != b[i].X1 || x.Y1 != b[i].Y1 || x.X2 != b[i].X2 || x.Y2 != b[i].Y2).Any();

    private void UpdateTimeline()
    {
        if (TimelineView is null) return;
        if (_action is null) { TimelineView.Show([], -1); return; }
        var action = _action;
        int loopAt = action.LoopStart > 0 ? action.LoopStart : -1;
        var frames = action.Elements.Select((el, i) => new Timeline.Frame(
            el.Duration,
            AttackColor: (el.OwnAttackBoxes ? el.Attack.Count : action.AttackSegmentAt(i)?.Boxes.Count ?? 0) > 0
                ? (el.OwnAttackBoxes ? AttackColor : Clsn1DefaultColor) : null,
            HurtColor: (el.OwnHurtBoxes ? el.Hurt.Count : action.HurtSegmentAt(i)?.Boxes.Count ?? 0) > 0
                ? (el.OwnHurtBoxes ? HurtColor : Clsn2DefaultColor) : null,
            Loop: i == loopAt)).ToList();
        TimelineView.Show(frames, FrameList.SelectedIndex);

        // A frame picked any other way than playback or scrubbing moves the playhead (and where Play resumes) to its start.
        int selected = FrameList.SelectedIndex;
        if (selected >= 0 && selected < action.Elements.Count && action.PositionAt(_tick).Index != selected)
            _tick = action.Elements.Take(selected).Sum(el => Math.Max(el.Duration, 0));
        TimelineView.SetPlayTick(action.Elements.Count > 0 ? PlayheadTick(action) : -1);
    }

    private void OnTimelineFrameClicked(int frame)
    {
        Pause();
        if (_action is null || frame < 0 || frame >= _action.Elements.Count) return;
        bool changed = FrameList.SelectedIndex != frame;
        FrameList.SelectedIndex = frame;
        _tick = _action.Elements.Take(frame).Sum(el => Math.Max(el.Duration, 0));
        if (!changed) ShowFrame(); // a changed selection already refreshes through OnFrameSelected
    }

    // ---- box editing --------------------------------------------------------

    // ---- offset dragging ------------------------------------------------------

    private (int X, int Y)? _offsetBeforeDrag;

    private void OnOffsetDragStarted()
    {
        if (_clock is not null || CurrentElement is not { } el) return;
        _offsetBeforeDrag = (el.OffsetX, el.OffsetY);
    }

    private void OnOffsetDelta(int dx, int dy)
    {
        if (_clock is not null || CurrentElement is not { } el) return;
        el.OffsetX += dx;
        el.OffsetY += dy;
        AnimCanvas.ElementOffset = new Point(el.OffsetX, el.OffsetY);
        AnimCanvas.InvalidateVisual();
    }

    private void OnOffsetDragFinished()
    {
        if (CurrentElement is not { } el || _offsetBeforeDrag is not { } before) return;
        _offsetBeforeDrag = null;
        var after = (el.OffsetX, el.OffsetY);
        if (before == after) return;

        void Apply((int X, int Y) v)
        {
            el.OffsetX = v.X; el.OffsetY = v.Y;
            RefreshFrameOffset();
            RefreshFrameTableRow(FrameList.SelectedIndex);
        }
        _undo.Record("move offset", () => Apply(before), () => Apply(after));
        Apply(after);
        MarkAirDirty();
    }

    private void OnBoxEditStarted()
    {
        // Playback can replace the overlays while a drag is in progress, so
        // keep the current frame stable until this edit is complete.
        Pause();
        _dragDefaultSegments.Clear();
        if (CurrentElement is not { } element || _action is not { } action) return;

        int index = FrameList.SelectedIndex;
        var touchedKinds = AnimCanvas.EditingBoxes
            .Where(i => i >= 0 && i < AnimCanvas.Boxes.Count)
            .Select(i => (ClsnKind?)AnimCanvas.Boxes[i].Tag)
            .Where(k => k.HasValue).Select(k => k!.Value).Distinct();
        foreach (var kind in touchedKinds)
        {
            bool own = kind == ClsnKind.Attack ? element.OwnAttackBoxes : element.OwnHurtBoxes;
            var segment = kind == ClsnKind.Attack
                ? action.AttackSegmentAt(index) : action.HurtSegmentAt(index);
            // Only a segment's declaring frame edits it directly. An inherited
            // frame becomes own when moved, without changing the shared default.
            if (!own && segment is not null && segment.At == index)
                _dragDefaultSegments[kind] = segment;
        }
        _boxesBeforeDrag = CaptureActionBoxes(action);
    }

    private void OnBoxChanged()
    {
        if (CurrentElement is not { } element) return;
        var touchedKinds = AnimCanvas.EditingBoxes
            .Where(i => i >= 0 && i < AnimCanvas.Boxes.Count)
            .Select(i => (ClsnKind?)AnimCanvas.Boxes[i].Tag)
            .Where(k => k.HasValue).Select(k => k!.Value).Distinct();
        foreach (var kind in touchedKinds)
        {
            var live = AnimCanvas.Boxes.Where(b => (ClsnKind?)b.Tag == kind)
                .Select(b => new ClsnBox(b.X1, b.Y1, b.X2, b.Y2).Normalized()).ToList();
            if (_dragDefaultSegments.TryGetValue(kind, out var segment))
                ApplyDefaultSegmentBoxes(kind, segment, live);
            else if (kind == ClsnKind.Attack)
            {
                element.Attack = live;
                element.OwnAttackBoxes = true;
            }
            else
            {
                element.Hurt = live;
                element.OwnHurtBoxes = true;
            }
        }
        UpdateBoxInfo();
    }

    private static bool SameSegments(List<DefaultSegment> a, List<DefaultSegment> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.At == p.Second.At && SameBoxes(p.First.Boxes, p.Second.Boxes));

    private static bool SameActionBoxes(ActionBoxState a, ActionBoxState b) =>
        SameSegments(a.Attack, b.Attack) && SameSegments(a.Hurt, b.Hurt)
        && a.Elements.Count == b.Elements.Count
        && a.Elements.Zip(b.Elements).All(p =>
            p.First.OwnHurt == p.Second.OwnHurt && p.First.OwnAttack == p.Second.OwnAttack
            && SameBoxes(p.First.Hurt, p.Second.Hurt) && SameBoxes(p.First.Attack, p.Second.Attack));

    /// <summary>Restores a drag's before/after snapshot, keeping the box selection.</summary>
    private void ApplyBoxEdit(AnimationAction action, ActionBoxState state)
    {
        int[] selected = AnimCanvas.SelectedBoxes.ToArray();
        int active = AnimCanvas.SelectedBox;
        RestoreActionBoxes(action, state);
        if (_action != action) return;
        AnimCanvas.SelectBoxes(selected, active);
        UpdateBoxInfo();
    }

    private void OnBoxEditFinished()
    {
        if (_action is not { } action || _boxesBeforeDrag is not { } before) return;
        var after = CaptureActionBoxes(action);
        _boxesBeforeDrag = null;
        _dragDefaultSegments.Clear();
        if (SameActionBoxes(before, after)) return;

        string mergeKey = $"clsn-reposition:{action.Number}:{FrameList.SelectedIndex}";
        _undo.Record("move boxes", () => ApplyBoxEdit(action, before), () => ApplyBoxEdit(action, after), mergeKey);
        MarkAirDirty();
        SyncTextFromAction();
    }

    private static List<(ClsnBox Box, bool IsAttack)> Snapshot(AnimationElement e) =>
        e.Hurt.Select(b => (b, false)).Concat(e.Attack.Select(b => (b, true))).ToList();

    private static bool Same(List<(ClsnBox Box, bool IsAttack)> a, List<(ClsnBox Box, bool IsAttack)> b) =>
        a.Count == b.Count && !a.Where((x, i) =>
            x.IsAttack != b[i].IsAttack || x.Box.X1 != b[i].Box.X1 || x.Box.Y1 != b[i].Box.Y1 ||
            x.Box.X2 != b[i].Box.X2 || x.Box.Y2 != b[i].Box.Y2).Any();

    /// <summary>Applies a frame's new box list as one undo step (whole-action snapshots).
    /// Returns false, changing nothing, when the edit would leave the model as it was.</summary>
    private bool RecordBoxEdit(string name, AnimationElement element,
        List<(ClsnBox Box, bool IsAttack)> after, ClsnKind? touched)
    {
        if (_action is not { } action) return false;
        var before = CaptureActionBoxes(action);
        ApplyOwnState(action, element, after, touched);
        var afterState = CaptureActionBoxes(action);
        if (SameActionBoxes(before, afterState)) return false;
        _undo.Record(name, () => RestoreActionBoxes(action, before), () => RestoreActionBoxes(action, afterState));
        MarkAirDirty();
        RestoreActionBoxes(action, afterState);
        return true;
    }

    /// <summary>Sets a frame's boxes; the touched kind becomes own. A kind left with no
    /// boxes uses the default in force instead — edits never create "ClsnX: 0".</summary>
    private static void ApplyOwnState(AnimationAction action, AnimationElement e,
        List<(ClsnBox Box, bool IsAttack)> state, ClsnKind? touched)
    {
        e.Hurt = state.Where(x => !x.IsAttack).Select(x => x.Box.Normalized()).ToList();
        e.Attack = state.Where(x => x.IsAttack).Select(x => x.Box.Normalized()).ToList();
        if (touched == ClsnKind.Hurt) e.OwnHurtBoxes = e.Hurt.Count > 0;
        else if (touched == ClsnKind.Attack) e.OwnAttackBoxes = e.Attack.Count > 0;
        int i = action.Elements.IndexOf(e);
        if (!e.OwnHurtBoxes) e.Hurt = [.. action.HurtSegmentAt(i)?.Boxes ?? []];
        if (!e.OwnAttackBoxes) e.Attack = [.. action.AttackSegmentAt(i)?.Boxes ?? []];
    }

    /// <summary>Canvas index of the last box of a kind — one just added (BoxesFor lists Clsn2 before Clsn1).</summary>
    private int LastBoxOf(ClsnKind kind)
    {
        for (int i = AnimCanvas.Boxes.Count - 1; i >= 0; i--)
            if ((ClsnKind?)AnimCanvas.Boxes[i].Tag == kind) return i;
        return -1;
    }

    private void OnAddHurtBox(object? sender, RoutedEventArgs e) => AddBox(ClsnKind.Hurt);
    private void OnAddAttackBox(object? sender, RoutedEventArgs e) => AddBox(ClsnKind.Attack);

    private void AddBox(ClsnKind kind)
    {
        if (CurrentElement is not { } element || _action is not { } action) return;
        int index = FrameList.SelectedIndex;
        var sprite = _file?.Find(element.Group, element.Index);
        var box = sprite is { Width: > 0 }
            ? new ClsnBox(-sprite.AxisX / 2, -sprite.AxisY, sprite.Width - sprite.AxisX - sprite.AxisX / 2, 0)
            : new ClsnBox(-20, -60, 20, 0);

        bool own = kind == ClsnKind.Attack ? element.OwnAttackBoxes : element.OwnHurtBoxes;
        var segment = kind == ClsnKind.Attack ? action.AttackSegmentAt(index) : action.HurtSegmentAt(index);

        if (!own && segment is not null && segment.At == index)
        {
            // This is the frame the default was declared at: add to the
            // shared segment itself, so every other still-inheriting frame in
            // its range picks up the new box too — not just this one.
            var after = new List<ClsnBox>(segment.Boxes) { box };
            RecordDefaultSegmentEdit("add default box", kind, segment, after);
            AnimCanvas.SelectBox(LastBoxOf(kind));
            return;
        }

        // Not own, and not the declaring frame either (or there's no default
        // at all here): own starts fresh. The OTHER kind, if any, keeps
        // whatever it already had — own or still-inherited — undisturbed;
        // this kind gets exactly the box just added, not the default's boxes
        // dragged along as if the person had placed them there themselves.
        var current = Snapshot(element);
        var beforeState = own ? current : current.Where(x => x.IsAttack != (kind == ClsnKind.Attack)).ToList();
        var afterState = new List<(ClsnBox, bool)>(beforeState) { (box, kind == ClsnKind.Attack) };
        RecordBoxEdit("add box", element, afterState, kind);
        AnimCanvas.SelectBox(LastBoxOf(kind));
    }

    private void OnDeleteBox(object? sender, RoutedEventArgs e) => DeleteBox(AnimCanvas.SelectedBox);

    /// <summary>Removes the current frame's own boxes (a default declared on it
    /// counts as its own). A frame left with no own Clsn uses the default in
    /// force, like the text. A frame with nothing of its own is a no-op.</summary>
    private void OnDeleteAllBoxes(object? sender, RoutedEventArgs e)
    {
        if (_action is not { } action || CurrentElement is not { } element) return;
        int at = action.Elements.IndexOf(element);
        bool attackDeclared = !element.OwnAttackBoxes && action.AttackSegmentAt(at)?.At == at;
        bool hurtDeclared = !element.OwnHurtBoxes && action.HurtSegmentAt(at)?.At == at;
        if (!element.OwnAttackBoxes && !element.OwnHurtBoxes && !attackDeclared && !hurtDeclared)
        {
            StatusText.Text = "This frame has no boxes of its own.";
            return;
        }
        var before = CaptureActionBoxes(action);

        if (attackDeclared) action.DefaultAttackSegments.RemoveAll(s => s.At == at);
        if (hurtDeclared) action.DefaultHurtSegments.RemoveAll(s => s.At == at);
        element.OwnHurtBoxes = element.OwnAttackBoxes = false;
        var after = CaptureActionBoxes(action);

        _undo.Record("delete all boxes", () => RestoreActionBoxes(action, before), () => RestoreActionBoxes(action, after));
        RestoreActionBoxes(action, after); // rebakes inheriting frames and refreshes the views
        MarkAirDirty();
    }

    private sealed record ActionBoxState(List<DefaultSegment> Attack, List<DefaultSegment> Hurt,
        List<(List<ClsnBox> Hurt, List<ClsnBox> Attack, bool OwnHurt, bool OwnAttack)> Elements);

    private static ActionBoxState CaptureActionBoxes(AnimationAction action) => new(
        CloneSegments(action.DefaultAttackSegments), CloneSegments(action.DefaultHurtSegments),
        action.Elements.Select(e => (e.Hurt.ToList(), e.Attack.ToList(), e.OwnHurtBoxes, e.OwnAttackBoxes)).ToList());

    /// <summary>Restores segments and ownership, then re-bakes every inheriting frame from its governing segment.</summary>
    private void RestoreActionBoxes(AnimationAction action, ActionBoxState state)
    {
        action.DefaultAttackSegments = CloneSegments(state.Attack);
        action.DefaultHurtSegments = CloneSegments(state.Hurt);
        for (int i = 0; i < state.Elements.Count && i < action.Elements.Count; i++)
        {
            var el = action.Elements[i];
            var (hurt, attack, ownHurt, ownAttack) = state.Elements[i];
            el.OwnHurtBoxes = ownHurt;
            el.OwnAttackBoxes = ownAttack;
            el.Hurt = (ownHurt ? hurt : action.HurtSegmentAt(i)?.Boxes ?? []).ToList();
            el.Attack = (ownAttack ? attack : action.AttackSegmentAt(i)?.Boxes ?? []).ToList();
        }
        if (_action != action) return;
        RebuildFrameList();
        ShowFrame();
        RebuildFrameTable();
        SyncTextFromAction();
    }

    private void OnDeleteAllBoxesInAction(object? sender, RoutedEventArgs e)
    {
        if (_action is not { } action)
        {
            StatusText.Text = "Open an .air and select an action first.";
            return;
        }
        DeleteAllBoxesInAction(action);
    }

    /// <summary>Removes every box from an action: all frames' own boxes plus
    /// the action's shared Clsn1Default/Clsn2Default segments. One undo entry.</summary>
    private void DeleteAllBoxesInAction(AnimationAction action)
    {
        int count = action.DefaultAttackSegments.Sum(s => s.Boxes.Count) + action.DefaultHurtSegments.Sum(s => s.Boxes.Count)
            + action.Elements.Sum(e => e.Hurt.Count + e.Attack.Count);
        if (count == 0)
        {
            StatusText.Text = $"Action {action.Number} has no boxes to delete.";
            return;
        }

        var beforeAttackSegs = CloneSegments(action.DefaultAttackSegments);
        var beforeHurtSegs = CloneSegments(action.DefaultHurtSegments);
        var before = action.Elements
            .Select(e => (e.Hurt.ToList(), e.Attack.ToList(), e.OwnHurtBoxes, e.OwnAttackBoxes)).ToList();
        var after = action.Elements
            .Select(_ => (new List<ClsnBox>(), new List<ClsnBox>(), false, false)).ToList(); // no own boxes, no defaults

        void Apply(List<DefaultSegment> attackSegs, List<DefaultSegment> hurtSegs,
            List<(List<ClsnBox> Hurt, List<ClsnBox> Attack, bool OwnHurt, bool OwnAttack)> state)
        {
            action.DefaultAttackSegments = CloneSegments(attackSegs);
            action.DefaultHurtSegments = CloneSegments(hurtSegs);
            for (int i = 0; i < state.Count && i < action.Elements.Count; i++)
            {
                var (hurt, attack, ownHurt, ownAttack) = state[i];
                action.Elements[i].Hurt = [.. hurt];
                action.Elements[i].Attack = [.. attack];
                action.Elements[i].OwnHurtBoxes = ownHurt;
                action.Elements[i].OwnAttackBoxes = ownAttack;
            }
            if (_action == action)
            {
                RebuildFrameList();
                ShowFrame();
                SyncTextFromAction();
            }
        }

        _undo.Record($"delete all boxes (action {action.Number})",
            () => Apply(beforeAttackSegs, beforeHurtSegs, before),
            () => Apply([], [], after));
        Apply([], [], after);
        MarkAirDirty();
        StatusText.Text = $"Deleted {count} boxes from action {action.Number}.";
    }

    private void DeleteBox(int index)
    {
        if (CurrentElement is not { } element || index < 0 || index >= AnimCanvas.Boxes.Count) return;
        var touched = (ClsnKind?)AnimCanvas.Boxes[index].Tag;
        if (DeclaredSegmentHere(element, touched) is { } segment)
        {
            // Declaring frame: remove from the shared default, same as AddBox adds to it.
            // Removing its last box removes the declaration (see RecordDefaultSegmentEdit).
            var segAfter = segment.Boxes.ToList();
            segAfter.RemoveAt(AnimCanvas.Boxes[index].Index);
            RecordDefaultSegmentEdit("delete default box", touched!.Value, segment, segAfter);
            return;
        }
        var after = Snapshot(element);
        after.RemoveAt(index);
        // Deleting an inherited default's only box here would just fall back to that default.
        if (!RecordBoxEdit("delete box", element, after, touched) && _action is { } action && touched is { } k
            && (k == ClsnKind.Attack ? action.AttackSegmentAt(action.Elements.IndexOf(element))
                                     : action.HurtSegmentAt(action.Elements.IndexOf(element))) is { } seg)
            StatusText.Text = $"This box comes from the default declared on frame {seg.At + 1}; edit it there.";
    }

    /// <summary>The default segment declared at the current frame for this kind, if the frame is
    /// still inheriting it — i.e. edits here should go to the segment, not become own.</summary>
    private DefaultSegment? DeclaredSegmentHere(AnimationElement element, ClsnKind? kind)
    {
        if (_action is not { } action || kind is not { } k) return null;
        int at = action.Elements.IndexOf(element);
        bool own = k == ClsnKind.Attack ? element.OwnAttackBoxes : element.OwnHurtBoxes;
        var seg = k == ClsnKind.Attack ? action.AttackSegmentAt(at) : action.HurtSegmentAt(at);
        return !own && seg is not null && seg.At == at ? seg : null;
    }

    private void DuplicateBox(int index)
    {
        if (CurrentElement is not { } element || index < 0 || index >= AnimCanvas.Boxes.Count) return;
        var b = AnimCanvas.Boxes[index];
        var touched = (ClsnKind?)b.Tag ?? ClsnKind.Hurt;
        var box = new ClsnBox(b.X1 + 4, b.Y1 + 4, b.X2 + 4, b.Y2 + 4);
        if (DeclaredSegmentHere(element, touched) is { } segment)
        {
            // Declaring frame: duplicate into the shared default, same as AddBox.
            RecordDefaultSegmentEdit("duplicate default box", touched, segment, [.. segment.Boxes, box]);
            AnimCanvas.SelectBox(LastBoxOf(touched));
            return;
        }
        var after = new List<(ClsnBox, bool)>(Snapshot(element)) { (box, touched == ClsnKind.Attack) };
        RecordBoxEdit("duplicate box", element, after, touched);
        AnimCanvas.SelectBox(LastBoxOf(touched));
    }

    /// <summary>
    /// Toggles the selected box's kind (Clsn1 or Clsn2, whichever the selection
    /// is) between the frame's own boxes and the action's default: if this
    /// frame currently has its own, they become the new default and the frame
    /// drops back to using it; if it's already showing the default, the frame
    /// gets its own copy instead. Which kind toggles is decided entirely by
    /// what's selected on the canvas — the two kinds are never touched together.
    /// </summary>
    private void OnMakeBoxDefault(object? sender, RoutedEventArgs e)
    {
        if (CurrentElement is not { } element) return;
        if (AnimCanvas.SelectedBox < 0 || AnimCanvas.SelectedBox >= AnimCanvas.Boxes.Count)
        {
            StatusText.Text = "Select a box first.";
            return;
        }
        var kind = (ClsnKind?)AnimCanvas.Boxes[AnimCanvas.SelectedBox].Tag ?? ClsnKind.Hurt;
        ToggleDefault(element, kind);
    }

    private void MakeBoxDefault(int boxIndex)
    {
        if (CurrentElement is not { } element || boxIndex < 0 || boxIndex >= AnimCanvas.Boxes.Count) return;
        var kind = (ClsnKind?)AnimCanvas.Boxes[boxIndex].Tag ?? ClsnKind.Hurt;
        ToggleDefault(element, kind);
    }

    private void ToggleDefault(AnimationElement element, ClsnKind kind)
    {
        if (_action is null) return;
        var action = _action;
        bool attack = kind == ClsnKind.Attack;
        bool currentlyOwn = attack ? element.OwnAttackBoxes : element.OwnHurtBoxes;
        int index = action.Elements.IndexOf(element);
        var segments = attack ? action.DefaultAttackSegments : action.DefaultHurtSegments;

        var beforeSegs = CloneSegments(segments);
        var beforeBoxes = (attack ? element.Attack : element.Hurt).ToList();

        List<DefaultSegment> afterSegs;
        bool afterOwn;
        List<ClsnBox> afterBoxes;

        if (currentlyOwn)
        {
            afterSegs = CloneSegments(segments);
            afterSegs.RemoveAll(s => s.At == index);
            afterSegs.Add(new DefaultSegment { At = index, Boxes = [.. beforeBoxes] });
            afterSegs.Sort((a, b) => a.At.CompareTo(b.At));
            afterOwn = false;
            afterBoxes = beforeBoxes;
        }
        else
        {
            // This frame keeps a copy as its own. If it's the declaring frame, the
            // default is removed too, so later frames stop inheriting it.
            var governing = attack ? action.AttackSegmentAt(index) : action.HurtSegmentAt(index);
            afterSegs = CloneSegments(segments);
            if (governing is not null && governing.At == index) afterSegs.RemoveAll(s => s.At == index);
            afterOwn = true;
            afterBoxes = governing?.Boxes.ToList() ?? [];
        }

        void Apply(List<DefaultSegment> segs, bool own, List<ClsnBox> boxes)
        {
            // Same boxes in the same order, so the selection survives ShowFrame's reset.
            int[] selected = AnimCanvas.SelectedBoxes.ToArray();
            int active = AnimCanvas.SelectedBox;
            if (attack) action.DefaultAttackSegments = CloneSegments(segs);
            else action.DefaultHurtSegments = CloneSegments(segs);
            if (attack) { element.Attack = [.. boxes]; element.OwnAttackBoxes = own; }
            else { element.Hurt = [.. boxes]; element.OwnHurtBoxes = own; }

            // Every OTHER still-inheriting element needs its own baked copy
            // refreshed too, since the segment boundaries may have just
            // changed which one actually governs it.
            for (int i = 0; i < action.Elements.Count; i++)
            {
                if (i == index) continue;
                var el = action.Elements[i];
                bool ownHere = attack ? el.OwnAttackBoxes : el.OwnHurtBoxes;
                if (ownHere) continue;
                var seg = attack ? action.AttackSegmentAt(i) : action.HurtSegmentAt(i);
                if (attack) el.Attack = [.. seg?.Boxes ?? []]; else el.Hurt = [.. seg?.Boxes ?? []];
            }

            RebuildFrameList();
            ShowFrame();
            AnimCanvas.SelectBoxes(selected, active);
            RebuildFrameTable();
            SyncTextFromAction();
        }

        _undo.Record(currentlyOwn ? "make default" : "make own",
            () => Apply(beforeSegs, currentlyOwn, beforeBoxes),
            () => Apply(afterSegs, afterOwn, afterBoxes));
        Apply(afterSegs, afterOwn, afterBoxes);
        MarkAirDirty();
        StatusText.Text = currentlyOwn
            ? $"{(attack ? "Clsn1" : "Clsn2")} is now this action's default from frame {index + 1} onward."
            : afterSegs.Count < beforeSegs.Count
                ? $"{(attack ? "Clsn1" : "Clsn2")} is now this frame's own; the default declared here was removed."
                : $"{(attack ? "Clsn1" : "Clsn2")} is now this frame's own.";
    }

    private async void OnSortBoxes(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (CurrentElement is not { } element) return;
            if (!element.OwnAttackBoxes && !element.OwnHurtBoxes)
            {
                StatusText.Text = "This frame has no boxes of its own to sort — only a default it's inheriting.";
                return;
            }
            if (await Dialogs.PromptBoxSortOrder(this) is not { } order) return;
            SortBoxes(order);
        }
        catch (Exception ex)
        {
            await Failed("OnSortBoxes", ex);
        }
    }

    /// <summary>
    /// Reorders this frame's own Clsn1 and Clsn2 lists. Only touches a kind
    /// the frame actually owns — reordering a kind still shown from the
    /// action's shared default would reorder that default for every other
    /// frame using it too, not just this one.
    /// </summary>
    private void SortBoxes(BoxSortOrder order)
    {
        if (CurrentElement is not { } element) return;

        var beforeAttack = element.Attack.ToList();
        var beforeHurt = element.Hurt.ToList();
        var afterAttack = element.OwnAttackBoxes ? Sorted(beforeAttack, order) : beforeAttack;
        var afterHurt = element.OwnHurtBoxes ? Sorted(beforeHurt, order) : beforeHurt;

        void Apply(List<ClsnBox> attack, List<ClsnBox> hurt)
        {
            element.Attack = [.. attack];
            element.Hurt = [.. hurt];
            RebuildFrameList();
            ShowFrame();
            SyncTextFromAction();
        }
        _undo.Record($"sort boxes ({order})", () => Apply(beforeAttack, beforeHurt), () => Apply(afterAttack, afterHurt));
        Apply(afterAttack, afterHurt);
        MarkAirDirty();
    }

    /// <summary>Sorts every frame's own Clsn1/Clsn2 and every Clsn1Default/Clsn2Default, in every
    /// action, after a confirm. Inheriting frames follow their default. One undo.</summary>
    private async void OnSortBoxesForAll(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_air is not { } air)
            {
                StatusText.Text = "Open an .air first.";
                return;
            }
            if (await Dialogs.PromptBoxSortOrder(this, "Reorder every frame's own Clsn1/Clsn2 and every default by:") is not { } order) return;

            // Work on copies first, so the confirm can count real changes and Cancel leaves everything as it was.
            var before = air.Actions.ToDictionary(a => a, CaptureActionBoxes);
            var work = before.ToList();
            var progressWindow = new Dialogs.ProgressWindow(this, "Sort Boxes For All", "Sorting boxes: action", work.Count);
            var progress = new Progress<int>(progressWindow.Report);
            var token = progressWindow.Token;
            List<(AnimationAction Action, ActionBoxState After, int Changed)> sortedStates;
            try
            {
                sortedStates = await Task.Run(() =>
                {
                    var list = new List<(AnimationAction, ActionBoxState, int)>();
                    for (int k = 0; k < work.Count; k++)
                    {
                        token.ThrowIfCancellationRequested();
                        ((IProgress<int>)progress).Report(k);
                        var (action, state) = work[k];
                        int changed = 0;
                        List<DefaultSegment> SortSegs(List<DefaultSegment> segs) => segs.Select(s =>
                        {
                            var sorted = Sorted(s.Boxes, order);
                            if (!SameBoxes(sorted, s.Boxes)) changed++;
                            return new DefaultSegment { At = s.At, Boxes = sorted };
                        }).ToList();
                        var attackSegs = SortSegs(state.Attack);
                        var hurtSegs = SortSegs(state.Hurt);
                        var elements = state.Elements.Select(x =>
                        {
                            var hurt = x.OwnHurt ? Sorted(x.Hurt, order) : x.Hurt;
                            var attack = x.OwnAttack ? Sorted(x.Attack, order) : x.Attack;
                            if (!SameBoxes(hurt, x.Hurt) || !SameBoxes(attack, x.Attack)) changed++;
                            return (hurt, attack, x.OwnHurt, x.OwnAttack);
                        }).ToList();
                        if (changed > 0) list.Add((action, new ActionBoxState(attackSegs, hurtSegs, elements), changed));
                    }
                    return list;
                }, token);
            }
            catch (OperationCanceledException)
            {
                CrashLog.Trace("Sort Boxes For All: canceled");
                StatusText.Text = "Sort Boxes For All canceled; nothing changed.";
                return;
            }
            finally
            {
                progressWindow.Close();
            }
            var after = sortedStates.ToDictionary(t => t.Action, t => t.After);
            int frames = sortedStates.Sum(t => t.Changed);
            // Also settles the Clsn lint found on load: every box gets written as [0], [1], ... under its real count.
            var lint = air.Actions.Where(a => a.HasClsnLint)
                .ToDictionary(a => a, a => (a.MisnumberedClsnLines, a.MismatchedClsnCounts));
            if (after.Count == 0 && lint.Count == 0)
            {
                StatusText.Text = "Every frame's boxes are already in that order.";
                return;
            }
            var parts = new List<string>();
            if (after.Count > 0) parts.Add($"Sort boxes on {Plural(frames, "frame", "frames")} in {Plural(after.Count, "action", "actions")}.");
            if (lint.Count > 0) parts.Add($"Fix Clsn counts and numbering in {Plural(lint.Count, "action", "actions")}.");
            if (!await Dialogs.Confirm(this, "Sort Boxes For All", string.Join(Environment.NewLine, parts), "Sort")) return;

            CrashLog.Trace($"Sort Boxes For All: {order}, {frames} frames in {after.Count} actions, lint fixed in {lint.Count}");
            var undoState = after.Keys.ToDictionary(a => a, a => before[a]);
            void Apply(Dictionary<AnimationAction, ActionBoxState> states, bool fixedLint)
            {
                foreach (var (act, state) in states) RestoreActionBoxes(act, state); // rebakes inheriting frames
                foreach (var (act, (misnumbered, mismatched)) in lint)
                {
                    act.MisnumberedClsnLines = fixedLint ? 0 : misnumbered;
                    act.MismatchedClsnCounts = fixedLint ? 0 : mismatched;
                }
                UpdateAirInfo();
            }
            _undo.Record($"sort boxes for all ({order})", () => Apply(undoState, false), () => Apply(after, true));
            Apply(after, true);
            MarkAirDirty();
            StatusText.Text = (after.Count > 0 ? $"Sorted boxes on {Plural(frames, "frame", "frames")} in {Plural(after.Count, "action", "actions")}" : "Boxes already in order")
                + (lint.Count > 0 ? $"; Clsn fixed in {Plural(lint.Count, "action", "actions")}." : ".");
        }
        catch (Exception ex)
        {
            await Failed("OnSortBoxesForAll", ex);
        }
    }

    private static List<ClsnBox> Sorted(List<ClsnBox> boxes, BoxSortOrder order) => order switch
    {
        BoxSortOrder.TopToBottom => boxes.OrderBy(b => b.Top).ToList(),
        BoxSortOrder.BottomToTop => boxes.OrderByDescending(b => b.Top).ToList(),
        BoxSortOrder.LeftToRight => boxes.OrderBy(b => b.Left).ToList(),
        BoxSortOrder.RightToLeft => boxes.OrderByDescending(b => b.Left).ToList(),
        BoxSortOrder.SmallestFirst => boxes.OrderBy(b => b.Width * b.Height).ToList(),
        BoxSortOrder.LargestFirst => boxes.OrderByDescending(b => b.Width * b.Height).ToList(),
        _ => boxes,
    };

    /// <summary>Generates Clsn2 boxes covering this frame's transformed sprite,
    /// replacing its own Hurt boxes. Clsn1 is untouched; the count and layout are
    /// asked for in a dialog, remembered for the session.</summary>
    private int? _autoBoxCount; // last choice this session; starts at settings.json AutoBoxesDefault
    private CoverLayout? _autoLayout; // last choice this session; starts at settings.json AutoBoxesLayout

    private async void OnAutoHurtBoxes(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (CurrentElement is not { } element) return;
            if (element.NoSprite)
            {
                StatusText.Text = "This frame has no sprite (-1), so there's nothing for Auto Clsn2 to cover.";
                return;
            }
            var sprite = _file?.Find(element.Group, element.Index);
            if (sprite is not { Width: > 0, Height: > 0 } || !sprite.HasPixels)
            {
                if (await Dialogs.AskOpenSpriteFile(this)) OnOpen(null, new RoutedEventArgs());
                return;
            }

            int initialBoxes = _autoBoxCount ?? Settings.Checked(nameof(Settings.AutoBoxesDefault), _settings.AutoBoxesDefault,
                Math.Min(Settings.DefaultAutoBoxes, _settings.MaxAutoBoxes), v => v >= 1 && v <= _settings.MaxAutoBoxes);
            CoverLayout initialLayout;
            if (_autoLayout is { } last) initialLayout = last;
            else if (Enum.TryParse<CoverLayout>(_settings.AutoBoxesLayout, true, out var a) && Enum.IsDefined(a)) initialLayout = a;
            else
            {
                CrashLog.Error("Settings", $"AutoBoxesLayout = {_settings.AutoBoxesLayout} isn't valid; using Stacked");
                initialLayout = CoverLayout.Stacked;
            }
            if (await Dialogs.PromptAutoBoxCount(this, initialBoxes, initialLayout, _settings.MaxAutoBoxes) is not { } choice) return;
            _autoBoxCount = choice.Boxes;
            _autoLayout = choice.Layout;
            CrashLog.Trace($"Auto Clsn2: cover sprite {element.Group},{element.Index} frame {FrameList.SelectedIndex + 1}, H={element.FlipH} V={element.FlipV} scale={element.ScaleX},{element.ScaleY} angle={element.Angle} boxes={choice.Boxes} layout={choice.Layout}");
            var rects = SpriteCover.CoverTransformed(sprite, choice.Boxes, choice.Layout,
                element.FlipH, element.FlipV, element.ScaleX, element.ScaleY, element.Angle);
            CrashLog.Trace($"Auto Clsn2: generated {rects.Count} boxes for sprite {element.Group},{element.Index}");
            if (rects.Count == 0)
            {
                StatusText.Text = "Auto Clsn2 found no visible pixels to cover.";
                return;
            }

            // Covers come back relative to the sprite's axis; the frame offset moves the sprite, not the boxes.
            var boxes = rects.Select(r => new ClsnBox(r.X1 + element.OffsetX, r.Y1 + element.OffsetY,
                r.X2 + element.OffsetX, r.Y2 + element.OffsetY)).ToList();
            if (DeclaredSegmentHere(element, ClsnKind.Hurt) is { } segment)
            {
                // Declaring frame: the new boxes replace the default itself.
                RecordDefaultSegmentEdit("auto clsn2", ClsnKind.Hurt, segment, boxes);
                return;
            }
            var before = Snapshot(element);
            var after = new List<(ClsnBox, bool)>(before.Where(x => x.IsAttack));
            foreach (var box in boxes) after.Add((box, false));
            RecordBoxEdit("auto clsn2", element, after, ClsnKind.Hurt);
        }
        catch (Exception ex)
        {
            // async void: an exception here has nowhere else to go — log it
            // explicitly rather than relying on it reaching AppDomain intact.
            CrashLog.Write("OnAutoHurtBoxes", ex);
            await Dialogs.ShowMessage(this, "Something went wrong generating Clsn2 boxes", ex.Message);
        }
    }

    /// <summary>
    /// Auto Clsn2 on every frame in every action; with "Skip frames without Clsn2", only frames with
    /// Clsn2 of their own (a Clsn2Default declared on a frame counts, and gets replaced). One dialog,
    /// a confirm, then one undo step for all. Frames with no usable sprite keep their boxes (counted in the status bar).
    /// </summary>
    private async void OnAutoHurtBoxesForAll(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_air is not { } air || _file is null)
            {
                StatusText.Text = "Open an .air and its .sff first.";
                return;
            }

            int initialBoxes = _autoBoxCount ?? Settings.Checked(nameof(Settings.AutoBoxesDefault), _settings.AutoBoxesDefault,
                Math.Min(Settings.DefaultAutoBoxes, _settings.MaxAutoBoxes), v => v >= 1 && v <= _settings.MaxAutoBoxes);
            CoverLayout initialLayout;
            if (_autoLayout is { } last) initialLayout = last;
            else if (Enum.TryParse<CoverLayout>(_settings.AutoBoxesLayout, true, out var a) && Enum.IsDefined(a)) initialLayout = a;
            else
            {
                CrashLog.Error("Settings", $"AutoBoxesLayout = {_settings.AutoBoxesLayout} isn't valid; using Stacked");
                initialLayout = CoverLayout.Stacked;
            }
            if (await Dialogs.PromptAutoBoxCount(this, initialBoxes, initialLayout, _settings.MaxAutoBoxes, forAll: true) is not { } choice) return;
            _autoBoxCount = choice.Boxes;
            _autoLayout = choice.Layout;

            // Targets: (action, frame, the segment it declares or null for own boxes). Unticked, every
            // frame: a declaring frame replaces its default, any other gets own boxes (AGENTS.md: Clsn).
            var targets = new List<(AnimationAction Action, int Frame, DefaultSegment? Segment, Sprite? Sprite)>();
            int noSprite = 0; // -1 frames: nothing to cover, and not a missing sprite
            foreach (var action in air.Actions)
                for (int i = 0; i < action.Elements.Count; i++)
                {
                    var el = action.Elements[i];
                    if (el.NoSprite) { noSprite++; continue; }
                    DefaultSegment? declared = !el.OwnHurtBoxes && action.HurtSegmentAt(i) is { } seg && seg.At == i ? seg : null;
                    if (choice.SkipWithoutClsn2 && !(el.OwnHurtBoxes && el.Hurt.Count > 0) && declared is null) continue;
                    var sprite = _file.Find(el.Group, el.Index);
                    targets.Add((action, i, declared, sprite is { Width: > 0, Height: > 0, HasPixels: true } ? sprite : null));
                }
            if (targets.Count == 0)
            {
                StatusText.Text = "No frame has Clsn2 of its own.";
                return;
            }

            int usable = targets.Count(t => t.Sprite is not null);
            int actions = targets.Where(t => t.Sprite is not null).Select(t => t.Action).Distinct().Count();
            if (usable > 0 && !await Dialogs.Confirm(this, "Auto Clsn2 For All",
                    $"Replace Clsn2 on {usable} frames in {actions} actions?", "Replace")) return;

            CrashLog.Trace($"Auto Clsn2 For All: {targets.Count} frames, {usable} with sprites, boxes={choice.Boxes} layout={choice.Layout} skipWithout={choice.SkipWithoutClsn2}");
            // Modal progress: blocks the window (so nothing changes underneath) until done or canceled.
            var progressWindow = new Dialogs.ProgressWindow(this, "Auto Clsn2 For All", "Generating Clsn2: frame", targets.Count);
            var progress = new Progress<int>(progressWindow.Report);
            var token = progressWindow.Token;
            List<List<ClsnBox>?> covers;
            try
            {
                covers = await Task.Run(() =>
                {
                    var list = new List<List<ClsnBox>?>(targets.Count);
                    for (int k = 0; k < targets.Count; k++)
                    {
                        token.ThrowIfCancellationRequested();
                        ((IProgress<int>)progress).Report(k);
                        var t = targets[k];
                        if (t.Sprite is not { } sp) { list.Add(null); continue; }
                        var el = t.Action.Elements[t.Frame];
                        list.Add(SpriteCover.CoverTransformed(sp, choice.Boxes, choice.Layout,
                                el.FlipH, el.FlipV, el.ScaleX, el.ScaleY, el.Angle)
                            .Select(r => new ClsnBox(r.X1 + el.OffsetX, r.Y1 + el.OffsetY, r.X2 + el.OffsetX, r.Y2 + el.OffsetY)).ToList());
                    }
                    return list;
                }, token);
            }
            catch (OperationCanceledException)
            {
                CrashLog.Trace("Auto Clsn2 For All: canceled");
                StatusText.Text = "Auto Clsn2 For All canceled; nothing changed.";
                return;
            }
            finally
            {
                progressWindow.Close();
            }

            var skipped = new List<string>();
            int missing = 0, blank = 0;
            var before = new Dictionary<AnimationAction, ActionBoxState>();
            for (int k = 0; k < targets.Count; k++)
            {
                var (action, frame, segment, sprite) = targets[k];
                var el = action.Elements[frame];
                string where = $"Action {action.Number}, frame {frame + 1} (sprite {el.Group},{el.Index})";
                if (sprite is null) { skipped.Add($"{where}: sprite missing or empty"); missing++; continue; }
                if (covers[k] is not { Count: > 0 } boxes) { skipped.Add($"{where}: no visible pixels"); blank++; continue; }
                if (!before.ContainsKey(action)) before[action] = CaptureActionBoxes(action);
                if (segment is not null) segment.Boxes = [.. boxes];
                else { el.Hurt = boxes; el.OwnHurtBoxes = true; }
            }
            foreach (var s in skipped) CrashLog.Error("Auto Clsn2 For All", $"skipped {s}");

            if (before.Count > 0)
            {
                var after = before.Keys.ToDictionary(act => act, CaptureActionBoxes);
                foreach (var (act, state) in after) RestoreActionBoxes(act, state); // rebakes inheriting frames
                _undo.Record("auto clsn2 for all",
                    () => { foreach (var (act, state) in before) RestoreActionBoxes(act, state); },
                    () => { foreach (var (act, state) in after) RestoreActionBoxes(act, state); });
                MarkAirDirty();
            }
            int done = targets.Count - skipped.Count;
            var notes = new List<string>();
            if (missing > 0) notes.Add($"{missing} skipped (sprite missing)");
            if (blank > 0) notes.Add($"{blank} skipped (no visible pixels)");
            if (noSprite > 0) notes.Add($"{noSprite} without a sprite (-1) left alone");
            StatusText.Text = $"Auto Clsn2: replaced Clsn2 on {done} frames in {before.Count} actions"
                + (notes.Count > 0 ? ", " + string.Join(", ", notes) + "." : ".");
        }
        catch (Exception ex)
        {
            await Failed("OnAutoHurtBoxesForAll", ex);
        }
    }

    private void UpdateBoxInfo()
    {
        if (BoxInfo is null) return;

        if (AnimCanvas.SelectedBox < 0 || AnimCanvas.SelectedBox >= AnimCanvas.Boxes.Count)
        {
            BoxInfo.Text = "No Clsn selected";
            if (MakeDefaultButton is not null) MakeDefaultButton.IsEnabled = false;
            return;
        }
        var b = AnimCanvas.Boxes[AnimCanvas.SelectedBox];
        bool attack = (ClsnKind?)b.Tag == ClsnKind.Attack;
        var kind = attack ? "Clsn1" : "Clsn2";
        BoxInfo.Text = $"{kind}[{b.Index}] = {b.X1}, {b.Y1}, {b.X2}, {b.Y2}  ({b.Right - b.Left} x {b.Bottom - b.Top})";

        if (MakeDefaultButton is not null) MakeDefaultButton.IsEnabled = true;
    }

    private void OnToggleBoxes(object? sender, RoutedEventArgs e) => ShowFrame();

    private void ToggleInterpolation(string kind)
    {
        if (_action is not { } action || CurrentElement is null) return;
        int frame = FrameList.SelectedIndex;
        if (frame < 0 || frame >= action.Elements.Count) return;

        SortedSet<int> set = kind switch
        {
            "offset" => action.InterpolateOffset,
            "scale" => action.InterpolateScale,
            "angle" => action.InterpolateAngle,
            "blend" => action.InterpolateBlend,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        bool before = set.Contains(frame);
        void Apply(bool enabled)
        {
            if (enabled) set.Add(frame); else set.Remove(frame);
            SyncTextFromAction();
            RebuildFrameTable();
            ShowFrame();
        }
        _undo.Do($"toggle interpolate {kind}", () => Apply(!before), () => Apply(before));
        MarkAirDirty();
        StatusText.Text = $"Interpolation {kind} {(before ? "disabled" : "enabled")} on frame {frame + 1}.";
    }

    private void OnBoxContextRequested(int index, Point screenPos)
    {
        bool hasFrame = CurrentElement is not null;
        if (index < 0)
        {
            var empty = new ContextMenu
            {
                ItemsSource = new List<object>
                {
                    MenuAction("Center image in canvas", AnimCanvas.CenterImage),
                    new Separator(),
                    MenuAction("Pin onion skin to this frame", PinFrameAsOnion, hasFrame),
                },
            };
            OpenContextMenu(AnimCanvas, empty);
            return;
        }

        var menu = new ContextMenu();
        var items = new List<object>
        {
            MenuAction("Center image in canvas", AnimCanvas.CenterImage),
            new Separator(),
            MenuAction("Delete", () => DeleteBox(index)),
            MenuAction("Duplicate", () => DuplicateBox(index)),
            MenuAction("Make default", () => MakeBoxDefault(index)),
            new Separator(),
            MenuAction("Pin onion skin to this frame", PinFrameAsOnion, hasFrame),
        };
        menu.ItemsSource = items;
        OpenContextMenu(AnimCanvas, menu);
    }

    private static MenuItem MenuAction(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    // ---- playback -------------------------------------------------------------

    private void OnPlayPause(object? sender, RoutedEventArgs e)
    {
        if (_clock is not null) { Pause(); return; }
        if (_action is null || _action.Elements.Count == 0)
        {
            PlayButton.IsChecked = false;
            return;
        }

        OnOffsetDragFinished();
        AnimCanvas.OffsetDraggingEnabled = false;
        _playWatch.Restart();
        _playedTicks = 0;
        AnimCanvas.CacheBitmaps = true;
        _clock = new DispatcherTimer { Interval = PlaybackPollInterval };
        _clock.Tick += (_, _) => Advance();
        _clock.Start();
        PlayButton.IsChecked = true;
    }

    private void WireTimelineEvents()
    {
        TimelineView.ContextRequested += (_, _) => ShowPlaceholderMenu(TimelineView);
    }

    private void Pause()
    {
        _clock?.Stop();
        _clock = null;
        _playWatch.Reset();
        ResetPlaybackCaches();
        AnimCanvas.OffsetDraggingEnabled = true;
        if (PlayButton is not null) PlayButton.IsChecked = false;
        TimelineView?.SetPlayTick(_action is { Elements.Count: > 0 } a ? PlayheadTick(a) : -1); // parked where it stopped
    }

    /// <summary>_file.Find, through a lookup table while playing.</summary>
    private Sprite? FindAnimSprite(int group, int index)
    {
        if (_file is null) return null;
        if (_clock is null) return _file.Find(group, index);
        if (_playSprites is null || !ReferenceEquals(_playSpritesFile, _file))
        {
            _playSprites = new(_file.Sprites.Count);
            foreach (var s in _file.Sprites) _playSprites.TryAdd((s.Group, s.Index), s); // first wins, like Find
            _playSpritesFile = _file;
        }
        return _playSprites.GetValueOrDefault((group, index));
    }

    /// <summary>Drops the playback sprite lookup and bitmap cache; starts a fresh cache if still playing.</summary>
    private void ResetPlaybackCaches()
    {
        _playSprites = null;
        _playSpritesFile = null;
        if (AnimCanvas is null) return;
        AnimCanvas.CacheBitmaps = false;
        AnimCanvas.CacheBitmaps = _clock is not null;
    }

    /// <summary>Steps every tick due since the last poll, then draws only the latest one.</summary>
    private void Advance()
    {
        if (_action is null) { Pause(); return; }
        long due = (long)(_playWatch.Elapsed.TotalSeconds / TickLength.TotalSeconds) - _playedTicks;
        if (due <= 0) return;
        if (due > MaxCatchUpTicks)
        {
            _playedTicks += due - MaxCatchUpTicks;
            due = MaxCatchUpTicks;
        }
        _playedTicks += due;
        for (long i = 0; i < due; i++) StepTick(_action);
        ShowPlayTick(_action);
    }

    /// <summary>Moves playback one tick forward, restarting after the hold or intro dwell.</summary>
    private void StepTick(AnimationAction action)
    {
        _tick++;

        // Ticks before the final hold. The engine just stops an action that
        // ends in -1, but a preview that stops dead needs a manual restart
        // every time — so playback dwells on the hold frame briefly, then
        // loops like any other action.
        int finite = action.Elements.TakeWhile(e => !e.ShowsForever).Sum(e => Math.Max(e.Duration, 0));
        bool infinite = action.LoopTime is null;
        if (infinite && _tick >= finite + InfiniteAnimationTimeoutTicks) _tick = 0;

        // A Loopstart past the beginning means there's an intro before the
        // loop point. Left looping forever (the normal, correct engine
        // behavior), a preview would show that intro exactly once and never
        // again. So: same dwell-then-restart idea as the infinite-hold case
        // above, just measured from the *second* time the loop point is
        // reached (end of the initial play-through, plus one full repeat of
        // the loop body) rather than from the first.
        if (!infinite && action.LoopStart > 0 && action.LoopTime is { } total)
        {
            int head = action.Elements.Take(action.LoopStart).Sum(e => e.Duration);
            int body = total - head;
            if (body > 0 && _tick >= total + body + InfiniteAnimationTimeoutTicks) _tick = 0;
        }
    }

    /// <summary>
    /// Where the playhead sits for _tick. During the dwell it parks at the track's
    /// end rather than wrapping around while the frame sits still. Finite actions
    /// wrap through WrapTick, the same loopback ElementAt replays by, so the
    /// playhead stays on the Loopstart element when the action loops.
    /// </summary>
    private int PlayheadTick(AnimationAction action)
    {
        if (action.LoopTime is not null) return action.WrapTick(_tick);
        int finite = action.Elements.TakeWhile(e => !e.ShowsForever).Sum(e => Math.Max(e.Duration, 0));
        return Math.Min(_tick, finite);
    }

    /// <summary>Drag on the timeline: pauses and shows the exact tick, interpolated.</summary>
    private void OnTimelineScrubbed(int tick)
    {
        if (_action is not { Elements.Count: > 0 } action) return;
        if (_clock is not null) Pause();
        _tick = tick;
        var (index, _) = action.PositionAt(tick);
        if (index >= 0 && index != FrameList.SelectedIndex)
        {
            _suppressEvents = true;
            FrameList.SelectedIndex = index;
            _suppressEvents = false;
            ShowFrame();
            SyncFrameTableSelection();
        }
        TimelineView.SetPlayTick(PlayheadTick(action));
        ApplyInterpolation(tick);
    }

    private bool _syncingTimelineScroll;

    /// <summary>Keeps the scrollbar under the timeline in step with its zoom and position.</summary>
    private void SyncTimelineScroll()
    {
        if (TimelineScroll is null || TimelineView is null) return;
        _syncingTimelineScroll = true;
        try
        {
            double visible = TimelineView.VisibleTicks, total = TimelineView.TotalTicks;
            TimelineScroll.Minimum = 0;
            TimelineScroll.Maximum = Math.Max(0, total - visible);
            TimelineScroll.ViewportSize = visible;
            TimelineScroll.Value = TimelineView.ViewStart;
            TimelineScroll.IsEnabled = total > visible;
        }
        finally
        {
            _syncingTimelineScroll = false;
        }
    }

    /// <summary>Draws the frame and playhead for the current tick.</summary>
    private void ShowPlayTick(AnimationAction action)
    {
        TimelineView.SetPlayTick(PlayheadTick(action));

        var element = action.ElementAt(_tick);
        if (element is null) { Pause(); return; }

        int index = action.Elements.IndexOf(element);
        if (index >= 0 && index != FrameList.SelectedIndex)
        {
            _suppressEvents = true;
            FrameList.SelectedIndex = index;
            _suppressEvents = false;
            ShowFrame();
        }
        ApplyInterpolation(_tick);
    }

    /// <summary>Playback only: overrides the frame's own offset/scale/angle/blend with the
    /// Interpolate-adjusted values for this tick (AnimationAction.StateAt). Boxes aren't interpolated.</summary>
    private void ApplyInterpolation(int tick)
    {
        if (_action is not { HasInterpolation: true } action || AnimCanvas is null) return;
        var st = action.StateAt(tick);
        AnimCanvas.ElementOffset = new Point(st.OffsetX, st.OffsetY);
        AnimCanvas.ElementScaleX = st.ScaleX;
        AnimCanvas.ElementScaleY = st.ScaleY;
        AnimCanvas.ElementAngle = st.Angle;
        AnimCanvas.ElementBlend = st.Blend;
        AnimCanvas.InvalidateVisual();
    }

    private void OnFirstFrame(object? sender, RoutedEventArgs e) => StepFrame(int.MinValue / 2);
    private void OnPrevFrame(object? sender, RoutedEventArgs e) => StepFrame(-1);
    private void OnNextFrame(object? sender, RoutedEventArgs e) => StepFrame(1);

    private void StepFrame(int delta)
    {
        Pause();
        if (_action is null || _action.Elements.Count == 0) return;
        FrameList.SelectedIndex = Math.Clamp(FrameList.SelectedIndex + delta, 0, _action.Elements.Count - 1);
        _tick = _action.Elements.Take(FrameList.SelectedIndex).Sum(el => Math.Max(el.Duration, 0));
        ShowFrame();
    }

    // ---- action management ----------------------------------------------------

    private const string ActionDragFormat = "ime/action-index";
    private bool _actionDragArmed;
    private int _actionDragFromIndex = -1;
    private Point _actionDragPressPoint;

    /// <summary>
    /// Arms a possible drag on the action list's row currently under the pointer.
    /// Skipped on the second click of a double-click (ClickCount &gt;= 2) — otherwise
    /// re-arming here on every press could race the double-tap gesture used to edit
    /// the Number/Comment cells: any pointer jitter between the two clicks would
    /// start a real drag instead, and DoubleTapped would never fire.
    /// </summary>
    private void OnActionRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_air is null || sender is not Control c || c.DataContext is not ActionRow row) return;
        var p = e.GetCurrentPoint(c);
        if (!p.Properties.IsLeftButtonPressed) return;
        if (e.ClickCount >= 2) { _actionDragArmed = false; return; }
        _actionDragFromIndex = _air.Actions.IndexOf(row.Action);
        _actionDragPressPoint = p.Position;
        _actionDragArmed = _actionDragFromIndex >= 0;
    }

    /// <summary>Past a small threshold, turns an armed press into a real drag carrying the source index.</summary>
    private async void OnActionRowPointerMoved(object? sender, PointerEventArgs e)
    {
        try
        {
            if (!_actionDragArmed || sender is not Control c) return;
            var p = e.GetPosition(c);
            double dx = p.X - _actionDragPressPoint.X, dy = p.Y - _actionDragPressPoint.Y;
            if (dx * dx + dy * dy < 36) return; // 6px, squared
            _actionDragArmed = false;

            var data = new DataObject();
            data.Set(ActionDragFormat, _actionDragFromIndex);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            await Failed("OnActionRowPointerMoved", ex);
        }
    }

    private void OnActionRowPointerReleased(object? sender, PointerReleasedEventArgs e) => _actionDragArmed = false;

    private void OnActionListDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.Data.Contains(ActionDragFormat) ? DragDropEffects.Move : DragDropEffects.None;

    private void OnActionListDrop(object? sender, DragEventArgs e)
    {
        if (_air is null || e.Data.Get(ActionDragFormat) is not int from) return;
        // IndexFromContainer is a position in the (possibly filtered) ItemsSource,
        // not in _air.Actions — translate through the row's own action identity,
        // the same way _actionDragFromIndex already does for the drag source.
        int to = _air.Actions.Count;
        if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(true) is { } item)
        {
            int row = ActionList.IndexFromContainer(item);
            if (row >= 0 && ActionList.ItemsSource is List<ActionRow> rows && row < rows.Count)
                to = _air.Actions.IndexOf(rows[row].Action);
        }
        if (to < 0) to = _air.Actions.Count;
        MoveAction(from, to);
    }

    /// <summary>
    /// Moves the action at `from` so it lands just before whatever was at `to`
    /// (in the list's indices before the move) — the usual drag-and-drop-list
    /// convention. `to == _air.Actions.Count` means "drop at the very end".
    /// </summary>
    private void MoveAction(int from, int to)
    {
        if (_air is null || from < 0 || from >= _air.Actions.Count) return;
        to = Math.Clamp(to, 0, _air.Actions.Count);
        if (to == from || to == from + 1) return; // no real move

        var air = _air;
        var action = air.Actions[from];

        void Apply(int f, int t)
        {
            air.Actions.RemoveAt(f);
            air.Actions.Insert(t > f ? t - 1 : t, action);
            RebuildActionList();
            SelectAction(action);
        }
        _undo.Do("reorder actions", () => Apply(from, to), () => Apply(to, from));
        MarkAirDirty();
    }

    /// <summary>Sorts every action by its number, ascending. One undo step for the whole reorder.</summary>
    private void OnSortActionsAscending(object? sender, RoutedEventArgs e)
    {
        if (_air is null || _air.Actions.Count < 2) return;
        var air = _air;
        var before = air.Actions.ToList();
        var after = air.Actions.OrderBy(a => a.Number).ToList();
        if (before.SequenceEqual(after)) return;

        void Apply(List<AnimationAction> order)
        {
            air.Actions = [.. order];
            RebuildActionList();
        }
        _undo.Do("sort actions", () => Apply(after), () => Apply(before));
        MarkAirDirty();
        StatusText.Text = "Actions sorted by number.";
    }

    private void OnNewAction(object? sender, RoutedEventArgs e)
    {
        if (_air is null) return;
        int number = 0;
        while (_air.Find(number) is not null) number++;

        var action = new AnimationAction { Number = number };
        action.Elements.Add(new AnimationElement { Duration = -1 });

        var air = _air;
        int at = ActionList.SelectedItem is ActionRow selRow
            ? air.Actions.IndexOf(selRow.Action) + 1
            : air.Actions.Count;
        _undo.Record("new action",
            () => { air.Actions.Remove(action); RebuildActionList(); },
            () => { air.Actions.Insert(Math.Min(at, air.Actions.Count), action); RebuildActionList(); SelectAction(action); });
        air.Actions.Insert(Math.Min(at, air.Actions.Count), action);
        RebuildActionList();
        SelectAction(action);
        MarkAirDirty();
    }

    private void OnDuplicateAction(object? sender, RoutedEventArgs e)
    {
        if (_air is null || _action is null) return;
        int number = 0;
        while (_air.Find(number) is not null) number++;

        var copy = new AnimationAction
        {
            Number = number,
            LoopStart = _action.LoopStart,
            CopyAction = _action.CopyAction,
            Comments = [.. _action.Comments],
            TrailingComments = [.. _action.TrailingComments],
            DefaultAttackSegments = CloneSegments(_action.DefaultAttackSegments),
            DefaultHurtSegments = CloneSegments(_action.DefaultHurtSegments),
            InterpolateOffset = new SortedSet<int>(_action.InterpolateOffset),
            InterpolateScale = new SortedSet<int>(_action.InterpolateScale),
            InterpolateAngle = new SortedSet<int>(_action.InterpolateAngle),
            InterpolateBlend = new SortedSet<int>(_action.InterpolateBlend),
        };
        foreach (var el in _action.Elements)
            copy.Elements.Add(new AnimationElement
            {
                Group = el.Group, Index = el.Index, OffsetX = el.OffsetX, OffsetY = el.OffsetY,
                Duration = el.Duration, FlipH = el.FlipH, FlipV = el.FlipV, Blend = el.Blend,
                ScaleX = el.ScaleX, ScaleY = el.ScaleY, Angle = el.Angle,
                Attack = [.. el.Attack], Hurt = [.. el.Hurt],
                OwnAttackBoxes = el.OwnAttackBoxes, OwnHurtBoxes = el.OwnHurtBoxes,
            });

        var air = _air;
        _undo.Record("duplicate action",
            () => { air.Actions.Remove(copy); RebuildActionList(); },
            () => { air.Actions.Add(copy); RebuildActionList(); SelectAction(copy); });
        air.Actions.Add(copy);
        RebuildActionList();
        SelectAction(copy);
        MarkAirDirty();
    }

    private async void OnDeleteAction(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_air is null || _action is null) return;
            var action = _action;
            var name = SuggestedActionName(action.Number) is { } n ? $"{action.Number} ({n})" : $"{action.Number}";
            if (!await Dialogs.Confirm(this, "Delete action", $"Delete action {name}?", "Delete")) return;

            var air = _air;
            int at = air.Actions.IndexOf(action);
            _undo.Record("delete action",
                () => { air.Actions.Insert(Math.Min(at, air.Actions.Count), action); RebuildActionList(); SelectAction(action); },
                () => { air.Actions.Remove(action); RebuildActionList(); });
            air.Actions.Remove(action);
            _action = null;
            RebuildActionList();
            if (air.Actions.Count > 0) SelectAction(air.Actions[Math.Min(at, air.Actions.Count - 1)]);
            MarkAirDirty();
        }
        catch (Exception ex)
        {
            await Failed("OnDeleteAction", ex);
        }
    }

    /// <summary>Selects an action by identity, not position. If it's filtered out of view, clears the filter first — same as the sprite list's SelectSprite.</summary>
    private void SelectAction(AnimationAction action)
    {
        if (_air is null || !_air.Actions.Contains(action)) return;
        if (FindActionRow(action) is not { } row)
        {
            _suppressEvents = true;
            if (ActionFilterBox is not null) ActionFilterBox.Text = "";
            _suppressEvents = false;
            RebuildActionList();
            row = FindActionRow(action);
            if (row is null) return;
        }
        int idx = ((List<ActionRow>)ActionList.ItemsSource!).IndexOf(row);
        ActionList.SelectedIndex = idx;
        ActionList.ScrollIntoView(idx);
    }

    private ActionRow? FindActionRow(AnimationAction action) =>
        (ActionList.ItemsSource as List<ActionRow>)?.FirstOrDefault(r => ReferenceEquals(r.Action, action));

    private async void OnApplyAllSuggestedNames(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_air is null) return;

            var targets = _air.Actions
                .Where(a => a.Comments.Count == 0)
                .Select(a => (Action: a, Name: SuggestedActionName(a.Number)))
                .Where(x => x.Name is not null)
                .Select(x => (x.Action, Name: x.Name!))
                .ToList();
            if (targets.Count == 0)
            {
                StatusText.Text = "Every action already has a name, or none have a suggested one.";
                return;
            }
            if (!await Dialogs.Confirm(this, "Apply suggested names",
                    $"Save the suggested name as the comment for {targets.Count} action(s) that don't have one yet?", "Apply"))
                return;

            void Apply(bool useSuggestion)
            {
                foreach (var (action, name) in targets) action.Comments = useSuggestion ? [name] : [];
                RebuildActionList();
            }
            _undo.Do($"apply {targets.Count} suggested name(s)", () => Apply(true), () => Apply(false));
            MarkAirDirty();
            StatusText.Text = $"Applied suggested names to {targets.Count} action(s).";
        }
        catch (Exception ex)
        {
            await Failed("OnApplyAllSuggestedNames", ex);
        }
    }

    private async void OnActionNumberDoubleTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            e.Handled = true;
            if (sender is not Control c || c.DataContext is not ActionRow row) return;
            await RenumberAction(row.Action);
        }
        catch (Exception ex)
        {
            await Failed("OnActionNumberDoubleTapped", ex);
        }
    }

    /// <summary>
    /// Double-clicking the comment: if the action already has one, edit it;
    /// otherwise, if a reserved name is showing as a bracketed suggestion,
    /// offer to write that suggestion in as the real comment. Never overwrites
    /// an existing comment — the suggestion only shows at all when there isn't one.
    /// </summary>
    private async void OnActionCommentDoubleTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            e.Handled = true;
            if (sender is not Control c || c.DataContext is not ActionRow row) return;
            SelectAction(row.Action);

            if (row.Action.Comments.Count == 0 && row.SuggestedName is { } suggestion)
            {
                if (await Dialogs.Confirm(this, "Add suggested name",
                        $"Save \"{suggestion}\" as this action's comment?", "Add"))
                    SetActionComment(row.Action, [suggestion]);
                return;
            }

            var text = await Dialogs.PromptText(this, "Action comment", "Comment for this action:",
                row.Action.Comments.Count > 0 ? row.Action.Comments[0] : "");
            if (text is null) return;
            var trimmed = text.Trim();
            SetActionComment(row.Action, trimmed.Length == 0 ? [] : [trimmed]);
        }
        catch (Exception ex)
        {
            await Failed("OnActionCommentDoubleTapped", ex);
        }
    }

    private async Task RenumberAction(AnimationAction action)
    {
        if (_air is null) return;
        SelectAction(action);

        var result = await Dialogs.PromptText(this, "Renumber action", "New action number:",
            action.Number.ToString(CultureInfo.InvariantCulture));
        if (result is null) return;
        if (!int.TryParse(result.Trim(), out int number) || number < 0)
        {
            // Ikemen keys actions by int32 and reserves -1 as Copy Action's
            // "unset" sentinel, so negative numbers are out regardless of mode.
            await Dialogs.ShowMessage(this, "Not a number", "Type a whole number of 0 or more.");
            return;
        }
        if (number != action.Number && _air.Find(number) is not null)
        {
            await Dialogs.ShowMessage(this, "Already used", $"Action {number} already exists in this file.");
            return;
        }

        int before = action.Number;
        void Apply(int n) { action.Number = n; RebuildActionList(); SelectAction(action); }
        _undo.Do("renumber action", () => Apply(number), () => Apply(before));
        MarkAirDirty();
    }

    private async void OnRenumberActionMenu(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_action is not { } action) return;
            await RenumberAction(action);
        }
        catch (Exception ex)
        {
            await Failed("OnRenumberActionMenu", ex);
        }
    }

    // ---- action name overrides -------------------------------------------

    private const string ActionNamesComment = "# Override suggested action names with one actionNumber=name entry per line.";

    /// <summary>
    /// Plain "number=name" lines, one per action, stored beside the app's
    /// other settings. A simple text format was chosen over a proper table
    /// editor because it is trivial to hand-edit, diff, and back up, and this
    /// project already uses the same "edit as text" pattern for AIR actions.
    /// </summary>
    private static string ActionNamesPath => AppPaths.DataFile("action-names.txt");

    private static void LoadActionNames()
    {
        try
        {
            if (!File.Exists(ActionNamesPath)) return;
            var custom = new Dictionary<int, string>();
            foreach (var line in File.ReadAllLines(ActionNamesPath))
            {
                if (line.TrimStart().StartsWith('#')) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (!int.TryParse(line[..eq].Trim(), out int n)) continue;
                var name = line[(eq + 1)..].Trim();
                if (name.Length > 0) custom[n] = name;
            }
            Air.ReservedActions.Custom = custom;
        }
        catch (Exception ex)
        {
            CrashLog.Write("LoadActionNames", ex);
        }
    }

    private static void SaveActionNames(IReadOnlyDictionary<int, string>? names = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ActionNamesPath)!);
            var source = names ?? Air.ReservedActions.Custom;
            var lines = new[] { ActionNamesComment }
                .Concat(source.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
            File.WriteAllLines(ActionNamesPath, lines);
        }
        catch (Exception ex)
        {
            CrashLog.Write("SaveActionNames", ex);
        }
    }

    private void OnEditActionNames(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Seed the file with the full effective table — built-in defaults plus any
            // override — on first open, so there's something to edit and the reserved
            // names are visible without guessing action numbers. The user edits it in
            // their own editor and reloads on next start (see LoadActionNames).
            if (!File.Exists(ActionNamesPath))
            {
                var merged = new Dictionary<int, string>(Air.ReservedActions.BuiltIn);
                foreach (var (n, name) in Air.ReservedActions.Custom) merged[n] = name;
                SaveActionNames(merged);
            }
            else
            {
                var lines = File.ReadAllLines(ActionNamesPath);
                if (lines.Length == 0 || lines[0] != ActionNamesComment)
                    File.WriteAllLines(ActionNamesPath, new[] { ActionNamesComment }.Concat(lines.Where(line => line != ActionNamesComment)));
            }
            Process.Start(new ProcessStartInfo(ActionNamesPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CrashLog.Write("OnEditActionNames", ex);
            ShowPopup("Action names file", $"Could not open the action names file: {ex.Message}");
        }
    }
}
