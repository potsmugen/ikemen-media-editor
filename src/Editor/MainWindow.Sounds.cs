using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Snd;

namespace Editor;

/// <summary>
/// The Sounds tab. A .snd is a container of whole WAV files, so nothing here
/// decodes audio: sounds are copied in and out untouched, and playback is handed
/// to the system rather than pulling an audio library into the project.
/// </summary>
public partial class MainWindow
{
    private static readonly FilePickerFileType SndType =
        new("Sound files") { Patterns = ["*.snd"] };
    private static readonly FilePickerFileType WavType =
        new("WAV audio") { Patterns = ["*.wav"] };

    private SndFile? _snd;
    private string? _sndPath;
    private bool _sndDirty;
    private List<int> _visibleSounds = [];
    private Process? _externalPlayer; // non-Windows fallback, so Stop has something to kill

    private SoundEntry? CurrentSound =>
        _snd is not null && SoundList.SelectedIndex >= 0 && SoundList.SelectedIndex < _visibleSounds.Count
            && _visibleSounds[SoundList.SelectedIndex] >= 0 && _visibleSounds[SoundList.SelectedIndex] < _snd.Sounds.Count
            ? _snd.Sounds[_visibleSounds[SoundList.SelectedIndex]]
            : null;

    private List<SoundEntry> GetSelectedSounds()
    {
        var result = new List<SoundEntry>();
        if (_snd is null || SoundList?.SelectedItems is null) return result;
        foreach (var item in SoundList.SelectedItems)
        {
            if (item is SoundRow row && _snd.Sounds.Contains(row.Sound))
                result.Add(row.Sound);
        }
        return result;
    }

    private bool HasMultipleSounds => GetSelectedSounds().Count > 1;

    // ---- file -------------------------------------------------------------

    /// <summary>Starts a blank .snd (no sounds) in place of whatever's open — the
    /// "start from scratch" counterpart to Open.</summary>
    private async void OnNewSnd(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!await ResolvePendingSoundEditsBeforeSwitch()) return;
            if (_sndDirty && !await Dialogs.Confirm(this, "Unsaved Sound Changes",
                    "The sound file has unsaved changes.", "Discard Them"))
                return;

            _snd = new SndFile();
            _sndPath = null;
            _sndDirty = false;
            _undo.MarkSaved(SndKind);
            _sndError = null;
            SoundFilterBox.Text = "";
            ApplySoundFilter();
            UpdateSndInfo();
            StatusText.Text = "New sound file. Add sounds, then Save.";
            SwitchToTab(3);
        }
        catch (Exception ex)
        {
            await Failed("OnNewSnd", ex);
        }
    }

    private async void OnOpenSnd(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!await ResolvePendingSoundEditsBeforeSwitch()) return;
            if (_sndDirty && !await Dialogs.Confirm(this, "Unsaved Sound Changes",
                    "The sound file has unsaved changes.", "Discard Them"))
                return;

            var files = await PickOpen(new FilePickerOpenOptions
            {
                Title = "Open Sound File",
                AllowMultiple = false,
                FileTypeFilter = [SndType],
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            SndFile? preloaded = null;
            try
            {
                preloaded = await RunFileWorkAsync($"Opening {Path.GetFileName(path)}…", () => SndFile.Open(path));
            }
            catch (Exception ex)
            {
                CrashLog.Write("LoadSnd", ex);
                _sndError = ex.Message;
            }
            if (preloaded is null || !LoadSnd(path, preloaded))
            {
                StatusText.Text = $"Could not open {Path.GetFileName(path)}.";
                await Dialogs.ShowMessage(this, "Could Not Read the Sound File", _sndError ?? "unknown problem");
                return;
            }
            SwitchToTab(3);
        }
        catch (Exception ex)
        {
            await Failed("OnOpenSnd", ex);
        }
    }

    private string? _sndError;

    /// <summary>Reads a .snd on a background thread under the busy lock. Null when it can't be read; LoadSnd then reports the error.</summary>
    private async Task<SndFile?> ReadSndInBackgroundAsync(string path)
    {
        try
        {
            return await RunFileWorkAsync($"Opening {Path.GetFileName(path)}…", () => SndFile.Open(path));
        }
        catch (Exception ex)
        {
            CrashLog.Write("LoadSnd", ex);
            return null;
        }
    }

    /// <summary>Loads a .snd and fills the list. Returns false on failure.</summary>
    internal bool LoadSnd(string path, SndFile? loaded = null)
    {
        try
        {
            _snd = loaded ?? SndFile.Open(path);
            _sndPath = path;
            _sndDirty = false;
            _undo.MarkSaved(SndKind);
            _sndError = null;
        }
        catch (Exception ex)
        {
            CrashLog.Write("LoadSnd", ex);
            _sndError = ex.Message;
            return false;
        }

        SoundFilterBox.Text = "";
        ApplySoundFilter();
        ShowSound(); // ApplySoundFilter selects row 0 with events suppressed, so nothing else shows it
        UpdateSndInfo();
        FocusTabList();
        return true;
    }

    private async void OnSaveSnd(object? sender, RoutedEventArgs e) => await SaveSnd(askPath: false);
    private async void OnSaveSndAs(object? sender, RoutedEventArgs e) => await SaveSnd(askPath: true);

    /// <summary>Save (askPath false: reuse the current path if any) or Save As.</summary>
    internal async Task SaveSnd(bool askPath)
    {
        try
        {
            if (_snd is null) return;

            string? savePath = _sndPath;
            if (savePath is null || !File.Exists(savePath) || askPath)
            {
                var file = await PickSave(new FilePickerSaveOptions
                {
                    Title = "Save Sounds",
                    DefaultExtension = "snd",
                    FileTypeChoices = [SndType],
                });
                if (file?.TryGetLocalPath() is not { } chosen) return;
                savePath = chosen;
            }
            if (await SaveSndWithRecoveryCheck(savePath)) StatusText.Text = $"Saved {Path.GetFileName(_sndPath!)}";
            else
            {
                StatusText.Text = $"Could not save {Path.GetFileName(savePath)}.";
                await Dialogs.ShowMessage(this, "Could Not Save the Sound File", _sndError ?? "unknown problem");
            }
        }
        catch (Exception ex)
        {
            await Failed("SaveSnd", ex);
        }
    }

    private async Task<bool> SaveSndWithRecoveryCheck(string? path)
    {
        if (!await ConfirmRecoveredSourceWrite(path)) return false;
        return await SaveSndTo(path);
    }

    /// <summary>Writes the .snd (Save and Save As), backing up the file it replaces.</summary>
    internal async Task<bool> SaveSndTo(string? path)
    {
        if (_snd is null || path is null) return false;
        string? recoverySource = _sndPath;
        try
        {
            var snd = _snd;
            int backupCount = _settings.BackupCount;
            await RunFileWriteAsync($"Saving {Path.GetFileName(path)}…", () =>
            {
                Backups.Make(path, backupCount);
                snd.Save(path);
            });
            _sndPath = path;
            _sndDirty = false;
            ResolveRecoveredItem("snd", recoverySource ?? path);
            _undo.MarkSaved(SndKind);
            UpdateSndInfo();
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("SaveSndTo", ex);
            _sndError = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The open .snd's summary lives in the status bar's corner, next to the
    /// .air's filename, rather than taking a line above the sound list — the
    /// list right below the filter box already makes clear something's loaded.
    /// </summary>
    private void UpdateSndInfo()
    {
        if (_snd is null || SndStatusText is null) return;

        int bad = _snd.Sounds.Count(s => s.Problem is not null);
        int duplicates = _snd.Duplicates().Count();
        var total = TimeSpan.FromSeconds(_snd.Sounds.Sum(s => s.Duration.TotalSeconds));

        SndStatusText.Text = (_sndDirty ? "* " : "")
            + $"{Path.GetFileName(_sndPath)}: {_snd.Sounds.Count} sounds, {total:mm\\:ss} total"
            + (bad > 0 ? $", {bad} unreadable" : "")
            + (duplicates > 0 ? $", {duplicates} duplicate keys" : "");
        UpdateTitle();
    }

    private void MarkSndDirty()
    {
        if (!_undo.Tag(SndKind)) _undo.Untracked(SndKind);
        _sndDirty = true;
        UpdateSndInfo();
        SyncSaveMenu();
    }

    // ---- pending edits (Paint Mode's model) --------------------------------

    private (byte[] Data, string? Problem, int Ch, int Rate, int Bits, ushort Format, TimeSpan Dur)? _soundEditBaseline;
    private SoundEntry? _soundEditSound;

    /// <summary>
    /// Captures the sound's state at the first pending edit; later edits on
    /// the same sound pile on without moving the baseline. The audio ops all
    /// replace Data with a fresh array rather than mutating it, so holding
    /// the references is enough — no clones.
    /// </summary>
    private void BeginSoundEditSession(SoundEntry s)
    {
        if (_soundEditBaseline is not null && ReferenceEquals(_soundEditSound, s)) return;
        _soundEditBaseline = (s.Data, s.Problem, s.Channels, s.SampleRate, s.BitsPerSample, s.FormatTag, s.Duration);
        _soundEditSound = s;
        if (SoundCommitButton is not null) SoundCommitButton.IsEnabled = true;
        if (SoundResetButton is not null) SoundResetButton.IsEnabled = true;
    }

    /// <summary>
    /// Applies one audio edit live under the pending-session model: the first
    /// edit on a sound captures the baseline, later ones pile on, and nothing
    /// is undoable until Commit Changes turns the whole session into one
    /// entry — or switching away asks to commit or drop it.
    /// </summary>
    private void ApplyPendingSoundEdit(SoundEntry s, byte[] data, Func<string> status)
    {
        BeginSoundEditSession(s);
        s.Data = data;
        s.Problem = null;
        SndReader.Describe(s);
        MarkSndDirty();
        AfterSoundEdit();
        StatusText.Text = status();
    }

    private void OnCommitSoundEdits(object? sender, RoutedEventArgs e) => CommitPendingSoundEdits();

    /// <summary>Reset: drops the pending session, like the palette sliders' Reset. No undo entry, since nothing was committed.</summary>
    private void OnResetSoundEdits(object? sender, RoutedEventArgs e)
    {
        if (_soundEditBaseline is null || _soundEditSound is not { } s) return;
        ClearSoundSelection();
        DropPendingSoundEdits();
        StatusText.Text = $"Reset {s.Group},{s.Index} to how it was before the pending edits.";
    }

    /// <summary>Turns the pending session into one real, undoable edit.</summary>
    private void CommitPendingSoundEdits()
    {
        if (_soundEditBaseline is not { } before || _soundEditSound is not { } s) return;
        _soundEditBaseline = null;
        _soundEditSound = null;
        if (SoundCommitButton is not null) SoundCommitButton.IsEnabled = false;
        if (SoundResetButton is not null) SoundResetButton.IsEnabled = false;

        var after = (Data: s.Data, Problem: s.Problem, Ch: s.Channels, Rate: s.SampleRate, Bits: s.BitsPerSample, Format: s.FormatTag, Dur: s.Duration);
        bool unchanged = ReferenceEquals(before.Data, after.Data)
            && before.Problem == after.Problem && before.Ch == after.Ch
            && before.Rate == after.Rate && before.Bits == after.Bits && before.Dur == after.Dur;
        if (unchanged) return; // nothing actually changed

        void Apply((byte[] Data, string? Problem, int Ch, int Rate, int Bits, ushort Format, TimeSpan Dur) v)
        {
            (s.Data, s.Problem, s.Channels, s.SampleRate, s.BitsPerSample, s.FormatTag, s.Duration) =
                (v.Data, v.Problem, v.Ch, v.Rate, v.Bits, v.Format, v.Dur);
            AfterSoundEdit();
        }

        // The session already applied itself live, so record the reversal
        // rather than re-applying — same pattern as the paint-commit entry.
        _undo.Record("edit sound", () => Apply(before), () => Apply(after));
        MarkSndDirty();
    }

    /// <summary>Reverts the pending session's changes and drops it — no undo entry, since nothing was ever committed.</summary>
    private void DropPendingSoundEdits()
    {
        if (_soundEditBaseline is not { } before || _soundEditSound is not { } s) return;
        (s.Data, s.Problem, s.Channels, s.SampleRate, s.BitsPerSample, s.FormatTag, s.Duration) = before;
        _soundEditBaseline = null;
        _soundEditSound = null;
        if (SoundCommitButton is not null) SoundCommitButton.IsEnabled = false;
        if (SoundResetButton is not null) SoundResetButton.IsEnabled = false;
        AfterSoundEdit();
    }

    /// <summary>
    /// Called before switching away from a sound (or the Sounds tab, or the
    /// file entirely) with pending uncommitted edits. True to proceed with
    /// the switch, false to stay put (the caller reverts whatever triggered
    /// this).
    /// </summary>
    private async Task<bool> ResolvePendingSoundEditsBeforeSwitch()
    {
        if (_soundEditBaseline is null) return true;
        var choice = await Dialogs.AskAboutPendingSoundEdits(this);
        switch (choice)
        {
            case PendingSoundEditChoice.Commit: CommitPendingSoundEdits(); return true;
            case PendingSoundEditChoice.Discard: DropPendingSoundEdits(); return true;
            default: return false;
        }
    }

    /// <summary>Sorts the packed sound entries by group and number, ascending. One undo step for the whole reorder.</summary>
    private void OnSortSoundsAscending(object? sender, RoutedEventArgs e)
    {
        if (_snd is null || _snd.Sounds.Count < 2) return;
        var file = _snd;
        var before = file.Sounds.ToList();
        var after = before.OrderBy(s => s.Group).ThenBy(s => s.Index).ToList();
        if (before.SequenceEqual(after)) return;

        void Apply(List<SoundEntry> order)
        {
            file.Sounds = [.. order];
            AfterSoundEdit();
        }

        _undo.Do("sort sounds", () => Apply(after), () => Apply(before));
        MarkSndDirty();
        StatusText.Text = "Sounds sorted by group and number.";
    }

    // ---- list -------------------------------------------------------------

    private void ApplySoundFilter()
    {
        if (_snd is null) return;

        var text = (SoundFilterBox.Text ?? "").Trim();
        var keep = CurrentSound;
        var selected = GetSelectedSounds();
        _visibleSounds = [];
        for (int i = 0; i < _snd.Sounds.Count; i++)
        {
            var s = _snd.Sounds[i];
            if (text.Length == 0 || $"{s.Group},{s.Index}".StartsWith(text) || $"{s.Group}".StartsWith(text))
                _visibleSounds.Add(i);
        }

        // Rebuilding the rows is an internal refresh, not a loss of focus.
        // Keep selected sounds by identity and silence transient selection changes
        // while ItemsSource is replaced; otherwise the list briefly selects row 0.
        _suppressEvents = true;
        // Widths over every sound, so filtering doesn't move the columns.
        var widths = TextTableWidths(SoundTableHeader, _snd.Sounds.Select((s, i) => new[]
        {
            (i + 1).ToString(CultureInfo.InvariantCulture), s.Group.ToString(CultureInfo.InvariantCulture),
            s.Index.ToString(CultureInfo.InvariantCulture),
            s.Problem is not null ? "unreadable" : $"{s.Duration.TotalSeconds:0.00}s",
            SoundRow.RateTextFor(s),
        }).ToList());

        // Most edits leave the same sounds on screen. Copy the cells into the rows already
        // there instead of replacing ItemsSource, which drops the list's scroll offset and
        // forces the selection back through the block below.
        if (SoundList.ItemsSource is List<SoundRow> existing && SameVisibleSounds(existing))
        {
            foreach (var row in existing) { row.Widths = widths; row.Refresh(); }
        }
        else
        {
            SoundList.ItemsSource = _visibleSounds.Select((i, row) => new SoundRow(_snd.Sounds[i], row + 1) { Widths = widths }).ToList();
            if (_visibleSounds.Count > 0)
            {
                // Visible row per sound, built once; IndexOf inside the loops below made select-all plus insert quadratic.
                var visibleRowOf = new Dictionary<SoundEntry, int>();
                for (int vi = 0; vi < _visibleSounds.Count; vi++) visibleRowOf.TryAdd(_snd.Sounds[_visibleSounds[vi]], vi);
                int keepRow = keep is null ? -1 : visibleRowOf.GetValueOrDefault(keep, -1);
                if (keepRow < 0)
                    keepRow = selected.Select(s => visibleRowOf.GetValueOrDefault(s, -1)).FirstOrDefault(row => row >= 0, -1);
                if (keepRow < 0) keepRow = 0;
                SoundList.SelectedIndex = keepRow;
                if (SoundList.SelectedItems is { } selectedItems && SoundList.Items is { } items)
                {
                    foreach (var s in selected)
                    {
                        int row = visibleRowOf.GetValueOrDefault(s, -1);
                        if (row >= 0 && row != keepRow && row < items.Count && items[row] is { } item)
                            selectedItems.Add(item);
                    }
                }
            }
            else SoundList.SelectedIndex = -1;
        }
        _suppressEvents = false;
        if (_visibleSounds.Count == 0) ShowSound();
    }

    /// <summary>True when the rows already handed to the list are the visible sounds, in order:
    /// nothing was added, removed, reordered or filtered since they were built.</summary>
    private bool SameVisibleSounds(List<SoundRow> rows)
    {
        if (_snd is null || rows.Count != _visibleSounds.Count) return false;
        for (int i = 0; i < rows.Count; i++)
            if (!ReferenceEquals(rows[i].Sound, _snd.Sounds[_visibleSounds[i]])) return false;
        return true;
    }

    private void OnSoundFilterChanged(object? sender, TextChangedEventArgs e) => ApplySoundFilter();

    private async void OnSoundNumberDoubleTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            e.Handled = true;
            if (_snd is null || sender is not Control c || c.DataContext is not SoundRow row) return;
            int at = _visibleSounds.IndexOf(_snd.Sounds.IndexOf(row.Sound));
            if (at < 0) return;

            if (_soundEditSound is { } pending && !ReferenceEquals(pending, row.Sound)
                && !await ResolvePendingSoundEditsBeforeSwitch())
                return;

            _suppressEvents = true;
            SoundList.SelectedIndex = at;
            _suppressEvents = false;
            ShowSound();
            PlayCurrent(loop: false);
        }
        catch (Exception ex)
        {
            await Failed("OnSoundNumberDoubleTapped", ex);
        }
    }

    private async void OnSoundSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        try
        {
            var target = CurrentSound;

            // One click only selects; a pending edit session follows its sound.
            // Switching away resolves it first (Paint Mode's rule), and canceling
            // puts the selection back where it was.
            // When multiple sounds are selected, skip the commit/discard popup.
            if (_soundEditSound is { } pending && !ReferenceEquals(pending, target) && !HasMultipleSounds)
            {
                if (!await ResolvePendingSoundEditsBeforeSwitch())
                {
                    _suppressEvents = true;
                    if (_snd is not null)
                    {
                        int row = _visibleSounds.IndexOf(_snd.Sounds.IndexOf(pending));
                        if (row >= 0) SoundList.SelectedIndex = row;
                    }
                    _suppressEvents = false;
                    return;
                }
            }

            ShowSound();
            UpdateSoundButtonStates();
        }
        catch (Exception ex)
        {
            // async void: an exception here has nowhere else to go — log it
            // explicitly rather than relying on it reaching AppDomain intact.
            CrashLog.Write("OnSoundSelected", ex);
            await Dialogs.ShowMessage(this, "Something Went Wrong", ex.Message);
        }
    }

    private void ShowSound()
    {
        if (CurrentSound is not { } s)
        {
            if (ConvertToMonoButton is not null) ConvertToMonoButton.IsEnabled = false;
            SoundTitle.Text = "No sound selected";
            SoundDetail.Text = "";
            SoundWarnings.Text = "";
            SoundSelectionText.Text = "";
            ClearSoundSelection();
            Waveform?.Show(null);
            return;
        }
        // When multiple sounds are selected, hide waveform and show summary
        if (HasMultipleSounds)
        {
            SoundTitle.Text = $"{GetSelectedSounds().Count} sounds selected";
            SoundDetail.Text = "";
            SoundWarnings.Text = "";
            SoundSelectionText.Text = "";
            ClearSoundSelection();
            Waveform?.Show(null);
            return;
        }
        // A selection belongs to one sound, and must still fit it.
        if (!ReferenceEquals(_soundSelectionOwner, s)
            || (_soundSelection is { } kept && kept.End > SndEdit.FrameCount(s)))
            ClearSoundSelection();

        if (ConvertToMonoButton is not null)
            ConvertToMonoButton.IsEnabled = s.Problem is null && s.Channels > 1;

        SoundTitle.Text = $"{s.Group},{s.Index}";
        SoundDetail.Text = SoundDetailText(s);
        Waveform?.Show(s.Problem is null ? s : null);
        if (Waveform is not null) Waveform.Selection = SelectionOn(s);
        SoundSelectionText.Text = SelectionText(s);

        var warnings = new List<string>();
        if (s.Problem is not null) warnings.Add($"This sound is {s.Problem}. The engine would skip it.");
        if (_snd!.Duplicates().Contains(s))
            warnings.Add("Another sound already uses this group and number; the engine keeps the first and ignores this one.");
        SoundWarnings.Text = string.Join("\n", warnings);
    }

    private void UpdateSoundButtonStates()
    {
        // When multiple sounds are selected, disable buttons that only work on a single sound.
        bool multi = HasMultipleSounds;

        // Playback buttons only work on a single sound
        if (SoundPlayButton is not null) SoundPlayButton.IsEnabled = !multi;
        if (SoundLoopButton is not null) SoundLoopButton.IsEnabled = !multi;
        if (SoundStopButton is not null) SoundStopButton.IsEnabled = !multi;

        // Trim to Selection needs a specific waveform selection
        if (SoundTrimToSelectionButton is not null) SoundTrimToSelectionButton.IsEnabled = !multi;
    }

    // ---- playing and moving sounds around ---------------------------------

    private void OnPlaySound(object? sender, RoutedEventArgs e) => PlayCurrent(loop: false);
    private void OnPlaySoundLoop(object? sender, RoutedEventArgs e) => PlayCurrent(loop: true);

    /// <summary>
    /// Plays the sound in-process on Windows via <see cref="WinSound"/>, so
    /// pressing Play does not launch a separate media app window and looping is
    /// possible. Elsewhere it hands the file to the OS's own player (afplay,
    /// paplay or aplay); those support neither in-process playback nor an easy
    /// stop, so looping there just replays once with a note about the limit.
    /// </summary>
    private async void PlayCurrent(bool loop)
    {
        try
        {
            if (CurrentSound is not { } s) return;
            if (s.Problem is not null)
            {
                await Dialogs.ShowMessage(this, "Cannot Play", $"This sound is {s.Problem}.");
                return;
            }

            try
            {
                if (loop && !OperatingSystem.IsWindows())
                    StatusText.Text = "Looping needs the in-process player, which is Windows only here; playing once.";
                var sel = SelectionOn(s);
                var data = sel is { } r ? SndEdit.ExtractRange(s, r.Start, r.End) ?? s.Data : s.Data;
                await PlayBytes(data, $"ime-{s.Group}_{s.Index}.wav", loop);
                if (ReferenceEquals(data, s.Data)) StartPlayhead(s, 0, SndEdit.FrameCount(s), loop);
                else StartPlayhead(s, sel!.Value.Start, sel.Value.End - sel.Value.Start, loop);
            }
            catch (Exception ex)
            {
                CrashLog.Write("PlayCurrent", ex);
                await Dialogs.ShowMessage(this, "Could Not Play the Sound",
                    $"{ex.Message}\n\nYou can still use Export .wav and play it with any player.");
            }
        }
        catch (Exception ex)
        {
            await Failed("PlayCurrent", ex);
        }
    }

    /// <summary>Plays WAV bytes: in-process on Windows, otherwise through the OS player via a temp file (loop is Windows only). Throws on failure.</summary>
    private async Task PlayBytes(byte[] data, string tempFileName, bool loop)
    {
        StopPlayhead(); // whatever played before is cut off
        if (OperatingSystem.IsWindows())
        {
            WinSound.Play(data, loop); // asynchronous; doesn't block the UI thread
            return;
        }

        // This run's own temp folder (under data/recovery), removed when the editor closes.
        Directory.CreateDirectory(RecoveryStore.TempDirectory);
        var temp = Path.Combine(RecoveryStore.TempDirectory, tempFileName);
        await File.WriteAllBytesAsync(temp, data);

        string player = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "afplay" : "paplay";
        try
        {
            _externalPlayer = Process.Start(new ProcessStartInfo(player, $"\"{temp}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            // paplay is PulseAudio-specific; ALSA-only systems have aplay instead.
            if (player == "paplay")
            {
                CrashLog.Write("PlayBytes.paplay", ex);
                _externalPlayer = Process.Start(new ProcessStartInfo("aplay", $"\"{temp}\"") { UseShellExecute = false });
            }
            else throw;
        }
    }

    private void OnStopSound(object? sender, RoutedEventArgs e) => StopPlayback();

    private void StopPlayback()
    {
        StopPlayhead();
        if (OperatingSystem.IsWindows())
        {
            WinSound.Stop();
            return;
        }
        try { if (_externalPlayer is { HasExited: false } p) p.Kill(); }
        catch (Exception ex)
        {
            // The process may have already exited on its own.
            CrashLog.Write("StopPlayback", ex);
        }
    }

    // Playhead: the players report no position, so it's timed from when playback was requested,
    // held back by PlaybackLatencyMilliseconds (settings.json) for the delay before it's heard.
    private DispatcherTimer? _playheadClock;
    private double _playheadLatencySeconds;
    private readonly Stopwatch _playheadWatch = new();
    private (SoundEntry Sound, int Start, int Frames, bool Loop)? _playing;

    /// <summary>Starts moving the waveform playhead over frames [start, start + frames) of s, wrapping if loop.</summary>
    private void StartPlayhead(SoundEntry s, int start, int frames, bool loop)
    {
        StopPlayhead();
        if (Waveform is null || frames <= 0 || s.SampleRate <= 0) return;
        _playing = (s, start, frames, loop);
        _playheadLatencySeconds = Settings.Checked(nameof(Settings.PlaybackLatencyMilliseconds), _settings.PlaybackLatencyMilliseconds,
            Settings.DefaultPlaybackLatencyMilliseconds, v => double.IsFinite(v) && v >= 0 && v <= 2000) / 1000;
        _playheadWatch.Restart();
        _playheadClock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _playheadClock.Tick += (_, _) => UpdatePlayhead();
        _playheadClock.Start();
        UpdatePlayhead();
    }

    private void UpdatePlayhead()
    {
        if (_playing is not { } p || Waveform is null) { StopPlayhead(); return; }
        // Another sound shown, or the OS player (non-Windows) quit early: nothing to follow.
        if (!ReferenceEquals(CurrentSound, p.Sound)
            || (!OperatingSystem.IsWindows() && _externalPlayer is { HasExited: true }))
        {
            StopPlayhead();
            return;
        }
        double played = Math.Max(0, _playheadWatch.Elapsed.TotalSeconds - _playheadLatencySeconds) * p.Sound.SampleRate;
        if (!p.Loop && played >= p.Frames) { StopPlayhead(); return; }
        Waveform.Playhead = (p.Start, p.Start + played % p.Frames);
    }

    private void StopPlayhead()
    {
        _playheadClock?.Stop();
        _playheadClock = null;
        _playing = null;
        if (Waveform is not null) Waveform.Playhead = null;
    }

    /// <summary>
    /// Sound-edit prompt with Preview. validate: an error for unusable text, or null.
    /// render: the bytes to hear for valid text (the sound's own Data when the value
    /// changes nothing), or null if the PCM couldn't be read. Previews render in the
    /// background (the dialog is modal, so the sound can't change meanwhile); clicking
    /// Preview again restarts, and closing the dialog cancels a render and stops playback.
    /// Returns the accepted text plus the finished preview's bytes when the text (and
    /// stateKey, for extra controls) is exactly what was previewed (same input, same
    /// output), so OK needn't redo it. render runs on the UI thread and returns the job
    /// that runs in the background, so it must read any control state before returning.
    /// initial null hides the text box; extra goes under it.
    /// </summary>
    private async Task<(string Text, byte[]? Reused)?> PromptSoundEdit(SoundEntry s, string title, string label,
        string? initial, Func<string, string?> validate, Func<string, Func<CancellationToken, byte[]?>> render,
        Control? extra = null, Func<string>? stateKey = null, Func<string, string?>? warn = null)
    {
        bool previewed = false;
        CancellationTokenSource? running = null;
        (string Text, byte[] Bytes)? finished = null;
        string verb = title.ToLowerInvariant();

        var text = await Dialogs.PromptTextWithPreview(this, title, label, initial, validate,
            async raw =>
            {
                running?.Cancel();
                finished = null;
                if (validate(raw) is { } error) return error;

                var cts = new CancellationTokenSource();
                running = cts;
                string key = raw.Trim() + "\n" + stateKey?.Invoke();
                var job = render(raw);
                CrashLog.Trace($"[sound] {verb} preview start: {s.Group},{s.Index}, \"{key}\"");
                byte[]? bytes;
                try
                {
                    bytes = await Task.Run(() => job(cts.Token), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    CrashLog.Trace($"[sound] {verb} preview canceled: {s.Group},{s.Index}, \"{key}\"");
                    return null;
                }
                if (cts.IsCancellationRequested) return null; // restarted or closed while it finished
                running = null;
                if (bytes is null)
                {
                    CrashLog.Error(title, $"preview of sound {s.Group},{s.Index}: PCM data couldn't be read");
                    return "Can't preview: this sound's PCM data couldn't be read.";
                }

                finished = (key, bytes);
                try
                {
                    await PlayBytes(bytes, $"ime-{s.Group}_{s.Index}-preview.wav", loop: false);
                    previewed = true;
                }
                catch (Exception ex)
                {
                    CrashLog.Write("PromptSoundEdit", ex);
                    return $"Couldn't play the preview: {ex.Message}";
                }
                return null;
            },
            () =>
            {
                running?.Cancel();
                if (previewed) StopPlayback();
            }, extra, warn);

        if (text is null) return null;
        string okKey = text.Trim() + "\n" + stateKey?.Invoke();
        return (text, finished is { } f && f.Text == okKey ? f.Bytes : null);
    }

    private async void OnExportSound(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (CurrentSound is not { } s) return;

            var file = await PickSave(new FilePickerSaveOptions
            {
                Title = "Export Sound",
                SuggestedFileName = $"{s.Group}_{s.Index}.wav",
                DefaultExtension = "wav",
                FileTypeChoices = [WavType],
            });
            if (file?.TryGetLocalPath() is not { } path) return;

            File.WriteAllBytes(path, s.Data);
            StatusText.Text = $"Wrote {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            await Failed("OnExportSound", ex);
        }
    }

    /// <summary>Writes every sound as WAV beside a manifest .txt that Import All reads back.</summary>
    private async void OnExportAllSounds(object? sender, RoutedEventArgs e)
    {
        if (_snd is null) return;
        try
        {
            string suggested = _sndPath is null ? "export" : Path.GetFileNameWithoutExtension(_sndPath);
            var file = await PickSave(new FilePickerSaveOptions
            {
                Title = "Export Sounds - Choose the Manifest .txt",
                SuggestedFileName = $"{suggested}.txt",
                DefaultExtension = "txt",
                FileTypeChoices = [TxtType],
            });
            if (file?.TryGetLocalPath() is not { } txtPath) return;
            if (Path.GetDirectoryName(txtPath) is not { } dir) return;
            string prefix = SanitizeFileName(Path.GetFileNameWithoutExtension(txtPath));
            var duplicateKeys = _snd.Sounds.GroupBy(s => (s.Group, s.Index)).Where(g => g.Count() > 1).ToList();
            if (duplicateKeys.Count > 0)
            {
                var keys = string.Join(", ", duplicateKeys.Select(g => $"{g.Key.Group},{g.Key.Index}"));
                CrashLog.Error("OnExportAllSounds", $"Cannot export duplicate sound keys: {keys}");
                await Dialogs.ShowMessage(this, "Cannot Export Duplicate Sound Keys", keys);
                return;
            }

            int written = 0;
            var entries = new List<ExportManifest.Entry>();
            foreach (var s in _snd.Sounds)
            {
                string name = $"{prefix}_{s.Group}_{s.Index}.wav";
                File.WriteAllBytes(Path.Combine(dir, name), s.Data);
                entries.Add(new("sound", s.Group, s.Index, name));
                written++;
            }
            File.WriteAllText(txtPath, ExportManifest.Write(entries));
            CrashLog.Trace($"[bulk] exported {written} sounds to {dir}");

            StatusText.Text = $"Exported {written} sounds to {dir}";
        }
        catch (Exception ex)
        {
            CrashLog.Write("OnExportAllSounds", ex);
            await Dialogs.ShowMessage(this, "Export Failed", $"Export failed: {ex.Message} (see crash.log)");
        }
    }

    /// <summary>Imports an export manifest (.txt): sounds matching an existing Group,Index are
    /// overwritten (after a confirm), the rest are added. One undo step.</summary>
    private async void OnImportAllSounds(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_snd is null || !await ResolvePendingSoundEditsBeforeSwitch()) return;
            try
            {
                var picked = await PickOpen(new FilePickerOpenOptions
                {
                    Title = "Import Sounds - Choose the Manifest .txt",
                    FileTypeFilter = [TxtType],
                });
                if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } txtPath) return;
                if (Path.GetDirectoryName(txtPath) is not { } dir) return;

                var (entries, errors) = ExportManifest.Read(File.ReadAllText(txtPath));
                var unexpectedTypes = entries.Where(x => x.Type != "sound").Select(x => x.Type).Distinct().ToList();
                var duplicateKeys = entries.GroupBy(x => (x.Group, x.Index)).Where(g => g.Count() > 1).ToList();
                if (duplicateKeys.Count > 0)
                {
                    var message = string.Join(", ", duplicateKeys.Select(g => $"{g.Key.Group},{g.Key.Index}"));
                    CrashLog.Error("ImportAllSounds", $"{txtPath}: duplicate sound keys: {message}");
                    await Dialogs.ShowMessage(this, "Duplicate Sound Entries", $"The manifest repeats these sound keys: {message}");
                    return;
                }
                if (unexpectedTypes.Count > 0)
                {
                    LogBulkProblems("ImportAllSounds", errors, []);
                    CrashLog.Error("ImportAllSounds", $"{txtPath}: unexpected entry types: {string.Join(", ", unexpectedTypes)}");
                    await Dialogs.ShowMessage(this, "Not a Sound Export", "This manifest contains entries that aren't sounds.");
                    return;
                }

                var file = _snd;
                SoundEntry? Find(int g, int i) => file.Sounds.FirstOrDefault(x => x.Group == g && x.Index == i);
                int existing = entries.Count(x => x.Type == "sound" && Find(x.Group, x.Index) is not null);
                if (existing > 0 && !await Dialogs.Confirm(this, "Import",
                        $"{existing} sound(s) in this manifest already exist in the open file and will be overwritten. " +
                        "Sounds that don't exist yet will be added.", "Overwrite"))
                    return;
                var soundsBefore = file.Sounds.ToList();
                var changes = new List<(SoundEntry Sound,
                    (byte[] Data, string? Problem, int Ch, int Rate, int Bits, ushort Format, TimeSpan Dur) Before,
                    (byte[] Data, string? Problem, int Ch, int Rate, int Bits, ushort Format, TimeSpan Dur) After)>();
                var additions = new List<SoundEntry>();

                foreach (var entry in entries)
                {
                    string path = Path.Combine(dir, entry.File);
                    if (!File.Exists(path)) { errors.Add($"{entry.File}: file not found"); continue; }
                    var fresh = new SoundEntry { Group = entry.Group, Index = entry.Index, Data = File.ReadAllBytes(path) };
                    SndReader.Describe(fresh);
                    var s = Find(entry.Group, entry.Index);
                    if (s is null)
                    {
                        additions.Add(fresh);
                        continue;
                    }

                    var before = (s.Data, s.Problem, s.Channels, s.SampleRate, s.BitsPerSample, s.FormatTag, s.Duration);
                    changes.Add((s, before, (fresh.Data, fresh.Problem, fresh.Channels, fresh.SampleRate,
                        fresh.BitsPerSample, fresh.FormatTag, fresh.Duration)));
                }
                int added = additions.Count;
                LogBulkProblems("ImportAllSounds", errors, []);
                CrashLog.Trace($"[bulk] sound import: {changes.Count} replaced, {added} added, {errors.Count} errors");
                foreach (var (sound, _, after) in changes)
                    (sound.Data, sound.Problem, sound.Channels, sound.SampleRate, sound.BitsPerSample, sound.FormatTag, sound.Duration) = after;
                file.Sounds.AddRange(additions);
                var soundsAfter = file.Sounds.ToList();

                if (changes.Count == 0 && added == 0)
                {
                    StatusText.Text = "Nothing imported.";
                    if (errors.Count > 0)
                        await Dialogs.ShowMessage(this, "Some Entries Could Not Be Imported", SummarizeBulkErrors(errors));
                    return;
                }

                void Apply(bool useAfter)
                {
                    file.Sounds.Clear();
                    file.Sounds.AddRange(useAfter ? soundsAfter : soundsBefore);
                    foreach (var (s, before, after) in changes)
                    {
                        var v = useAfter ? after : before;
                        (s.Data, s.Problem, s.Channels, s.SampleRate, s.BitsPerSample, s.FormatTag, s.Duration) = v;
                    }
                    AfterSoundEdit();
                }

                _undo.Record($"import {changes.Count + added} sound(s)", () => Apply(false), () => Apply(true));
                MarkSndDirty();
                AfterSoundEdit();

                string summary = $"Sounds: {changes.Count} replaced, {added} added";
                if (errors.Count > 0) summary += $"; {errors.Count} error(s)";
                StatusText.Text = summary;
                if (errors.Count > 0)
                    await Dialogs.ShowMessage(this, "Some Entries Could Not Be Imported", SummarizeBulkErrors(errors));
            }
            catch (Exception ex)
            {
                CrashLog.Write("OnImportAllSounds", ex);
                await Dialogs.ShowMessage(this, "Could Not Import", $"{ex.Message}\n\nDetails were written to {CrashLog.Path}");
            }
        }
        catch (Exception ex)
        {
            await Failed("OnImportAllSounds", ex);
        }
    }

    private async Task<(byte[] Bytes, string Name)?> PickWav(string title)
    {
        var files = await PickOpen(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [WavType],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } p) return null;
        return (File.ReadAllBytes(p), Path.GetFileName(p));
    }

    /// <summary>
    /// Duplicate: a copy as it sounds now (pending edits included), same group, next free
    /// number, right after the original; one undo step. The original stays selected. The
    /// WAV bytes are shared: edits always replace Data with a new array, never mutate it.
    /// </summary>
    private void OnDuplicateSound(object? sender, RoutedEventArgs e)
    {
        if (_snd is null || CurrentSound is not { } s) return;
        var copy = new SoundEntry { Group = s.Group, Data = s.Data };
        copy.Index = NextFreeSoundIndex(copy.Group);
        SndReader.Describe(copy);

        var file = _snd;
        int at = file.Sounds.IndexOf(s) + 1;
        _undo.Record("duplicate sound",
            () => { file.Sounds.Remove(copy); AfterSoundEdit(); },
            () => { file.Sounds.Insert(Math.Min(at, file.Sounds.Count), copy); AfterSoundEdit(); });
        file.Sounds.Insert(at, copy);
        MarkSndDirty();
        AfterSoundEdit();
        CrashLog.Trace($"[sound] duplicate {s.Group},{s.Index} -> {copy.Group},{copy.Index}");
        StatusText.Text = $"Duplicated {s.Group},{s.Index} as {copy.Group},{copy.Index}.";
    }

    private async void OnAddSound(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_snd is null) return;
            if (await PickWav("Add sound from .wav") is not { } wav) return;

            var entry = new SoundEntry
            {
                Data = wav.Bytes,
                Group = CurrentSound?.Group ?? 0,
            };
            entry.Index = NextFreeSoundIndex(entry.Group);
            SndReader.Describe(entry);

            var file = _snd;
            int at = CurrentSound is { } cur ? file.Sounds.IndexOf(cur) + 1 : file.Sounds.Count;

            _undo.Record("add sound",
                () => { file.Sounds.Remove(entry); AfterSoundEdit(); },
                () => { file.Sounds.Insert(Math.Min(at, file.Sounds.Count), entry); AfterSoundEdit(); });
            file.Sounds.Insert(at, entry);
            MarkSndDirty();
            AfterSoundEdit();

            StatusText.Text = entry.Problem is null
                ? $"Added {entry.Group},{entry.Index} from {wav.Name}."
                : $"Added {wav.Name}, but its header is {entry.Problem}.";
        }
        catch (Exception ex)
        {
            await Failed("OnAddSound", ex);
        }
    }

    private async void OnReplaceSound(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (CurrentSound is not { } s) return;
            if (await PickWav($"Replace {s.Group},{s.Index}") is not { } wav) return;

            var before = (s.Data, s.Problem, s.Channels, s.SampleRate, s.BitsPerSample, s.FormatTag, s.Duration);
            s.Data = wav.Bytes;
            s.Problem = null;
            SndReader.Describe(s);
            var after = (s.Data, s.Problem, s.Channels, s.SampleRate, s.BitsPerSample, s.FormatTag, s.Duration);

            void Apply((byte[] Data, string? Problem, int Ch, int Rate, int Bits, ushort Format, TimeSpan Dur) v)
            {
                (s.Data, s.Problem, s.Channels, s.SampleRate, s.BitsPerSample, s.FormatTag, s.Duration) =
                    (v.Data, v.Problem, v.Ch, v.Rate, v.Bits, v.Format, v.Dur);
                AfterSoundEdit();
            }

            _undo.Record("replace sound", () => Apply(before), () => Apply(after));
            MarkSndDirty();
            AfterSoundEdit();
            StatusText.Text = $"Replaced {s.Group},{s.Index} from {wav.Name}.";
        }
        catch (Exception ex)
        {
            await Failed("OnReplaceSound", ex);
        }
    }

    private async void OnDeleteSound(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_snd is not { } file) return;
            var sounds = GetSelectedSounds();
            if (sounds.Count == 0 && CurrentSound is { } current) sounds.Add(current);
            if (sounds.Count == 0) return;

            string names = string.Join(", ", sounds.Take(3).Select(sound => $"{sound.Group},{sound.Index}"))
                + (sounds.Count > 3 ? ", …" : "");
            string message = sounds.Count == 1 ? $"Delete {names}?" : $"Delete {sounds.Count} sounds ({names})?";
            if (!await Dialogs.Confirm(this, sounds.Count == 1 ? "Delete sound" : "Delete sounds", message, "Delete")
                || !ReferenceEquals(_snd, file) || sounds.Any(sound => !file.Sounds.Contains(sound))) return;

            var positions = sounds.Select(sound => file.Sounds.IndexOf(sound)).ToList();
            _undo.Record(sounds.Count == 1 ? "delete sound" : "delete sounds",
                () =>
                {
                    foreach (int i in Enumerable.Range(0, sounds.Count).OrderBy(i => positions[i]))
                        file.Sounds.Insert(Math.Min(positions[i], file.Sounds.Count), sounds[i]);
                    AfterSoundEdit();
                },
                () =>
                {
                    foreach (var sound in sounds.OrderByDescending(file.Sounds.IndexOf)) file.Sounds.Remove(sound);
                    AfterSoundEdit();
                });
            foreach (var sound in sounds.OrderByDescending(file.Sounds.IndexOf)) file.Sounds.Remove(sound);
            MarkSndDirty();
            AfterSoundEdit();
        }
        catch (Exception ex)
        {
            await Failed("OnDeleteSound", ex);
        }
    }

    private void SelectAllSounds()
    {
        CrashLog.Trace("SelectAllSounds: start");
        if (SoundList is not null && SoundList.SelectedItems is not null)
        {
            // See SelectAllSprites: select in one batched operation, not one Add per row. AutoScrollToSelectedItem
            // is off on SoundList, so the view never relocates on a selection change.
            _suppressEvents = true;
            try
            {
                SoundList.SelectAll();
                CrashLog.Trace("SelectAllSounds: all items selected");
            }
            finally
            {
                _suppressEvents = false;
                UpdateSoundButtonStates();
                CrashLog.Trace("SelectAllSounds: done");
            }
        }
    }

    private void InvertSoundSelection()
    {
        CrashLog.Trace("InvertSoundSelection: start");
        if (SoundList is not null && SoundList.SelectedItems is not null)
        {
            // See InvertSpriteSelection: clear and re-add (don't assign a fresh list). AutoScrollToSelectedItem
            // is off on the list, so the view doesn't relocate.
            _suppressEvents = true;
            try
            {
                var selectedItems = new HashSet<object?>(SoundList.SelectedItems.Cast<object?>());
                SoundList.SelectedItems.Clear();
                CrashLog.Trace($"InvertSoundSelection: clearing done, inverting {SoundList.Items.Count} items");
                for (int i = 0; i < SoundList.Items.Count; i++)
                {
                    var item = SoundList.Items[i];
                    if (item is not null && !selectedItems.Contains(item))
                        SoundList.SelectedItems.Add(item);
                }
                CrashLog.Trace("InvertSoundSelection: done");
            }
            finally
            {
                _suppressEvents = false;
                UpdateSoundButtonStates();
            }
        }
    }

    private void OnTrimSilence(object? sender, RoutedEventArgs e)
    {
        if (CurrentSound is not { } s) return;
        if (s.Problem is not null) { StatusText.Text = "Can't trim: this sound's header couldn't be read."; return; }

        if (SndEdit.TrimSilence(s, _settings.SilenceThreshold) is not { } trimmed)
        {
            StatusText.Text = "Nothing to trim — already tight, or entirely silent.";
            return;
        }

        double beforeSeconds = s.Duration.TotalSeconds;
        ApplyWholeEdit(s, trimmed,
            () => $"Trimmed {s.Group},{s.Index}: {beforeSeconds:0.00}s -> {s.Duration.TotalSeconds:0.00}s.");
    }

    /// <summary>Insert Silence: pads the whole sound with a length in ms of leading silence, trailing silence, or both.</summary>
    private async void OnInsertSilence(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (CurrentSound is not { } s) return;
            if (s.Problem is not null || s.SampleRate <= 0)
            {
                CrashLog.Error("OnInsertSilence", $"sound {s.Group},{s.Index}: WAV header problem: {s.Problem}");
                StatusText.Text = "Can't insert silence: this sound's header couldn't be read.";
                return;
            }

            double maxMs = Settings.Checked(nameof(Settings.InsertSilenceMaxMilliseconds), _settings.InsertSilenceMaxMilliseconds,
                Settings.DefaultInsertSilenceMaxMilliseconds, v => double.IsFinite(v) && v > 0);
            double defaultMs = Settings.Checked(nameof(Settings.InsertSilenceMilliseconds), _settings.InsertSilenceMilliseconds,
                Settings.DefaultInsertSilenceMilliseconds, v => double.IsFinite(v) && v > 0 && v <= maxMs);
            string[] places = ["Leading (before the start)", "Trailing (after the end)", "Leading and trailing"];
            var where = new ComboBox { ItemsSource = places, SelectedIndex = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
            var extra = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Where:" }, where } };

            string? Parse(string raw, out int frames)
            {
                frames = 0;
                string t = raw.Trim();
                if (t.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) t = t[..^2].Trim();
                if ((!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms)
                        && !double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out ms))
                    || !double.IsFinite(ms) || ms <= 0)
                    return $"\"{t}\" isn't a length in ms.";
                if (ms > maxMs) return $"Up to {maxMs:N0} ms at a time.";
                frames = Math.Max(1, (int)Math.Round(ms * s.SampleRate / 1000));
                return null;
            }

            // Index 0 = leading only, 1 = trailing only, 2 = both.
            byte[]? Make(int frames, int place) =>
                SndEdit.InsertSilence(s, place != 1 ? frames : 0, place != 0 ? frames : 0);

            if (await PromptSoundEdit(s, "Insert Silence", "Silence length in ms:", defaultMs.ToString("0.##", CultureInfo.InvariantCulture),
                    raw => Parse(raw, out _),
                    raw =>
                    {
                        Parse(raw, out int f);
                        int p = where.SelectedIndex;
                        return _ => Make(f, p);
                    },
                    extra, () => where.SelectedIndex.ToString()) is not { } answer) return;
            if (!ReferenceEquals(CurrentSound, s)) return; // selection changed while the dialog was open

            Parse(answer.Text, out int frames);
            int place = where.SelectedIndex;
            if ((answer.Reused ?? Make(frames, place)) is not { } padded)
            {
                CrashLog.Error("OnInsertSilence", $"sound {s.Group},{s.Index}: PCM data couldn't be read");
                StatusText.Text = "Can't insert silence: this sound's header couldn't be read.";
                return;
            }
            double ms = frames * 1000.0 / s.SampleRate, before = s.Duration.TotalSeconds;
            string kind = place switch { 0 => "leading", 1 => "trailing", _ => "leading and trailing" };
            CrashLog.Trace($"[sound] insert silence {s.Group},{s.Index}: {ms:0} ms, {kind}");
            ApplyWholeEdit(s, padded,
                () => $"Added {ms:0} ms of {kind} silence to {s.Group},{s.Index}: {before:0.00}s -> {s.Duration.TotalSeconds:0.00}s.");
        }
        catch (Exception ex)
        {
            await Failed("OnInsertSilence", ex);
        }
    }

    private void OnMaximizeVolume(object? sender, RoutedEventArgs e)
    {
        if (CurrentSound is not { } s) return;
        if (s.Problem is not null) { StatusText.Text = "Can't maximize: this sound's header couldn't be read."; return; }

        var sel = SelectionOn(s);
        float gain = 1f;
        if (EditRangeOrWhole(s, sel, SelectionCrossfadeFrames(s), part => SndEdit.MaximizeVolume(part, out gain)) is not { } louder)
        {
            StatusText.Text = "Nothing to maximize — already at full volume, or entirely silent.";
            return;
        }

        ApplyRangeEdit(s, louder, sel, () => $"Maximized {s.Group},{s.Index}{(sel is null ? "" : " (selection)")} to full volume (×{gain:0.##}).");
    }

    private async void OnMaximizeLoudness(object? sender, RoutedEventArgs e)
    {
        try
        {
            await IncreaseLoudness();
        }
        catch (Exception ex)
        {
            await Failed("OnMaximizeLoudness", ex);
        }
    }

    private async Task IncreaseLoudness(List<SoundEntry> targets)
    {
        if (_snd is null || _busy || !await ResolvePendingSoundEditsBeforeSwitch()) return;
        var file = _snd;
        double defaultAmount = Settings.Checked(nameof(Settings.LoudnessDefaultAmount), _settings.LoudnessDefaultAmount,
            SndEdit.DefaultLoudnessAmount, v => v >= SndEdit.MinLoudnessAmount && v <= SndEdit.MaxLoudnessAmount);
        string? text = await Dialogs.PromptText(this, "Increase Loudness",
            $"Amount ({SndEdit.MinLoudnessAmount:0}-{SndEdit.MaxLoudnessAmount:0}; higher lifts quiet parts more, each sound's peak stays unchanged):",
            defaultAmount.ToString("0.##", CultureInfo.InvariantCulture));
        if (text is null) return;
        string raw = text.Trim();
        if ((!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount)
                && !double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out amount))
            || !double.IsFinite(amount) || amount < SndEdit.MinLoudnessAmount || amount > SndEdit.MaxLoudnessAmount)
        {
            StatusText.Text = $"\"{raw}\" isn't an amount between {SndEdit.MinLoudnessAmount:0} and {SndEdit.MaxLoudnessAmount:0}.";
            return;
        }
        if (!ReferenceEquals(_snd, file)) return;

        var changes = new List<(SoundEntry Sound,
            (byte[] Data, string? Problem, int Ch, int Rate, int Bits, ushort Format, TimeSpan Dur) Before,
            (byte[] Data, string? Problem, int Ch, int Rate, int Bits, ushort Format, TimeSpan Dur) After)>();
        var skipped = new List<string>();
        if (!TryBeginBusy()) return;
        try
        {
            for (int n = 0; n < targets.Count; n++)
            {
                var s = targets[n];
                await BusyStep("Increasing loudness", n, targets.Count, $"{s.Group},{s.Index}");
                if (!ReferenceEquals(_snd, file))
                {
                    StatusText.Text = "Increase loudness stopped — the file was closed.";
                    return;
                }
                if (!file.Sounds.Contains(s)) continue;
                var before = (s.Data, s.Problem, s.Channels, s.SampleRate, s.BitsPerSample, s.FormatTag, s.Duration);
                var data = SndEdit.IncreaseLoudness(s, amount, out LoudnessResult result, out string? reason);
                if (data is null)
                {
                    string why = reason switch
                    {
                        "silent" => "silent",
                        "already-loud" => "already unchanged",
                        "format" => "unsupported format",
                        "header" => "unreadable WAV header",
                        "below-gate" => "no measurable loudness",
                        _ => reason ?? "could not be processed",
                    };
                    skipped.Add($"{s.Group},{s.Index}: {why}");
                    continue;
                }

                var after = new SoundEntry { Data = data };
                SndReader.Describe(after);
                changes.Add((s, before, (data, after.Problem, after.Channels, after.SampleRate, after.BitsPerSample, after.FormatTag, after.Duration)));
                CrashLog.Trace($"[sound] loudness curve complete: {s.Group},{s.Index}, {result.InputLufs:0.#} -> {result.OutputLufs:0.#} LUFS");
            }

            foreach (var item in skipped) CrashLog.Error("IncreaseLoudnessAll", item);
            if (changes.Count == 0)
            {
                StatusText.Text = skipped.Count == 0 ? "Nothing to boost." : $"No sounds changed; {skipped.Count} skipped (see crash.log).";
                return;
            }

            void Apply(bool useAfter)
            {
                foreach (var (s, before, after) in changes)
                {
                    var v = useAfter ? after : before;
                    (s.Data, s.Problem, s.Channels, s.SampleRate, s.BitsPerSample, s.FormatTag, s.Duration) = v;
                }
                AfterSoundEdit();
            }

            foreach (var (s, _, after) in changes)
                (s.Data, s.Problem, s.Channels, s.SampleRate, s.BitsPerSample, s.FormatTag, s.Duration) = after;
            _undo.Record($"increase loudness on {changes.Count} sound(s)", () => Apply(false), () => Apply(true));
            MarkSndDirty();
            AfterSoundEdit();
            StatusText.Text = $"Increased loudness on {changes.Count} sound(s)" + (skipped.Count > 0 ? $"; {skipped.Count} skipped (see crash.log)." : ".");
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>Asks for an amount (1-100), then pulls quiet samples up toward the sound's own peak with a curve.</summary>
    private async Task IncreaseLoudness()
    {
        var selected = GetSelectedSounds();
        if (selected.Count > 1)
        {
            await IncreaseLoudness(selected);
            return;
        }
        if (CurrentSound is not { } s) return;
        if (s.Problem is not null)
        {
            CrashLog.Error("IncreaseLoudness", $"sound {s.Group},{s.Index}: WAV header problem: {s.Problem}");
            StatusText.Text = "Can't increase loudness: this sound's header couldn't be read.";
            return;
        }

        static string? Parse(string raw, out double amount)
        {
            string t = raw.Trim();
            if ((!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out amount)
                    && !double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out amount))
                || !double.IsFinite(amount) || amount < SndEdit.MinLoudnessAmount || amount > SndEdit.MaxLoudnessAmount)
                return $"\"{t}\" isn't an amount between {SndEdit.MinLoudnessAmount:0} and {SndEdit.MaxLoudnessAmount:0}.";
            return null;
        }

        var sel = SelectionOn(s);
        int crossfade = SelectionCrossfadeFrames(s);
        double defaultAmount = Settings.Checked(nameof(Settings.LoudnessDefaultAmount), _settings.LoudnessDefaultAmount,
            SndEdit.DefaultLoudnessAmount, v => v >= SndEdit.MinLoudnessAmount && v <= SndEdit.MaxLoudnessAmount);
        if (await PromptSoundEdit(s, "Increase Loudness",
                "Amount (1-100; higher lifts quiet parts more, the peak never changes):",
                defaultAmount.ToString("0.##", CultureInfo.InvariantCulture),
                raw => Parse(raw, out _),
                raw =>
                {
                    Parse(raw, out double a);
                    // Silent or already loud enough: the result is the sound as it is.
                    // Named, not _: a lone `_` lambda parameter is a real parameter, so `out _` below would bind to it.
                    return cancel => EditRangeOrWhole(s, sel, crossfade, part =>
                        SndEdit.IncreaseLoudness(part, a, out _, out string? why)
                        ?? (why is "silent" or "already-loud" ? part.Data : null));
                }) is not { } answer) return;
        // Not reusing answer.Reused: the status line needs the LUFS figures, and this edit is fast anyway.
        Parse(answer.Text, out double amount);
        if (!ReferenceEquals(CurrentSound, s)) return; // selection changed while the dialog was open

        CrashLog.Trace($"[sound] loudness curve start: {s.Group},{s.Index}, amount {amount:0.#}, {s.Duration.TotalSeconds:0.###} s");
        LoudnessResult result = default;
        string? reason = null;
        if (EditRangeOrWhole(s, sel, crossfade, part => SndEdit.IncreaseLoudness(part, amount, out result, out reason)) is not { } louder)
        {
            if (reason is "header" or "format" or "silent")
                CrashLog.Error("IncreaseLoudness", $"sound {s.Group},{s.Index}: {reason} — cannot boost quiet parts");
            else if (reason == "already-loud")
                CrashLog.Trace($"[sound] loudness curve no-op: {s.Group},{s.Index}, no quiet part needed lifting");
            StatusText.Text = reason switch
            {
                "silent" => "Nothing to boost — this sound is silent.",
                "already-loud" => "Nothing to boost — this amount wouldn't change this sound.",
                "format" => "Can't increase loudness: this operation supports mono/stereo integer PCM WAV only.",
                _ => "Can't increase loudness: this sound's WAV header couldn't be read.",
            };
            CrashLog.Trace($"[sound] loudness curve skipped: {s.Group},{s.Index}, {reason ?? "no result"}");
            return;
        }

        CrashLog.Trace($"[sound] loudness curve complete: {s.Group},{s.Index}, {result.InputLufs:0.#} -> {result.OutputLufs:0.#} LUFS");
        ApplyRangeEdit(s, louder, sel, () =>
            $"Raised quiet parts of {s.Group},{s.Index} toward its own peak (amount {amount:0.#}; {result.InputLufs:0.#} -> {result.OutputLufs:0.#} LUFS).");
    }

    /// <summary>Resample: asks for a new sample rate, up or down. Raising it adds no detail by itself, but makes room for Change Pitch/Speed to shift content up without losing the top end.</summary>
    private async void OnResample(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (CurrentSound is not { } s) return;
            if (s.Problem is not null || s.SampleRate <= 0)
            {
                CrashLog.Error("OnResample", $"sound {s.Group},{s.Index}: WAV header problem: {s.Problem}");
                StatusText.Text = "Can't resample: this sound's header couldn't be read.";
                return;
            }

            static string? Parse(string raw, out int rate)
            {
                string t = raw.Trim();
                if (t.EndsWith("hz", StringComparison.OrdinalIgnoreCase)) t = t[..^2].Trim();
                if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out rate)
                    || rate < SndEdit.MinSampleRate || rate > SndEdit.MaxSampleRate)
                    return $"\"{t}\" isn't a sample rate between {SndEdit.MinSampleRate} and {SndEdit.MaxSampleRate} Hz.";
                return null;
            }

            if (await PromptSoundEdit(s, "Resample",
                    $"New sample rate in Hz (now {s.SampleRate}; common: 48000, 44100, 22050, 11025):",
                    s.SampleRate.ToString(CultureInfo.InvariantCulture),
                    raw => Parse(raw, out _),
                    raw =>
                    {
                        Parse(raw, out int r);
                        return cancel => r == s.SampleRate ? s.Data : SndEdit.Resample(s, r, cancel);
                    }) is not { } answer) return;
            Parse(answer.Text, out int rate);
            if (!ReferenceEquals(CurrentSound, s)) return; // selection changed while the dialog was open
            if (rate == s.SampleRate)
            {
                StatusText.Text = $"Already at {rate} Hz.";
                return;
            }
            if (rate > s.SampleRate && !await Dialogs.Confirm(this, "Upsample",
                    $"Raising the rate from {s.SampleRate} to {rate} Hz adds no detail and makes the sound bigger.\n" +
                    "It's only worth it right before Change Pitch or Change Speed shifts the sound up.",
                    "Upsample")) return;
            if (!ReferenceEquals(CurrentSound, s)) return; // selection changed while the dialog was open

            int from = s.SampleRate;
            if ((answer.Reused ?? SndEdit.Resample(s, rate)) is not { } resampled)
            {
                CrashLog.Error("OnResample", $"sound {s.Group},{s.Index}: PCM data couldn't be read");
                StatusText.Text = "Can't resample: this sound's header couldn't be read.";
                return;
            }
            CrashLog.Trace($"[sound] resample {s.Group},{s.Index} {from} -> {rate} Hz");
            ApplyWholeEdit(s, resampled, () => $"Resampled {s.Group},{s.Index} from {from} to {rate} Hz.");
        }
        catch (Exception ex)
        {
            await Failed("OnResample", ex);
        }
    }

    /// <summary>Downsample All: lowers every sound above a chosen rate to that rate. Immediate, one undo step.</summary>
    private async void OnDownsampleAll(object? sender, RoutedEventArgs e)
    {
        if (_snd is null || _busy) return;
        bool busy = false;
        try
        {
            if (!await ResolvePendingSoundEditsBeforeSwitch()) return;
            if (await Dialogs.PromptText(this, "Downsample All",
                    "Lower every sound above this rate to it (Hz; common: 44100, 22050, 11025):",
                    Settings.Checked(nameof(Settings.DownsampleAllRate), _settings.DownsampleAllRate, Settings.DefaultDownsampleAllRate,
                        v => v >= SndEdit.MinSampleRate && v <= SndEdit.MaxSampleRate).ToString(CultureInfo.InvariantCulture)) is not { } text) return;
            text = text.Trim();
            if (text.EndsWith("hz", StringComparison.OrdinalIgnoreCase)) text = text[..^2].Trim();
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rate)
                || rate < SndEdit.MinSampleRate || rate > SndEdit.MaxSampleRate)
            {
                StatusText.Text = $"\"{text}\" isn't a sample rate between {SndEdit.MinSampleRate} and {SndEdit.MaxSampleRate} Hz.";
                return;
            }

            var file = _snd;
            var targets = file.Sounds.Where(x => x.Problem is null && x.SampleRate > rate).ToList();
            if (targets.Count == 0) { StatusText.Text = $"No sounds above {rate} Hz."; return; }

            var changes = new List<(SoundEntry Sound,
                (byte[] Data, string? Problem, int Ch, int Rate, int Bits, ushort Format, TimeSpan Dur) Before,
                (byte[] Data, string? Problem, int Ch, int Rate, int Bits, ushort Format, TimeSpan Dur) After)>();
            var errors = new List<string>();
            CrashLog.Trace($"[bulk] downsample {targets.Count} sounds to {rate} Hz");
            busy = TryBeginBusy();
            if (!busy) return;
            for (int n = 0; n < targets.Count; n++)
            {
                var s = targets[n];
                await BusyStep("Downsampling", n, targets.Count, $"{s.Group},{s.Index} {s.SampleRate} Hz");
                if (!ReferenceEquals(_snd, file)) { StatusText.Text = "Downsample stopped \u2014 the file was closed."; return; }
                var before = (s.Data, s.Problem, s.Channels, s.SampleRate, s.BitsPerSample, s.FormatTag, s.Duration);
                if (SndEdit.Resample(s, rate) is not { } data) { errors.Add($"{s.Group},{s.Index}: header couldn't be read"); continue; }
                s.Data = data;
                s.Problem = null;
                SndReader.Describe(s);
                changes.Add((s, before, (s.Data, s.Problem, s.Channels, s.SampleRate, s.BitsPerSample, s.FormatTag, s.Duration)));
            }
            LogBulkProblems("DownsampleAll", errors, []);
            if (changes.Count == 0) { StatusText.Text = "Nothing downsampled."; return; }

            void Apply(bool useAfter)
            {
                foreach (var (s, before, after) in changes)
                    (s.Data, s.Problem, s.Channels, s.SampleRate, s.BitsPerSample, s.FormatTag, s.Duration) = useAfter ? after : before;
                AfterSoundEdit();
            }
            _undo.Record($"downsample {changes.Count} sound(s) to {rate} Hz", () => Apply(false), () => Apply(true));
            MarkSndDirty();
            AfterSoundEdit();
            StatusText.Text = $"Downsampled {changes.Count} sound(s) to {rate} Hz" + (errors.Count > 0 ? $"; {errors.Count} skipped (see crash.log)" : ".");
        }
        catch (Exception ex)
        {
            await Failed("OnDownsampleAll", ex);
        }
        finally
        {
            if (busy) EndBusy();
        }
    }

    /// <summary>Change Volume: asks for a percentage (100 = unchanged); warns in the dialog when it would clip.</summary>
    private async void OnApplyVolume(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ChangeVolume();
        }
        catch (Exception ex)
        {
            await Failed("OnApplyVolume", ex);
        }
    }

    private async Task ChangeVolume()
    {
        if (CurrentSound is not { } s) return;
        if (s.Problem is not null) { StatusText.Text = "Can't adjust volume: this sound's header couldn't be read."; return; }

        double maxPercent = Settings.Checked(nameof(Settings.VolumeMaxPercent), _settings.VolumeMaxPercent,
            Settings.DefaultVolumeMaxPercent, v => double.IsFinite(v) && v > 0);
        var sel = SelectionOn(s);
        int crossfade = SelectionCrossfadeFrames(s);

        string? Parse(string raw, out float percent)
        {
            string t = raw.Trim().TrimEnd('%').Trim();
            if ((!float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out percent)
                    && !float.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out percent))
                || !float.IsFinite(percent) || percent <= 0 || percent > maxPercent)
                return $"\"{t}\" isn't a volume between 1 and {maxPercent:0.##}%.";
            return null;
        }

        // Peak of what the edit touches, read once: the sound can't change while the dialog is open.
        float? peak = sel is { } r ? SndEdit.Peak(s, r.Start, r.End) : SndEdit.Peak(s);
        string? ClipWarning(string raw)
        {
            if (Parse(raw, out float p) is not null || peak is not { } pk || pk <= 0 || pk * p / 100f <= 1f) return null;
            double limit = Math.Floor(1000 / pk) / 10; // round down so the quoted value itself doesn't clip
            return $"This will clip. Highest volume without clipping: {limit:0.#}%.";
        }

        if (await PromptSoundEdit(s, "Change Volume", "Volume % (100 = unchanged):", "100",
                raw => Parse(raw, out _),
                raw =>
                {
                    Parse(raw, out float p);
                    return _ => Math.Abs(p / 100f - 1f) < 0.001f ? s.Data
                        : EditRangeOrWhole(s, sel, crossfade, part => SndEdit.SetVolume(part, p / 100f));
                }, warn: ClipWarning) is not { } answer) return;
        Parse(answer.Text, out float percent);
        if (!ReferenceEquals(CurrentSound, s)) return; // selection changed while the dialog was open
        float gain = percent / 100f;
        if (Math.Abs(gain - 1f) < 0.001f
            || (answer.Reused ?? EditRangeOrWhole(s, sel, crossfade, part => SndEdit.SetVolume(part, gain))) is not { } scaled)
        {
            StatusText.Text = "That volume is the same as it already is.";
            return;
        }

        ApplyRangeEdit(s, scaled, sel, () => $"Set {s.Group},{s.Index}{(sel is null ? "" : " (selection)")} to {percent:0.#}% volume.");
    }

    /// <summary>PitchMaxSemitones from settings.json; anything not positive and finite falls back to the default (logged).</summary>
    private double PitchLimit()
    {
        double max = _settings.PitchMaxSemitones;
        if (double.IsFinite(max) && max > 0) return max;
        CrashLog.Error("Settings", $"PitchMaxSemitones {max} isn't a positive number; using {Settings.DefaultPitchMaxSemitones}");
        return Settings.DefaultPitchMaxSemitones;
    }

    /// <summary>RateMinPercent/RateMaxPercent from settings.json; unless 0 &lt; min &lt; max (finite), both fall back to the defaults (logged).</summary>
    private (double Min, double Max) RateLimits()
    {
        double min = _settings.RateMinPercent, max = _settings.RateMaxPercent;
        if (double.IsFinite(min) && double.IsFinite(max) && min > 0 && max > min) return (min, max);
        CrashLog.Error("Settings", $"RateMinPercent {min} / RateMaxPercent {max} need 0 < min < max; " +
            $"using {Settings.DefaultRateMinPercent} / {Settings.DefaultRateMaxPercent}");
        return (Settings.DefaultRateMinPercent, Settings.DefaultRateMaxPercent);
    }

    private async void OnChangePitch(object? sender, RoutedEventArgs e)
    {
        try
        {
            double max = PitchLimit();
            await RunRateEdit("Change pitch",
                $"Semitones, {-max:0.##} to {max:0.##} (12 = one octave up, -12 = one down; length stays the same):",
                0, -max, max, $"a pitch shift between {-max:0.##} and {max:0.##} semitones",
                SndEdit.ChangePitch,
                (s, v, _) => $"Shifted {s.Group},{s.Index} by {v:+0.##;-0.##} semitones.");
        }
        catch (Exception ex)
        {
            await Failed("OnChangePitch", ex);
        }
    }

    private async void OnChangeTempo(object? sender, RoutedEventArgs e)
    {
        try
        {
            var (min, max) = RateLimits();
            await RunRateEdit("Change tempo",
                $"Tempo %, {min:0.##} to {max:0.##} (100 = unchanged, 200 = twice as fast; pitch stays the same):",
                100, min, max, $"a tempo between {min:0.##} and {max:0.##}%",
                SndEdit.ChangeTempo,
                (s, v, before) => $"Set {s.Group},{s.Index} to {v:0.#}% tempo: {before:0.00}s -> {s.Duration.TotalSeconds:0.00}s.");
        }
        catch (Exception ex)
        {
            await Failed("OnChangeTempo", ex);
        }
    }

    private async void OnChangeSpeed(object? sender, RoutedEventArgs e)
    {
        try
        {
            var (min, max) = RateLimits();
            await RunRateEdit("Change speed",
                $"Speed %, {min:0.##} to {max:0.##} (100 = unchanged, 200 = twice as fast and an octave higher):",
                100, min, max, $"a speed between {min:0.##} and {max:0.##}%",
                SndEdit.ChangeSpeed,
                (s, v, before) => $"Set {s.Group},{s.Index} to {v:0.#}% speed: {before:0.00}s -> {s.Duration.TotalSeconds:0.00}s.");
        }
        catch (Exception ex)
        {
            await Failed("OnChangeSpeed", ex);
        }
    }

    /// <summary>Shared by Change Pitch/Tempo/Speed: asks for a value, then applies the edit as a pending edit.</summary>
    private async Task RunRateEdit(string title, string prompt, double neutral, double min, double max,
        string rangeText, Func<SoundEntry, double, CancellationToken, byte[]?> edit, Func<SoundEntry, double, double, string> done)
    {
        if (CurrentSound is not { } s) return;
        string verb = title.ToLowerInvariant();
        if (s.Problem is not null)
        {
            CrashLog.Error(title, $"sound {s.Group},{s.Index}: WAV header problem: {s.Problem}");
            StatusText.Text = $"Can't {verb}: this sound's header couldn't be read.";
            return;
        }

        var sel = SelectionOn(s);
        int crossfade = SelectionCrossfadeFrames(s);

        string? Parse(string raw, out double v)
        {
            string t = raw.Trim().TrimEnd('%').Trim();
            if ((!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                    && !double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
                || !double.IsFinite(v) || v < min || v > max)
                return $"\"{t}\" isn't {rangeText}.";
            return null;
        }

        if (await PromptSoundEdit(s, title, prompt, neutral.ToString("0", CultureInfo.InvariantCulture),
                raw => Parse(raw, out _),
                raw =>
                {
                    Parse(raw, out double v);
                    return cancel => Math.Abs(v - neutral) < 0.001 ? s.Data
                        : EditRangeOrWhole(s, sel, crossfade, part => edit(part, v, cancel));
                }) is not { } answer) return;
        Parse(answer.Text, out double value);
        if (!ReferenceEquals(CurrentSound, s)) return; // selection changed while the dialog was open
        if (Math.Abs(value - neutral) < 0.001)
        {
            StatusText.Text = "That's the same as it already is.";
            return;
        }

        double beforeSeconds = s.Duration.TotalSeconds;
        CrashLog.Trace($"[sound] {verb} start: {s.Group},{s.Index}, {value:0.##}, {beforeSeconds:0.###} s");
        var result = answer.Reused ?? EditRangeOrWhole(s, sel, crossfade, part => edit(part, value, CancellationToken.None));
        if (result is null)
        {
            CrashLog.Error(title, $"sound {s.Group},{s.Index}: PCM data couldn't be read");
            StatusText.Text = $"Can't {verb}: this sound's header couldn't be read.";
            return;
        }

        CrashLog.Trace($"[sound] {verb} complete: {s.Group},{s.Index}, {result.Length} bytes");
        ApplyRangeEdit(s, result, sel, () => done(s, value, beforeSeconds));
    }

    // ---- waveform selection ------------------------------------------------

    private (int Start, int End)? _soundSelection; // frames of _soundSelectionOwner
    private SoundEntry? _soundSelectionOwner;

    private (int Start, int End)? SelectionOn(SoundEntry s) =>
        ReferenceEquals(_soundSelectionOwner, s) ? _soundSelection : null;

    private void ClearSoundSelection()
    {
        _soundSelection = null;
        _soundSelectionOwner = null;
        if (Waveform is not null) Waveform.Selection = null;
        if (SoundSelectionText is not null) SoundSelectionText.Text = "";
    }

    /// <summary>Waveform drag (range so far) or plain click (null). Updates the selection text only; the waveform draws the band itself.</summary>
    private void OnWaveformSelectionEdited((int Start, int End)? selection)
    {
        if (CurrentSound is not { } s) return;
        _soundSelection = selection;
        _soundSelectionOwner = selection is null ? null : s;
        SoundSelectionText.Text = SelectionText(s);
    }

    /// <summary>Selection start, end and length in ms, for the waveform panel's header; empty without one.</summary>
    private string SelectionText(SoundEntry s)
    {
        if (SelectionOn(s) is not { } r || s.SampleRate <= 0) return "";
        double Ms(int frames) => frames * 1000.0 / s.SampleRate;
        return $"Selection {Ms(r.Start):0}–{Ms(r.End):0} ms ({Ms(r.End - r.Start):0} ms)";
    }

    private bool _syncingWaveformScroll;

    /// <summary>Keeps the scrollbar under the waveform in step with its zoom and position.</summary>
    private void SyncWaveformScroll()
    {
        if (WaveformScroll is null || Waveform is null) return;
        _syncingWaveformScroll = true;
        try
        {
            double visible = Waveform.VisibleFrames, total = Waveform.TotalFrames;
            WaveformScroll.Minimum = 0;
            WaveformScroll.Maximum = Math.Max(0, total - visible);
            WaveformScroll.ViewportSize = visible;
            WaveformScroll.Value = Waveform.ViewStart;
            WaveformScroll.IsEnabled = total > visible;
        }
        finally
        {
            _syncingWaveformScroll = false;
        }
    }

    /// <summary>Edge crossfade for edits on a selection (settings.json SelectionCrossfadeMilliseconds), in frames of s.</summary>
    private int SelectionCrossfadeFrames(SoundEntry s) =>
        (int)Math.Round(Settings.Checked(nameof(Settings.SelectionCrossfadeMilliseconds), _settings.SelectionCrossfadeMilliseconds,
            Settings.DefaultSelectionCrossfadeMilliseconds, v => double.IsFinite(v) && v >= 0) / 1000 * s.SampleRate);

    /// <summary>Runs a format-keeping edit on sel when given (edges crossfaded), else on the whole sound. Touches no UI, so previews can run it in the background.</summary>
    private static byte[]? EditRangeOrWhole(SoundEntry s, (int Start, int End)? sel, int crossfadeFrames, Func<SoundEntry, byte[]?> edit) =>
        sel is { } r ? SndEdit.ApplyToRange(s, r.Start, r.End, crossfadeFrames, edit) : edit(s);

    /// <summary>Applies an EditRangeOrWhole result; a selection stays, resized to what the edit made of it (Tempo/Speed change its length).</summary>
    private void ApplyRangeEdit(SoundEntry s, byte[] data, (int Start, int End)? sel, Func<string> status)
    {
        if (sel is { } r)
        {
            var probe = new SoundEntry { Data = data };
            SndReader.Describe(probe);
            int length = r.End - r.Start + SndEdit.FrameCount(probe) - SndEdit.FrameCount(s);
            if (length > 0)
            {
                _soundSelection = (r.Start, r.Start + length);
                _soundSelectionOwner = s;
            }
            else ClearSoundSelection();
        }
        ApplyPendingSoundEdit(s, data, status);
    }

    /// <summary>Whole-file edits (Resample, Bit Depth, Mono, Trim Silence, Trim to Selection, Insert Silence) drop the selection.</summary>
    private void ApplyWholeEdit(SoundEntry s, byte[] data, Func<string> status)
    {
        ClearSoundSelection();
        ApplyPendingSoundEdit(s, data, status);
    }

    private string SoundDetailText(SoundEntry s)
    {
        if (s.Problem is not null) return $"{s.Data.Length:N0} bytes";
        string text = $"{s.Duration.TotalSeconds:0.00} seconds   |   {s.SampleRate:N0} Hz   |   "
            + $"{(s.Channels == 1 ? "mono" : s.Channels == 2 ? "stereo" : $"{s.Channels} channels")}   |   "
            + $"{s.BitsPerSample}-bit   |   {s.Data.Length:N0} bytes";
        return text;
    }

    private void OnTrimToSelection(object? sender, RoutedEventArgs e)
    {
        if (CurrentSound is not { } s) return;
        if (SelectionOn(s) is not { } r)
        {
            StatusText.Text = "Select part of the waveform first.";
            return;
        }
        if (r.Start == 0 && r.End == SndEdit.FrameCount(s))
        {
            StatusText.Text = "The selection is already the whole sound.";
            return;
        }
        if (SndEdit.ExtractRange(s, r.Start, r.End) is not { } kept)
        {
            CrashLog.Error("OnTrimToSelection", $"sound {s.Group},{s.Index}: range {r.Start}-{r.End} couldn't be cut");
            StatusText.Text = "Can't trim: this sound's header couldn't be read.";
            return;
        }
        double before = s.Duration.TotalSeconds;
        ApplyWholeEdit(s, kept, () => $"Trimmed {s.Group},{s.Index} to the selection: {before:0.00}s -> {s.Duration.TotalSeconds:0.00}s.");
    }

    private void OnReverse(object? sender, RoutedEventArgs e)
    {
        if (CurrentSound is not { } s) return;
        if (s.Problem is not null) { StatusText.Text = "Can't reverse: this sound's header couldn't be read."; return; }
        var sel = SelectionOn(s);
        if (EditRangeOrWhole(s, sel, SelectionCrossfadeFrames(s), SndEdit.Reverse) is not { } reversed)
        {
            StatusText.Text = "Nothing to reverse — too short.";
            return;
        }
        ApplyRangeEdit(s, reversed, sel, () => $"Reversed {s.Group},{s.Index}{(sel is null ? "" : " (selection)")}.");
    }

    private async void OnFadeIn(object? sender, RoutedEventArgs e)
    {
        try { await FadeSound(fadeIn: true); }
        catch (Exception ex) { await Failed("OnFadeIn", ex); }
    }

    private async void OnFadeOut(object? sender, RoutedEventArgs e)
    {
        try { await FadeSound(fadeIn: false); }
        catch (Exception ex) { await Failed("OnFadeOut", ex); }
    }

    /// <summary>
    /// Fade In / Fade Out. With a selection: across the whole selection, no length box and
    /// no edge crossfade. Without: over a length in ms from the start or end (longer than
    /// the sound is an error). Curve via radios, Linear by default.
    /// </summary>
    private async Task FadeSound(bool fadeIn)
    {
        if (CurrentSound is not { } s) return;
        string title = fadeIn ? "Fade in" : "Fade out", verb = fadeIn ? "fade in" : "fade out";
        if (s.Problem is not null || s.SampleRate <= 0)
        {
            CrashLog.Error(title, $"sound {s.Group},{s.Index}: WAV header problem: {s.Problem}");
            StatusText.Text = $"Can't {verb}: this sound's header couldn't be read.";
            return;
        }
        var sel = SelectionOn(s);
        int total = SndEdit.FrameCount(s);
        double totalMs = total * 1000.0 / s.SampleRate;

        var curve = FadeCurve.Linear;
        var linear = new RadioButton { Content = "Linear", IsChecked = true };
        var equalPower = new RadioButton { Content = "Equal-power" };
        equalPower.IsCheckedChanged += (_, _) => curve = equalPower.IsChecked == true ? FadeCurve.EqualPower : FadeCurve.Linear;
        var curves = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { linear, equalPower } };

        string? Parse(string raw, out int frames)
        {
            frames = total;
            if (sel is not null) return null; // the selection sets the length
            string t = raw.Trim();
            if (t.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) t = t[..^2].Trim();
            if ((!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms)
                    && !double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out ms))
                || !double.IsFinite(ms) || ms <= 0)
                return $"\"{t}\" isn't a length in ms.";
            frames = Math.Max(1, (int)Math.Round(ms * s.SampleRate / 1000));
            if (frames > total) return $"{ms:0.##} ms is longer than the sound ({totalMs:0} ms).";
            return null;
        }

        byte[]? Make(int frames, FadeCurve c) => sel is { } range
            ? SndEdit.ApplyToRange(s, range.Start, range.End, 0, part => SndEdit.Fade(part, fadeIn, SndEdit.FrameCount(part), c))
            : SndEdit.Fade(s, fadeIn, frames, c);

        string label = sel is { } shown
            ? $"Fades {(fadeIn ? "in" : "out")} across the selection ({(shown.End - shown.Start) * 1000.0 / s.SampleRate:0} ms). Curve:"
            : $"{(fadeIn ? "Fade-in" : "Fade-out")} length in ms (the sound is {totalMs:0} ms):";
        double defaultMs = Settings.Checked(nameof(Settings.FadeMilliseconds), _settings.FadeMilliseconds,
            Settings.DefaultFadeMilliseconds, v => double.IsFinite(v) && v > 0);

        if (await PromptSoundEdit(s, title, label,
                sel is null ? defaultMs.ToString("0.##", CultureInfo.InvariantCulture) : null,
                raw => Parse(raw, out _),
                raw =>
                {
                    Parse(raw, out int f);
                    var c = curve;
                    return _ => Make(f, c);
                },
                curves, () => curve.ToString()) is not { } answer) return;
        if (!ReferenceEquals(CurrentSound, s)) return; // selection changed while the dialog was open

        Parse(answer.Text, out int frames);
        var chosen = curve;
        if ((answer.Reused ?? Make(frames, chosen)) is not { } faded)
        {
            CrashLog.Error(title, $"sound {s.Group},{s.Index}: PCM data couldn't be read");
            StatusText.Text = $"Can't {verb}: this sound's header couldn't be read.";
            return;
        }
        double fadeMs = (sel is { } used ? used.End - used.Start : frames) * 1000.0 / s.SampleRate;
        string shape = chosen == FadeCurve.Linear ? "linear" : "equal-power";
        CrashLog.Trace($"[sound] {verb} {s.Group},{s.Index}: {fadeMs:0} ms, {shape}{(sel is null ? "" : ", selection")}");
        ApplyRangeEdit(s, faded, sel, () =>
            $"Faded {(fadeIn ? "in" : "out")} {s.Group},{s.Index} over {fadeMs:0} ms ({shape}){(sel is null ? "" : " across the selection")}.");
    }

    private async void OnEcho(object? sender, RoutedEventArgs e)
    {
        try { await EchoSound(); }
        catch (Exception ex) { await Failed("OnEcho", ex); }
    }

    /// <summary>
    /// Echo: delay in ms (the dialog's field), decay % (1-100), repeats (Auto = until a repeat falls below
    /// SilenceThreshold, as Trim Silence judges it; at most EchoMaxRepeats either way, so the tail always ends). With a selection only that part echoes and
    /// its echoes mix into what follows; the sound grows when they run past its end. Clip warning on
    /// Preview/OK like Change Volume. The selection is kept as it was.
    /// </summary>
    private async Task EchoSound()
    {
        if (CurrentSound is not { } s) return;
        if (s.Problem is not null || s.SampleRate <= 0)
        {
            CrashLog.Error("Echo", $"sound {s.Group},{s.Index}: WAV header problem: {s.Problem}");
            StatusText.Text = "Can't add echo: this sound's header couldn't be read.";
            return;
        }
        var sel = SelectionOn(s);
        int total = SndEdit.FrameCount(s);
        int start = sel?.Start ?? 0, end = sel?.End ?? total;
        int frameBytes = Math.Max(s.BitsPerSample, 8) / 8 * s.Channels;

        const double maxDecay = 100; // 100% = every echo as loud as the original; the repeat cap still ends the tail
        int maxRepeats = Settings.Checked(nameof(Settings.EchoMaxRepeats), _settings.EchoMaxRepeats,
            Settings.DefaultEchoMaxRepeats, v => v >= 1);
        // Auto stops where Trim Silence would call it silence, measured from the echoed part's peak.
        double silence = Settings.Checked(nameof(Settings.SilenceThreshold), _settings.SilenceThreshold,
            SndEdit.DefaultSilenceThreshold, v => double.IsFinite(v) && v > 0 && v < 1);
        double peak = SndEdit.Peak(s, start, end) ?? 1;
        double defaultDelay = Settings.Checked(nameof(Settings.EchoDelayMilliseconds), _settings.EchoDelayMilliseconds,
            Settings.DefaultEchoDelayMilliseconds, v => double.IsFinite(v) && v > 0);
        double defaultDecay = Settings.Checked(nameof(Settings.EchoDecayPercent), _settings.EchoDecayPercent,
            Settings.DefaultEchoDecayPercent, v => v > 0 && v <= maxDecay);

        double decayStep = Settings.Checked(nameof(Settings.EchoDecayStep), _settings.EchoDecayStep,
            Settings.DefaultEchoDecayStep, v => v is >= 1 and <= 100);
        var decayBox = new NumericUpDown { Minimum = 1, Maximum = (decimal)maxDecay, Increment = (decimal)decayStep, Value = (decimal)defaultDecay, FormatString = "0", Width = 110 };
        var auto = new CheckBox { Content = "Auto", IsChecked = true };
        var repeatsBox = new NumericUpDown { Minimum = 1, Maximum = maxRepeats, Increment = 1, FormatString = "0", Width = 110, IsEnabled = false };
        var tail = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        double lastDelayMs = defaultDelay;

        double Decay() => (double)(decayBox.Value ?? (decimal)defaultDecay) / 100;
        int Repeats() => auto.IsChecked == true
            ? SndEdit.AutoEchoRepeats(Decay(), peak, silence, maxRepeats)
            : (int)(repeatsBox.Value ?? 1);
        void ShowTail()
        {
            int n = Repeats();
            tail.Text = $"{n} repeat{(n == 1 ? "" : "s")}, echoes last {n * lastDelayMs / 1000:0.##} s";
        }
        repeatsBox.Value = SndEdit.AutoEchoRepeats(defaultDecay / 100, peak, silence, maxRepeats);
        auto.IsCheckedChanged += (_, _) => { repeatsBox.IsEnabled = auto.IsChecked != true; ShowTail(); };
        decayBox.ValueChanged += (_, _) => ShowTail();
        repeatsBox.ValueChanged += (_, _) => ShowTail();
        ShowTail();

        var extra = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                {
                    new TextBlock { Text = "Decay %", Width = 70, VerticalAlignment = VerticalAlignment.Center }, decayBox,
                } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                {
                    new TextBlock { Text = "Repeats", Width = 70, VerticalAlignment = VerticalAlignment.Center }, repeatsBox, auto,
                } },
                tail,
            },
        };

        string? Parse(string raw, out int delayFrames)
        {
            delayFrames = 0;
            string t = raw.Trim();
            if (t.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) t = t[..^2].Trim();
            if ((!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms)
                    && !double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out ms))
                || !double.IsFinite(ms) || ms <= 0)
                return $"\"{t}\" isn't a delay in ms.";
            delayFrames = Math.Max(1, (int)Math.Round(ms * s.SampleRate / 1000));
            if (SndEdit.EchoLength(total, end, delayFrames, Repeats()) * frameBytes > int.MaxValue / 2)
                return "Those echoes make the sound too long to hold.";
            lastDelayMs = ms;
            ShowTail();
            return null;
        }

        string? ClipWarning(string raw)
        {
            if (Parse(raw, out int d) is not null) return null;
            return SndEdit.EchoPeak(s, start, end, d, Decay(), Repeats()) is > 1f
                ? "This will clip. Lower the decay, or the sound's volume first." : null;
        }

        string label = sel is { } shown
            ? $"Echoes the selection ({(shown.End - shown.Start) * 1000.0 / s.SampleRate:0} ms). Delay in ms:"
            : "Delay in ms:";
        if (await PromptSoundEdit(s, "Echo", label, defaultDelay.ToString("0.##", CultureInfo.InvariantCulture),
                raw => Parse(raw, out _),
                raw =>
                {
                    Parse(raw, out int d);
                    double decay = Decay();
                    int n = Repeats();
                    return cancel => SndEdit.Echo(s, start, end, d, decay, n, cancel);
                },
                extra, () => $"{decayBox.Value}|{auto.IsChecked}|{repeatsBox.Value}", ClipWarning) is not { } answer) return;
        if (!ReferenceEquals(CurrentSound, s)) return; // selection changed while the dialog was open

        Parse(answer.Text, out int delay);
        double chosenDecay = Decay();
        int repeats = Repeats();
        if ((answer.Reused ?? SndEdit.Echo(s, start, end, delay, chosenDecay, repeats)) is not { } echoed)
        {
            CrashLog.Error("Echo", $"sound {s.Group},{s.Index}: PCM data couldn't be read");
            StatusText.Text = "Can't add echo: this sound's header couldn't be read.";
            return;
        }
        double delayMs = delay * 1000.0 / s.SampleRate;
        CrashLog.Trace($"[sound] echo {s.Group},{s.Index}: {delayMs:0} ms, {chosenDecay * 100:0}%, {repeats} repeats{(sel is null ? "" : ", selection")}");
        // Not ApplyRangeEdit: the echoes are added after the selection, which itself stays where it was.
        ApplyPendingSoundEdit(s, echoed, () =>
            $"Added echo to {s.Group},{s.Index}{(sel is null ? "" : " (selection)")}: {delayMs:0} ms, {chosenDecay * 100:0}%, {repeats} repeats.");
    }

    /// <summary>Change Bit Depth: 8/16/24 (what Ikemen plays), dither checkbox for lowering, confirm when raising. Whole sound.</summary>
    private async void OnChangeBitDepth(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (CurrentSound is not { } s) return;
            if (s.Problem is not null)
            {
                CrashLog.Error("OnChangeBitDepth", $"sound {s.Group},{s.Index}: WAV header problem: {s.Problem}");
                StatusText.Text = "Can't change bit depth: this sound's header couldn't be read.";
                return;
            }
            int current = s.BitsPerSample;
            int target = SndEdit.BitDepthTargets.Contains(current) ? current : 24;
            bool dither = true;

            var radios = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            foreach (int bits in SndEdit.BitDepthTargets)
            {
                var radio = new RadioButton { Content = $"{bits}-bit", IsChecked = bits == target };
                radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) target = bits; };
                radios.Children.Add(radio);
            }
            var ditherBox = new CheckBox { Content = "Dither when lowering", IsChecked = true };
            ditherBox.IsCheckedChanged += (_, _) => dither = ditherBox.IsChecked == true;
            var extra = new StackPanel { Spacing = 8, Children = { radios, ditherBox } };

            if (await PromptSoundEdit(s, "Change Bit Depth", $"New bit depth (now {current}-bit):", null,
                    _ => null,
                    _ =>
                    {
                        int t = target;
                        bool d = dither;
                        return _ => t == current ? s.Data : SndEdit.ChangeBitDepth(s, t, d);
                    },
                    extra, () => $"{target}|{dither}") is not { } answer) return;
            if (!ReferenceEquals(CurrentSound, s)) return; // selection changed while the dialog was open

            int to = target;
            bool dithered = dither && to < current;
            if (to == current)
            {
                StatusText.Text = $"Already {current}-bit.";
                return;
            }
            if (to > current && !await Dialogs.Confirm(this, "Raise Bit Depth",
                    $"Raising from {current} to {to}-bit adds no detail and makes the sound bigger.\n" +
                    $"It's only worth it before several edits on a {current}-bit sound.",
                    "Raise")) return;
            if (!ReferenceEquals(CurrentSound, s)) return;

            if ((answer.Reused ?? SndEdit.ChangeBitDepth(s, to, dither)) is not { } changed)
            {
                CrashLog.Error("OnChangeBitDepth", $"sound {s.Group},{s.Index}: PCM data couldn't be read");
                StatusText.Text = "Can't change bit depth: this sound's header couldn't be read.";
                return;
            }
            CrashLog.Trace($"[sound] bit depth {s.Group},{s.Index} {current} -> {to}{(dithered ? ", dithered" : "")}");
            ApplyWholeEdit(s, changed, () => $"Changed {s.Group},{s.Index} from {current}-bit to {to}-bit{(dithered ? " (dithered)" : "")}.");
        }
        catch (Exception ex)
        {
            await Failed("OnChangeBitDepth", ex);
        }
    }

    private void OnConvertToMono(object? sender, RoutedEventArgs e)
    {
        if (CurrentSound is not { } s) return;
        if (s.Problem is not null) { StatusText.Text = "Can't convert: this sound's header couldn't be read."; return; }

        if (SndEdit.ConvertToMono(s) is not { } mono)
        {
            StatusText.Text = "Already mono — nothing to convert.";
            return;
        }

        int fromChannels = s.Channels;
        ApplyWholeEdit(s, mono, () => $"Converted {s.Group},{s.Index} from {fromChannels} channels to mono.");
    }

    /// <summary>
    /// One click only selects, like the action list; double-clicking a Group
    /// or Number cell prompts for the new value and commits through the same
    /// undo path the old inline cells used. DoubleTapped lives on the cell
    /// Border, not the TextBlock (same hit-testing reason as the action
    /// list's template documents).
    /// </summary>
    private async void OnSoundCellDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_snd is null || sender is not Control c || c.DataContext is not SoundRow row) return;
        if (!_snd.Sounds.Contains(row.Sound)) return;
        bool isGroup = (string?)c.Tag == "Group";

        try
        {
            var result = await Dialogs.PromptText(this, isGroup ? "Change group" : "Change number",
                isGroup ? "New group:" : "New number:",
                (isGroup ? row.Sound.Group : row.Sound.Index).ToString(CultureInfo.InvariantCulture));
            if (result is null) return;
            if (!int.TryParse(result.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int val) || val < 0)
            {
                await Dialogs.ShowMessage(this, "Not a Number", "Type a whole number of 0 or more.");
                return;
            }
            CommitSoundField(row, isGroup ? val : null, isGroup ? null : val);
        }
        catch (Exception ex)
        {
            // async void: an exception here has nowhere else to go — log it
            // explicitly rather than relying on it reaching AppDomain intact.
            CrashLog.Write("OnSoundCellDoubleTapped", ex);
            await Dialogs.ShowMessage(this, "Something Went Wrong", ex.Message);
        }
    }

    /// <summary>Selects the row being edited (so the rest of the UI follows it) and commits through the same undo path the old side-panel spinners used.</summary>
    private void CommitSoundField(SoundRow row, int? group, int? index)
    {
        if (_snd is null || !_snd.Sounds.Contains(row.Sound)) { ApplySoundFilter(); return; }
        int at = (SoundList.ItemsSource as List<SoundRow>)?.IndexOf(row) ?? -1;
        if (at < 0) { ApplySoundFilter(); return; }
        if (SoundList.SelectedIndex != at) SoundList.SelectedIndex = at;

        var s = row.Sound;
        var before = (G: s.Group, N: s.Index);
        var after = (G: group ?? before.G, N: index ?? before.N);
        if (before == after) { AfterSoundEdit(); return; }

        void Apply((int G, int N) v) { (s.Group, s.Index) = v; AfterSoundEdit(); }
        _undo.Do("sound number", () => Apply(after), () => Apply(before), $"sound:{_snd!.Sounds.IndexOf(s)}");
        MarkSndDirty();
    }

    private void AfterSoundEdit()
    {
        ApplySoundFilter();
        ShowSound();
        UpdateSoundButtonStates();
        UpdateSndInfo();
    }

    private int NextFreeSoundIndex(int group)
    {
        if (_snd is null) return 0;
        var used = _snd.Sounds.Where(s => s.Group == group).Select(s => s.Index).ToHashSet();
        int n = 0;
        while (used.Contains(n)) n++;
        return n;
    }
}
