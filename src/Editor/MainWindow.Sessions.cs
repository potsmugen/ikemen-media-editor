using System.Text.Json;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Editor;

/// <summary>Save Session / Load Session: which files each project tab has open (Session.cs).</summary>
public partial class MainWindow
{
    private static readonly FilePickerFileType SessionType =
        new("Editor sessions") { Patterns = ["*.imesession"] };

    private static bool IsSessionPath(string path) =>
        Path.GetExtension(path).Equals(".imesession", StringComparison.OrdinalIgnoreCase);

    /// <summary>Null when the tab has nothing saved on disk to point at.</summary>
    private static SessionTab? SessionTabFor(Document d)
    {
        var tab = new SessionTab { Name = d.CustomName };
        if (d.DefPath is not null) tab.Def = d.DefPath;
        else
        {
            tab.Sff = d.Path;
            tab.Air = d.AirPath;
            tab.Snd = d.SndPath;
            tab.Palettes = d.StandalonePalettes.Where(p => p.SourcePath is not null).Select(p => p.SourcePath!).ToList();
        }
        return tab.Def is null && tab.Sff is null && tab.Air is null && tab.Snd is null && tab.Palettes.Count == 0 ? null : tab;
    }

    private async void OnSaveSession(object? sender, RoutedEventArgs e)
    {
        try
        {
            var session = new SessionFile();
            int skipped = 0;
            foreach (var doc in _docs)
            {
                if (IsEmptyDocument(doc)) continue;
                if (SessionTabFor(doc) is not { } tab) { skipped++; continue; }
                if (ReferenceEquals(doc, _doc)) session.ActiveTab = session.Tabs.Count;
                session.Tabs.Add(tab);
            }
            if (session.Tabs.Count == 0)
            {
                StatusText.Text = "Nothing to save: no tab has files saved on disk.";
                return;
            }

            var file = await PickSave(new FilePickerSaveOptions
            {
                Title = "Save Session",
                SuggestedFileName = "session",
                DefaultExtension = "imesession",
                FileTypeChoices = [SessionType],
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            File.WriteAllText(path, JsonSerializer.Serialize(session, SettingsContext.Default.SessionFile));
            CrashLog.Trace($"[session] saved {path}: {session.Tabs.Count} tabs, {skipped} skipped");
            _settings.AddRecent(path);
            _settings.Save();
            RebuildRecentMenu();
            StatusText.Text = $"Saved session {Path.GetFileName(path)}: {session.Tabs.Count} tab{(session.Tabs.Count == 1 ? "" : "s")}"
                + (skipped > 0 ? $". Skipped {skipped} tab{(skipped == 1 ? "" : "s")} with files never saved" : "");
        }
        catch (Exception ex)
        {
            await Failed("OnSaveSession", ex);
        }
    }

    private async void OnLoadSession(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_projectOpening) return;
            var files = await PickOpen(new FilePickerOpenOptions
            {
                Title = "Load Session",
                AllowMultiple = false,
                FileTypeFilter = [SessionType],
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            await LoadSessionFile(path);
        }
        catch (Exception ex)
        {
            await Failed("OnLoadSession", ex);
        }
    }

    /// <summary>
    /// Loads a session's tabs. When any tab is in use, asks Replace or Append; Replace first runs
    /// each unsaved tab's check, like quitting. Missing files are skipped and listed afterwards.
    /// </summary>
    private async Task LoadSessionFile(string path)
    {
        SessionFile? session;
        try
        {
            session = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsContext.Default.SessionFile);
        }
        catch (Exception ex)
        {
            CrashLog.Write("LoadSessionFile", ex);
            await Dialogs.ShowMessage(this, "Could Not Read the Session", ex.Message);
            return;
        }
        if (session is null || session.Tabs.Count == 0)
        {
            await Dialogs.ShowMessage(this, "Could Not Read the Session", $"{Path.GetFileName(path)} has no tabs.");
            return;
        }

        if (_docs.Any(d => !IsEmptyDocument(d)))
        {
            bool? append = await Dialogs.AskReplaceOrAppendTabs(this, Path.GetFileName(path));
            if (append is null) return;
            if (append == false)
            {
                if (!await ConfirmDiscardAllTabs()) return;
                DropAllProjectTabs();
            }
        }

        var missing = new List<string>();
        Document? active = null;
        await RunProjectOpen(async () =>
        {
            for (int i = 0; i < session.Tabs.Count; i++)
            {
                var tab = session.Tabs[i];
                if (!IsEmptyDocument(_doc) && !await AddProjectTab()) break;
                await LoadSessionTab(tab, missing);
                _doc.CustomName = string.IsNullOrWhiteSpace(tab.Name) ? null : tab.Name;
                if (i == session.ActiveTab) active = _doc;
            }
        });
        if (active is not null) await SwitchToDocument(active);
        RebuildProjectTabStrip();

        CrashLog.Trace($"[session] loaded {path}: {session.Tabs.Count} tabs, {missing.Count} missing files");
        _settings.AddRecent(path);
        _settings.Save();
        RebuildRecentMenu();
        StatusText.Text = $"Loaded session {Path.GetFileName(path)}"
            + (missing.Count > 0 ? $". {missing.Count} missing file{(missing.Count == 1 ? "" : "s")}" : "");
        if (missing.Count > 0)
            await Dialogs.ShowMessage(this, "Some Session Files Are Missing",
                "These files were not found and were skipped:\n" + string.Join("\n", missing));
    }

    /// <summary>Loads one session tab's files into the current (empty) tab.</summary>
    private async Task LoadSessionTab(SessionTab tab, List<string> missing)
    {
        bool Exists(string? p)
        {
            if (p is null) return false;
            if (File.Exists(p)) return true;
            missing.Add(p);
            return false;
        }

        if (tab.Def is not null)
        {
            if (Exists(tab.Def)) await OpenDefFile(tab.Def);
            return;
        }
        if (Exists(tab.Sff)) OpenFile(tab.Sff!, recordRecent: false, loaded: await ReadSffInBackgroundAsync(tab.Sff!));
        if (Exists(tab.Air) && !LoadAnimationSource(tab.Air!))
            ShowPopup("Could Not Read the Animation File", $"{Path.GetFileName(tab.Air)}: {_airError ?? "unknown problem"}");
        if (Exists(tab.Snd) && !LoadSnd(tab.Snd!, await ReadSndInBackgroundAsync(tab.Snd!)))
            ShowPopup("Could Not Open File", $"Could not open {Path.GetFileName(tab.Snd)}: {_sndError ?? "unknown problem"}");
        var palettes = tab.Palettes.Where(Exists).ToList();
        if (palettes.Count > 0 && _file is null)
            await ImportPaletteFiles(palettes.Select(p => (Path.GetFileName(p), (string?)p)).ToList());
    }
}
