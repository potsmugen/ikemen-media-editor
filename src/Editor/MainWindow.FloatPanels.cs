using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Editor;

/// <summary>
/// Floats a panel's body in an owned window at the docked panel's size and spot. Its own header
/// moves it, rolls it up and docks it. Windows hide while another tab is shown.
/// </summary>
public partial class MainWindow
{
    /// <summary>A side panel that can float: Border > Grid(header, body) in a tab-grid column.</summary>
    private sealed class FloatPanel(string title, int tab, Control body, Button toggle, Button floatButton)
    {
        public string Title { get; } = title;
        public int Tab { get; } = tab;
        public Control Body { get; } = body;
        public Button Toggle { get; } = toggle;
        public Button FloatButton { get; } = floatButton;

        public string Key => Body.Name ?? Title;

        // Set while floating.
        public Window? Window;
        public bool RolledUp;
        public Size ExpandedSize;
        public Action<bool>? SetRolledUp;
        public Panel? DockParent;
        public int DockIndex, DockRow;
        public readonly List<(Control Control, bool Visible)> Hidden = new();
        public readonly List<(ColumnDefinition Column, GridLength Width)> Columns = new();
    }

    private List<FloatPanel>? _floatPanels;
    private readonly List<Control> _busyDisabledBodies = new();
    private bool _mainClosing;
    private readonly HashSet<int> _floatRestoredTabs = new();
    private bool _forwardingKey;
    private IInputElement? _forwardedKeyFocus;

    // Built on first use: x:Name fields exist only after InitializeComponent.
    private List<FloatPanel> FloatPanels => _floatPanels ??= new()
    {
        new(SpriteListLabel.Text ?? "", 0, SpriteListBody, SpriteListToggle, SpriteListFloat),
        new(PaintPanelLabel.Text ?? "", 0, PaintPanelBody, PaintPanelToggle, PaintPanelFloat),
        new(SpritePaletteLabel.Text ?? "", 0, SpritePaletteBody, SpritePaletteToggle, SpritePaletteFloat),
        new(PaletteListLabel.Text ?? "", 1, PaletteListBody, PaletteListToggle, PaletteListFloat),
        new(PaletteRightLabel.Text ?? "", 1, PaletteRightBody, PaletteRightToggle, PaletteRightFloat),
        new(ActionListLabel.Text ?? "", 2, ActionListBody, ActionListToggle, ActionListFloat),
        new(FramePanelLabel.Text ?? "", 2, FramePanelBody, FramePanelToggle, FramePanelFloat),
        new(SoundListLabel.Text ?? "", 3, SoundListBody, SoundListToggle, SoundListFloat),
    };

    /// <summary>The focused element for shortcut routing: the floating window's while forwarding its keys.</summary>
    private IInputElement? KeyFocus() => _forwardingKey ? _forwardedKeyFocus : FocusManager?.GetFocusedElement();

    /// <summary>Float buttons show only on expanded, docked panels.</summary>
    private void SyncFloatButtons()
    {
        foreach (var p in FloatPanels) p.FloatButton.IsVisible = p.Window is null && p.Body.IsVisible;
    }

    private async void OnFloatPanel(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (FloatPanels.FirstOrDefault(p => ReferenceEquals(p.FloatButton, sender)) is { } panel) Float(panel);
            else CrashLog.Error(nameof(OnFloatPanel), "no floating panel for this button.");
        }
        catch (Exception ex) { await Failed(nameof(OnFloatPanel), ex); }
    }

    private void Float(FloatPanel p, bool restoring = false)
    {
        if (p.Window is not null || !p.Body.IsVisible) return;
        // Border > Grid(header, body), in a grid column with its separator column beside it.
        Control? host = (p.Body.Parent as Control)?.Parent as Control;
        if (p.Body.Parent is not Panel dock || host?.Parent is not Grid grid)
        {
            CrashLog.Error(nameof(Float), $"unexpected layout for {p.Title}; not floating.");
            return;
        }

        var size = host.Bounds.Size;
        var position = host.PointToScreen(new Point(0, 0));
        // Floating by hand starts where the panel is docked. A restart restore reopens it as it was;
        // a saved spot off every monitor falls back to the docked one.
        var saved = restoring ? SavedPlacement(p) : null;
        if (saved is not null)
        {
            size = new Size(saved.Width, saved.Height);
            var at = new PixelPoint(saved.X, saved.Y);
            if (OnAnyScreen(at, size.Width, p.Toggle.Height)) position = at;
        }
        p.DockParent = dock;
        p.DockIndex = dock.Children.IndexOf(p.Body);
        p.DockRow = Grid.GetRow(p.Body);
        dock.Children.Remove(p.Body);

        p.Hidden.Clear();
        p.Columns.Clear();
        int col = Grid.GetColumn(host), separator = col == 0 ? 1 : col - 1;
        foreach (var child in grid.Children)
            if (Grid.GetColumn(child) is var c && (c == col || c == separator)) p.Hidden.Add((child, child.IsVisible));
        foreach (int c in new[] { col, separator })
            if (c < grid.ColumnDefinitions.Count) p.Columns.Add((grid.ColumnDefinitions[c], grid.ColumnDefinitions[c].Width));
        foreach (var (control, _) in p.Hidden) control.IsVisible = false;
        foreach (var (column, _) in p.Columns) column.Width = new GridLength(0);

        var window = new Window
        {
            Title = p.Title,
            Icon = Icon,
            ShowInTaskbar = false,
            SystemDecorations = SystemDecorations.BorderOnly, // our own title bar; no minimize or maximize
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = position,
            Width = size.Width,
            Height = size.Height,
        };
        window.Bind(BackgroundProperty, window.GetResourceObservable("ThemePanelBg"));
        window.Bind(ForegroundProperty, window.GetResourceObservable("ThemeText"));
        window.Content = BuildFloatContent(p, window);
        if (restoring)
        {
            window.ShowActivated = false; // don't take focus from the main window at startup
            if (saved?.RolledUp == true) p.SetRolledUp?.Invoke(true);
        }
        window.KeyDown += (_, e) => { if (!e.Handled) ForwardFloatingKey(window, e); };
        WireListNavKeys(window);
        window.Closed += (_, _) => { if (ReferenceEquals(p.Window, window) && !_mainClosing) Dock(p, closeWindow: false); };
        p.Window = window;
        SyncFloatButtons();
        window.Show(this);
    }

    /// <summary>Puts the body back in its docked slot and restores what floating hid or zeroed.</summary>
    /// <summary>Docking is a reset: the saved placement goes too, so the next float starts docked.</summary>
    private void Dock(FloatPanel p, bool closeWindow)
    {
        if (p.Window is not { } window) return;
        _settings.FloatingPanels.Remove(p.Key);
        p.Window = null;
        p.RolledUp = false;
        p.SetRolledUp = null;
        try
        {
            if (p.Body.Parent is Panel floating) floating.Children.Remove(p.Body);
            window.Content = null;
            p.Body.IsVisible = true; // expanded, even if it was rolled up
            Grid.SetRow(p.Body, p.DockRow);
            if (p.DockParent is { } dock) dock.Children.Insert(Math.Clamp(p.DockIndex, 0, dock.Children.Count), p.Body);
            foreach (var (control, visible) in p.Hidden) control.IsVisible = visible;
            foreach (var (column, width) in p.Columns) column.Width = width;
            p.Hidden.Clear();
            p.Columns.Clear();
            p.DockParent = null;
            SyncFloatButtons();
        }
        catch (Exception ex)
        {
            CrashLog.Write(nameof(Dock), ex);
        }
        if (closeWindow) window.Close();
    }

    /// <summary>Reset Panel Sizes: docks the tab's floating panels and forgets their spots.</summary>
    private void ResetFloatingPanels(int tab)
    {
        foreach (var p in FloatPanels.Where(p => p.Tab == tab))
        {
            Dock(p, closeWindow: true);
            _settings.FloatingPanels.Remove(p.Key); // also entries for panels not yet restored
        }
    }

    /// <summary>Reopens the panels that were floating at exit, the first time their tab is shown.</summary>
    private void RestoreFloatingPanels(int tab)
    {
        if (!_floatRestoredTabs.Add(tab)) return;
        // After layout, so the docked spot is known for the off-screen fallback.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (Tabs?.SelectedIndex != tab) { _floatRestoredTabs.Remove(tab); return; }
                foreach (var p in FloatPanels)
                    if (p.Tab == tab && p.Window is null && p.Body.IsVisible && SavedPlacement(p) is { Floating: true })
                        Float(p, restoring: true);
            }
            catch (Exception ex) { CrashLog.Write(nameof(RestoreFloatingPanels), ex); }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The panel's saved placement, or null when missing or unusable (logged).</summary>
    private FloatPanelPlacement? SavedPlacement(FloatPanel p)
    {
        if (!_settings.FloatingPanels.TryGetValue(p.Key, out var s) || s is null) return null;
        if (double.IsFinite(s.Width) && double.IsFinite(s.Height) && s.Width > 0 && s.Height > 0) return s;
        CrashLog.Error(nameof(SavedPlacement), $"ignoring FloatingPanels.{p.Key}: size {s.Width}x{s.Height}.");
        return null;
    }

    private void RecordPlacement(FloatPanel p, Window window)
    {
        var size = p.RolledUp ? p.ExpandedSize : window.ClientSize;
        _settings.FloatingPanels[p.Key] = new FloatPanelPlacement
        {
            Floating = true, RolledUp = p.RolledUp,
            X = window.Position.X, Y = window.Position.Y,
            Width = size.Width, Height = size.Height,
        };
    }

    /// <summary>Saved with the panel sizes: open windows reopen floating next session.</summary>
    private void SaveFloatPlacements()
    {
        foreach (var p in FloatPanels)
            if (p.Window is { } window) RecordPlacement(p, window);
    }

    /// <summary>Whether the window's header strip would touch any monitor's working area.</summary>
    private bool OnAnyScreen(PixelPoint position, double width, double headerHeight)
    {
        double scale = DesktopScaling;
        var header = new PixelRect(position, new PixelSize(Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(headerHeight * scale))));
        return Screens?.All.Any(s => s.WorkingArea.Intersects(header)) == true;
    }

    /// <summary>A column's width as docked, so a floating panel's zeroed column isn't saved.</summary>
    private double DockedWidth(Grid grid, int column)
    {
        var def = grid.ColumnDefinitions[column];
        foreach (var p in FloatPanels)
            foreach (var (c, w) in p.Columns)
                if (ReferenceEquals(c, def)) return w.Value;
        return def.Width.Value;
    }

    /// <summary>The docked layout plus a draggable header that rolls the window up and docks it.</summary>
    private Control BuildFloatContent(FloatPanel p, Window window)
    {
        Button HeaderButton(string glyph, string tip)
        {
            // Sized like the docked toggle.
            var b = new Button
            {
                Content = glyph, Width = p.Toggle.Width, Height = p.Toggle.Height, FontSize = p.Toggle.FontSize,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(b, tip);
            return b;
        }
        var rollUp = HeaderButton("▲", "Collapse this panel");
        var close = HeaderButton("✕", "Dock this panel");
        Grid.SetColumn(close, 2);
        var label = new TextBlock { Text = p.Title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("ThemeMutedText"));
        Grid.SetColumn(label, 1);

        // A background makes the empty part of the header hit-testable for dragging.
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        header.Bind(Panel.BackgroundProperty, header.GetResourceObservable("ThemePanelBg"));
        header.Children.Add(rollUp);
        header.Children.Add(label);
        header.Children.Add(close);

        // Moved by hand: BeginMoveDrag hands the drag to the OS, which snaps to screen edges on Windows.
        PixelPoint dragStart = default, windowStart = default;
        header.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
            dragStart = header.PointToScreen(e.GetPosition(header));
            windowStart = window.Position;
            e.Pointer.Capture(header);
            e.Handled = true;
        };
        header.PointerMoved += (_, e) =>
        {
            if (!ReferenceEquals(e.Pointer.Captured, header)) return;
            var now = header.PointToScreen(e.GetPosition(header));
            window.Position = new PixelPoint(windowStart.X + now.X - dragStart.X, windowStart.Y + now.Y - dragStart.Y);
        };
        header.PointerReleased += (_, e) => { if (ReferenceEquals(e.Pointer.Captured, header)) e.Pointer.Capture(null); };

        p.SetRolledUp = rolled =>
        {
            bool expand = !rolled;
            if (expand == p.Body.IsVisible) return;
            if (rolled && window.IsVisible) p.ExpandedSize = window.ClientSize;
            else if (rolled) p.ExpandedSize = new Size(window.Width, window.Height); // not shown yet
            p.RolledUp = rolled;
            p.Body.IsVisible = expand;
            rollUp.Content = expand ? "\u25B2" : "\u25BC";
            // Rolled up, the window shrinks to fit its header and can't be resized until expanded again.
            window.CanResize = expand;
            window.SizeToContent = expand ? SizeToContent.Manual : SizeToContent.WidthAndHeight;
            if (expand && p.ExpandedSize.Width > 0)
            {
                window.Width = p.ExpandedSize.Width;
                window.Height = p.ExpandedSize.Height;
            }
        };
        rollUp.Click += (_, _) => p.SetRolledUp?.Invoke(!p.RolledUp);
        close.Click += (_, _) => Dock(p, closeWindow: true);

        header.Margin = new Thickness(0, 0, 0, 4); // as docked
        Grid.SetRow(p.Body, 1);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        layout.Children.Add(header);
        layout.Children.Add(p.Body);

        // No system frame, so outline the window like a panel border, open or rolled up.
        double outline = this.TryFindResource("PanelSeparatorWidth", out var w) && w is double d ? d : 1;
        var host = new Border { Child = layout, BorderThickness = new Thickness(outline) };
        host.Bind(Border.PaddingProperty, host.GetResourceObservable("PanelContentPadding"));
        host.Bind(Border.BorderBrushProperty, host.GetResourceObservable("ThemeSeparator"));
        return host;
    }

    /// <summary>Floating panels only act on their own tab, so they hide elsewhere.</summary>
    private void SyncFloatingVisibility()
    {
        int tab = Tabs?.SelectedIndex ?? 0;
        foreach (var p in FloatPanels)
        {
            if (p.Window is not { } window) continue;
            if (p.Tab == tab && !window.IsVisible) window.Show(this); // Hide clears the owner, so pass it again
            else if (p.Tab != tab && window.IsVisible) window.Hide();
        }
    }

    /// <summary>Busy lock: floating bodies are outside Tabs, so disable them directly.</summary>
    private void SetFloatingBusy(bool busy)
    {
        if (busy)
        {
            foreach (var p in FloatPanels)
                if (p.Window is not null && p.Body.IsEnabled) { p.Body.IsEnabled = false; _busyDisabledBodies.Add(p.Body); }
        }
        else
        {
            foreach (var body in _busyDisabledBodies) body.IsEnabled = true;
            _busyDisabledBodies.Clear();
        }
    }

    /// <summary>Main window closing: close floating windows without re-docking into it.</summary>
    private void CloseFloatingPanels()
    {
        _mainClosing = true;
        foreach (var p in FloatPanels) p.Window?.Close();
    }

    /// <summary>Unhandled keys in a floating window run the main window's shortcuts.</summary>
    private void ForwardFloatingKey(Window window, KeyEventArgs e)
    {
        _forwardedKeyFocus = window.FocusManager?.GetFocusedElement();
        _forwardingKey = true;
        try { OnKeyDown(e); }
        catch (Exception ex) { CrashLog.Write(nameof(ForwardFloatingKey), ex); }
        finally
        {
            _forwardingKey = false;
            _forwardedKeyFocus = null;
        }
    }
}
