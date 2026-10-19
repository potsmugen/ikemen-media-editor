using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Sff;

namespace Editor;

/// <summary>
/// Project tabs: each tab is a Document (a .def's files, or loose files). Switching swaps _doc
/// and redraws every sub-tab from the stored state; nothing is reloaded from disk.
/// </summary>
public partial class MainWindow
{
    private readonly List<Document> _docs = [];
    /// <summary>Set across a multi-file open, so a tab click can't land between its steps.</summary>
    private bool _projectOpening;
    /// <summary>The tab being pressed or dragged, and where the press started (strip coordinates).</summary>
    private Document? _tabDragDoc;
    private Point _tabDragStart;
    private bool _tabDragging;
    private double _tabDragThreshold = Settings.DefaultTabDragThresholdPixels;

    /// <summary>Called once from the constructor, after InitializeComponent.</summary>
    private void WireProjectTabs()
    {
        _docs.Add(_doc);
        WireUndo(_doc.Undo);
        // Tunnel: Ctrl+Tab must arrive before keyboard focus navigation takes the Tab key.
        AddHandler(KeyDownEvent, OnProjectTabKeyDown, RoutingStrategies.Tunnel);
        _tabDragThreshold = Settings.Checked(nameof(Settings.TabDragThresholdPixels), _settings.TabDragThresholdPixels,
            Settings.DefaultTabDragThresholdPixels, v => double.IsFinite(v) && v >= 0);
        // The pointer is captured by the strip, not a tab: reordering rebuilds the tabs mid-drag.
        ProjectTabStrip.PointerMoved += OnProjectTabStripPointerMoved;
        ProjectTabStrip.PointerReleased += (_, _) => EndTabDrag();
        ProjectTabStrip.PointerCaptureLost += (_, _) => EndTabDrag();
        RebuildProjectTabStrip();
    }

    private void WireUndo(UndoStack undo)
    {
        undo.Changed += () =>
        {
            if (!ReferenceEquals(undo, _undo)) return;
            SyncUndoMenu();
            SyncDirtyFromUndo();
        };
        undo.CaptureView = CaptureUndoView;
    }

    private static bool IsEmptyDocument(Document d) =>
        d.File is null && d.Air is null && d.Snd is null && d.StandalonePalettes.Count == 0;

    private bool DocumentHasUnsavedWork(Document d) => ReferenceEquals(d, _doc)
        ? _paintBaseline is not null || _soundEditBaseline is not null || HasPendingColorAdjust()
            || _dirty || _airDirty || _sndDirty || HasDirtyExternalPalettes() || HasDirtyStandalonePalettes()
        : d.Dirty || d.AirDirty || d.SndDirty || d.StandalonePalettes.Any(p => p.Dirty)
            || d.File?.Palettes.Any(p => p.Source == PaletteSource.External && p.Dirty) == true;

    private static string? DocumentPath(Document d) => d.DefPath ?? d.Path ?? d.AirPath ?? d.SndPath;

    /// <summary>The .def's name= field, else the .def or first file's name, else "Untitled".</summary>
    private static string AutomaticName(Document d) =>
        d.DefName ?? (DocumentPath(d) is { } p ? System.IO.Path.GetFileName(p) : "Untitled");

    private string DocumentLabel(Document d)
    {
        static string BaseName(Document x) => x.CustomName ?? AutomaticName(x);
        string name = BaseName(d);
        int same = _docs.TakeWhile(x => !ReferenceEquals(x, d)).Count(x => BaseName(x) == name);
        if (same > 0) name += $" ({same + 1})";
        return (DocumentHasUnsavedWork(d) ? "* " : "") + name;
    }

    /// <summary>Redraws the strip. Runs from UpdateTitle, so every open, close and dirty change reaches it.</summary>
    private void RebuildProjectTabStrip()
    {
        if (ProjectTabStrip is null || _docs.Count == 0) return;
        ProjectTabStrip.Children.Clear();
        foreach (var doc in _docs)
        {
            var label = new TextBlock { Text = DocumentLabel(doc), VerticalAlignment = VerticalAlignment.Center };
            // Drawn, not a ✕ glyph: the symbol font doesn't sit centered in its line.
            var close = new Button
            {
                Classes = { "projectTabButton" },
                Content = new Avalonia.Controls.Shapes.Path { Data = Avalonia.Media.Geometry.Parse("M0,0 L8,8 M8,0 L0,8") },
            };
            ToolTip.SetTip(close, "Close tab");
            close.Click += async (_, e) =>
            {
                e.Handled = true;
                try { await CloseProjectTab(doc); }
                catch (Exception ex) { await Failed("CloseProjectTab", ex); }
            };
            var tab = new Border
            {
                Classes = { "projectTab" },
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { label, close } },
            };
            if (ReferenceEquals(doc, _doc)) tab.Classes.Add("selected");
            if (DocumentPath(doc) is { } path) ToolTip.SetTip(tab, path);
            tab.ContextRequested += (_, e) =>
            {
                e.Handled = true;
                ShowProjectTabMenu(tab, doc);
            };
            tab.PointerPressed += async (_, e) =>
            {
                try
                {
                    var props = e.GetCurrentPoint(tab).Properties;
                    if (props.IsMiddleButtonPressed) { e.Handled = true; await CloseProjectTab(doc); }
                    else if (props.IsLeftButtonPressed)
                    {
                        e.Handled = true;
                        _tabDragDoc = doc;
                        _tabDragging = false;
                        _tabDragStart = e.GetPosition(ProjectTabStrip);
                        e.Pointer.Capture(ProjectTabStrip);
                        await SwitchToDocument(doc);
                    }
                }
                catch (Exception ex) { await Failed("ProjectTabPressed", ex); }
            };
            ProjectTabStrip.Children.Add(tab);
        }
        SyncProjectTabBarEnabled();
    }

    private void SyncProjectTabBarEnabled()
    {
        if (ProjectTabBar is not null) ProjectTabBar.IsEnabled = !_busy && !_projectOpening;
    }

    /// <summary>Runs a multi-file open (a .def) with tab switching blocked throughout.</summary>
    private async Task RunProjectOpen(Func<Task> open)
    {
        _projectOpening = true;
        SyncProjectTabBarEnabled();
        try { await open(); }
        finally
        {
            _projectOpening = false;
            SyncProjectTabBarEnabled();
        }
    }

    /// <summary>
    /// Before a .def opens: an empty tab takes it; otherwise asks New Tab or Current Tab, and
    /// Current Tab goes through the usual unsaved-changes check. False if canceled.
    /// </summary>
    private async Task<bool> PrepareTabForProject(string path)
    {
        if (!IsEmptyDocument(_doc))
        {
            bool? newTab = await Dialogs.AskOpenInNewTab(this, System.IO.Path.GetFileName(path));
            if (newTab is null) return false;
            if (newTab == true) return await AddProjectTab();
        }
        return await ConfirmDiscardEverything(resolveStandalone: false);
    }

    /// <summary>Adds an empty tab at the right end and shows it. False if the switch was canceled.</summary>
    private async Task<bool> AddProjectTab()
    {
        if (_busy) return false;
        var doc = new Document();
        WireUndo(doc.Undo);
        doc.Undo.Limit = _undo.Limit;
        doc.Undo.MergeWindow = _undo.MergeWindow;
        _docs.Add(doc);
        if (await SwitchToDocument(doc)) return true;
        _docs.Remove(doc);
        RebuildProjectTabStrip();
        return false;
    }

    /// <summary>Shows another tab. Pending paint and sound edits are resolved first (Cancel stays put).</summary>
    private async Task<bool> SwitchToDocument(Document target)
    {
        if (ReferenceEquals(target, _doc)) return true;
        if (_busy || !_docs.Contains(target)) return false;
        if (!await ResolvePendingPaintBeforeSwitch()) return false;
        if (!await ResolvePendingSoundEditsBeforeSwitch()) return false;
        DiscardPendingColorAdjust();
        LeaveDocument();
        CrashLog.Trace($"[tabs] switch to {DocumentPath(target) ?? "(untitled)"}");
        _doc = target;
        ShowDocument();
        return true;
    }

    /// <summary>Stops what belongs to the outgoing tab's screen: playback, picks, the eyedropper.</summary>
    private void LeaveDocument()
    {
        Pause();
        StopPlayback();
        CancelPick(restoreSelection: false);
        CancelPaletteLinkPick(restoreSelection: false);
        DisarmEyedropper();
        DisarmPreviewEyedropper();
        _doc.SpriteView = SpriteCanvas.View;
        _doc.AnimView = AnimCanvas.View;
    }

    /// <summary>Redraws every sub-tab from the active Document, keeping its selection, dirty state and undo.
    /// The sub-tab shown doesn't change.</summary>
    private void ShowDocument()
    {
        var keepAction = _action;
        int keepOverride = _paletteOverride;
        _palettePreviewSprite = null;
        _animSprite = null;
        _editingPalette = null;
        _editingView = null;
        _selectedSwatch = -1;
        _visible = [];

        _suppressEvents = true;
        FilterBox.Text = "";
        if (ActionFilterBox is not null) ActionFilterBox.Text = "";
        if (SoundFilterBox is not null) SoundFilterBox.Text = "";
        _suppressEvents = false;

        // Sprites and palettes
        SpriteList.ItemsSource = null;
        PaletteBox.ItemsSource = null;
        PaletteList.ItemsSource = null;
        SpriteCanvas.Show(null, null, false);
        SpriteCanvas.ShowOnion(null, null);
        PalettePreview.Show(null, null, false);
        PalettePreview.ShowOnion(null, null);
        ShowSpriteSwatches(null);
        BigSwatches.Show(null);
        if (_file is not null || _standalonePalettes.Count > 0) RebuildPaletteList();
        if (_file is not null)
        {
            if (PaletteBox.ItemCount > 0) PaletteBox.SelectedIndex = Math.Clamp(keepOverride + 1, 0, PaletteBox.ItemCount - 1);
            ApplyFilter(); // keeps _current selected
        }
        if (_doc.SpriteView is { } view) SpriteCanvas.View = view;
        else SpriteCanvas.ResetView();

        // Animations
        _action = null;
        _tick = 0;
        AnimCanvas.Show(null, null, false);
        AnimCanvas.Boxes = [];
        AnimCanvas.GhostBoxes = [];
        AnimCanvas.ShowOnion(null, null);
        AnimCanvas.SelectBox(-1);
        AnimCanvas.InvalidateVisual();
        FrameList.ItemsSource = null;
        ActionList.ItemsSource = null;
        TimelineView.Show([], -1);
        if (_air is not null)
        {
            RebuildActionList();
            _suppressEvents = true;
            ActionList.SelectedIndex = -1;
            _suppressEvents = false;
            if (ActionList.ItemsSource is List<ActionRow> rows && rows.Count > 0)
            {
                int row = rows.FindIndex(r => ReferenceEquals(r.Action, keepAction));
                ActionList.SelectedIndex = row >= 0 ? row : 0;
            }
        }
        if (_doc.AnimView is { } animView) AnimCanvas.View = animView;
        else AnimCanvas.ResetView();
        UpdateFrameButtons();
        UpdateAirInfo();

        // Sounds
        SoundList.ItemsSource = null;
        _visibleSounds = [];
        if (_snd is not null) ApplySoundFilter();
        else SndStatusText.Text = "";
        ShowSound();
        UpdateSndInfo();

        SyncOnionControls();
        SyncAnimOnionControls();

        // The Sprites/Palettes/Animations/Sounds tab stays as it is across project tabs.
        if (Tabs?.SelectedIndex == 1) RefreshPaletteTab();

        UpdateSffInfo();
        UpdateStatus();
        UpdateTitle();
        SyncSaveMenu();
        SyncUndoMenu();
        FocusTabList();
    }

    /// <summary>Closes a tab after the usual unsaved-changes check. The last tab is emptied instead of removed.</summary>
    private async Task CloseProjectTab(Document doc)
    {
        if (_busy || _projectOpening) return;
        if (!await SwitchToDocument(doc)) return;
        if (!await ConfirmCloseTab()) return;
        ResetAllState();
        CrashLog.Trace($"[tabs] closed tab {_docs.IndexOf(doc) + 1} of {_docs.Count}");
        if (_docs.Count == 1)
        {
            doc.CustomName = null;
            RebuildProjectTabStrip();
            return;
        }
        int at = _docs.IndexOf(doc);
        LeaveDocument();
        _docs.RemoveAt(at);
        _doc = _docs[Math.Min(at, _docs.Count - 1)];
        ShowDocument();
    }

    private void ShowProjectTabMenu(Control anchor, Document doc)
    {
        int at = _docs.IndexOf(doc);
        bool free = !_busy && !_projectOpening;
        string? folder = DocumentPath(doc) is { } p ? System.IO.Path.GetDirectoryName(p) : null;
        var menu = new ContextMenu
        {
            ItemsSource = new List<Control>
            {
                MenuAction("Rename", () => RunTabCommand("RenameProjectTab", () => RenameProjectTab(doc)), free),
                new Separator(),
                MenuAction("Close", () => RunTabCommand("CloseProjectTab", () => CloseProjectTab(doc)), free),
                MenuAction("Close Others", () => RunTabCommand("CloseOtherProjectTabs", () => CloseOtherProjectTabs(doc, rightOnly: false)),
                    free && _docs.Count > 1),
                MenuAction("Close Tabs to the Right", () => RunTabCommand("CloseProjectTabsToRight", () => CloseOtherProjectTabs(doc, rightOnly: true)),
                    free && at < _docs.Count - 1),
                new Separator(),
                MenuAction("Open Containing Folder", () => OpenProjectTabFolder(folder!), folder is not null),
            },
        };
        OpenContextMenu(anchor, menu);
    }

    private async void RunTabCommand(string name, Func<Task> command)
    {
        try { await command(); }
        catch (Exception ex) { await Failed(name, ex); }
    }

    /// <summary>An empty name goes back to the automatic .def/file name.</summary>
    private async Task RenameProjectTab(Document doc)
    {
        string? name = await Dialogs.PromptText(this, "Rename Tab", "Name", doc.CustomName ?? AutomaticName(doc));
        if (name is null) return;
        name = name.Trim();
        doc.CustomName = name.Length == 0 || name == AutomaticName(doc) ? null : name;
        RebuildProjectTabStrip();
    }

    /// <summary>Closes every other tab (or only those right of <paramref name="keep"/>), each with its own unsaved check. Cancel stops.</summary>
    private async Task CloseOtherProjectTabs(Document keep, bool rightOnly)
    {
        var targets = rightOnly ? _docs.Skip(_docs.IndexOf(keep) + 1).ToList() : _docs.Where(d => !ReferenceEquals(d, keep)).ToList();
        foreach (var doc in targets)
        {
            await CloseProjectTab(doc);
            if (_docs.Contains(doc)) break; // canceled
        }
        await SwitchToDocument(keep);
    }

    private void OpenProjectTabFolder(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CrashLog.Write("OpenProjectTabFolder", ex);
            StatusText.Text = $"Could not open {folder}: {ex.Message}";
        }
    }

    /// <summary>After ConfirmDiscardAllTabs: drops every tab without asking again, leaving one empty tab.</summary>
    private void DropAllProjectTabs()
    {
        LeaveDocument();
        foreach (var d in _docs.Where(d => !ReferenceEquals(d, _doc))) DiscardRecoveryForTab(d);
        _docs.RemoveAll(d => !ReferenceEquals(d, _doc));
        ResetAllState();
        _doc.CustomName = null;
        RebuildProjectTabStrip();
    }

    /// <summary>
    /// Closing the current tab or quitting: pending paint and sound edits get their own prompt, then
    /// unsaved files get Save / Discard / Cancel. Save runs Save All for the tab and goes on only if
    /// nothing is left unsaved (a canceled Save As, or a temp palette, which Save All never saves).
    /// </summary>
    private async Task<bool> ConfirmCloseTab()
    {
        if (!await ResolvePendingPaintBeforeSwitch()) return false;
        if (!await ResolvePendingSoundEditsBeforeSwitch()) return false;
        DiscardPendingColorAdjust(); // no dialog, as elsewhere: Commit is the only way to keep a slider preview
        if (!DocumentHasUnsavedWork(_doc)) return true;
        var what = new List<string>();
        if (_dirty) what.Add("sprites");
        if (_airDirty) what.Add("animations");
        if (_sndDirty) what.Add("sounds");
        if (HasDirtyExternalPalettes()) what.Add("external palettes");
        if (HasDirtyStandalonePalettes()) what.Add("standalone palettes");
        switch (await Dialogs.AskToSaveTab(this, _doc.CustomName ?? AutomaticName(_doc), string.Join(", ", what)))
        {
            case SaveChoice.Save:
                await SaveAllInTab();
                return !DocumentHasUnsavedWork(_doc);
            case SaveChoice.Discard:
                return true;
            default:
                return false;
        }
    }

    /// <summary>Quitting: each tab with unsaved work is shown and asked about in turn. False if any was canceled.</summary>
    private async Task<bool> ConfirmDiscardAllTabs()
    {
        foreach (var doc in _docs.ToList())
        {
            if (!DocumentHasUnsavedWork(doc)) continue;
            if (!await SwitchToDocument(doc)) return false;
            if (!await ConfirmCloseTab()) return false;
        }
        return true;
    }

    /// <summary>Past the threshold, a pressed tab follows the pointer: it moves to the slot under it.</summary>
    private void OnProjectTabStripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tabDragDoc is not { } doc || _busy || _projectOpening) return;
        var p = e.GetPosition(ProjectTabStrip);
        if (!_tabDragging)
        {
            if (Math.Abs(p.X - _tabDragStart.X) < _tabDragThreshold) return;
            _tabDragging = true;
        }
        int from = _docs.IndexOf(doc);
        if (from < 0) { EndTabDrag(); return; }
        int to = from;
        for (int i = 0; i < ProjectTabStrip.Children.Count; i++)
        {
            var b = ProjectTabStrip.Children[i].Bounds;
            if (p.X < b.X + b.Width) { to = i; break; }
            to = i;
        }
        if (to == from) return;
        _docs.RemoveAt(from);
        _docs.Insert(to, doc);
        RebuildProjectTabStrip();
    }

    private void EndTabDrag()
    {
        _tabDragDoc = null;
        _tabDragging = false;
    }

    private async void OnNewProjectTab(object? sender, RoutedEventArgs e)
    {
        try { if (!_projectOpening) await AddProjectTab(); }
        catch (Exception ex) { await Failed("OnNewProjectTab", ex); }
    }

    private async void OnCloseProjectTab(object? sender, RoutedEventArgs e)
    {
        try { await CloseProjectTab(_doc); }
        catch (Exception ex) { await Failed("OnCloseProjectTab", ex); }
    }

    private async void OnProjectTabKeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            if (_busy || _projectOpening) return;
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            if (!ctrl || alt) return;
            if (e.Key == Key.Tab)
            {
                e.Handled = true;
                if (_docs.Count < 2) return;
                int at = _docs.IndexOf(_doc) + (shift ? -1 : 1);
                await SwitchToDocument(_docs[(at + _docs.Count) % _docs.Count]);
            }
            else if (e.Key == Key.T && !shift) { e.Handled = true; await AddProjectTab(); }
            else if (e.Key == Key.W && !shift) { e.Handled = true; await CloseProjectTab(_doc); }
        }
        catch (Exception ex)
        {
            await Failed("OnProjectTabKeyDown", ex);
        }
    }
}
