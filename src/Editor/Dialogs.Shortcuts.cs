using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;

namespace Editor;

public static partial class Dialogs
{
    /// <summary>One row of Preferences > Shortcuts. Only the Shortcut cell changes while the tab is open.</summary>
    public sealed class ShortcutRow(ShortcutCommand command) : INotifyPropertyChanged
    {
        public ShortcutCommand Command { get; } = command;
        public string Label => Command.Label;
        public string Where => Shortcuts.ScopeText(Command.Scope);

        private string _shortcutText = "";
        public string ShortcutText { get => _shortcutText; set => Set(ref _shortcutText, value, nameof(ShortcutText)); }
        private FontWeight _shortcutWeight = FontWeight.Normal;
        public FontWeight ShortcutWeight { get => _shortcutWeight; set => Set(ref _shortcutWeight, value, nameof(ShortcutWeight)); }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set<T>(ref T field, T value, string name)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>What built-in keys do with a chord, for the "use it anyway?" warning; null when nothing.
    /// <paramref name="menuKeys"/>: the menu bar's Alt+letter access keys and their menu names.</summary>
    private static string? BuiltInKeyUse(Chord c, IReadOnlyDictionary<Key, string> menuKeys)
    {
        if (c.Modifiers == KeyModifiers.Alt && menuKeys.TryGetValue(c.Key, out var menu)) return $"opening the {menu} menu";
        return c.Modifiers != KeyModifiers.None ? null : c.Key switch
        {
            Key.Left or Key.Right or Key.Up or Key.Down => "moving through lists and nudging boxes and sprites",
            Key.PageUp or Key.PageDown or Key.Home or Key.End => "moving through lists",
            Key.Delete => "deleting items",
            Key.Enter or Key.Tab => "editing table cells",
            _ => null,
        };
    }

    /// <summary>Keys the system keeps for itself, which can't be shortcuts.</summary>
    private static bool ReservedChord(Chord c) => c.Modifiers == KeyModifiers.Alt && c.Key == Key.F4;

    /// <summary>
    /// Preferences > Shortcuts: a table like the editor's lists (Where, Command, Shortcut), ordered by
    /// where each shortcut works, then table order. Double-click a Shortcut cell (or Enter/F2 on the selected
    /// row) and press keys; Escape cancels, and so does leaving the table. Right-click resets or removes.
    /// Edits go to <paramref name="bindings"/>, which the caller keeps on OK. Sized to the other pages,
    /// so the window doesn't grow with the command count; the list scrolls.
    /// </summary>
    private static (Control Page, Action Reset) ShortcutsPage(Window dialog, Dictionary<string, Chord?> bindings, IReadOnlyDictionary<Key, string> menuKeys,
        double width, double height)
    {
        var rows = Shortcuts.All.Select(c => new ShortcutRow(c)).OrderBy(r => r.Command.Scope).ToList(); // stable: table order within a scope
        ShortcutRow? capturing = null;

        void Refresh(ShortcutRow r)
        {
            var chord = bindings.GetValueOrDefault(r.Command.Id);
            bool changed = chord != r.Command.DefaultChord;
            r.ShortcutText = ReferenceEquals(r, capturing) ? "Press a key" : chord?.ToString() ?? "None";
            r.ShortcutWeight = changed ? FontWeight.Bold : FontWeight.Normal;
        }
        foreach (var r in rows) Refresh(r);

        // Fixed widths, shared by the header and every row, so they line up with or without a scroll bar.
        // Text here follows style-set sizes (windows: ControlContentThemeFontSize; tab content: the TabItem's
        // UiFont14) that dialog.FontSize may not show yet, so measure at the largest so nothing is cut.
        double Resource(string key) => Application.Current?.TryFindResource(key, out var v) == true && v is double d ? d : 0;
        double fontSize = new[] { dialog.FontSize, Resource("ControlContentThemeFontSize"), Resource("UiFont14") }.Max();
        double Measure(string s, FontWeight weight) =>
            new TextLayout(s, new Typeface(dialog.FontFamily, weight: weight), fontSize, null).Width;
        double Column(string header, IEnumerable<string> texts) =>
            Math.Ceiling(texts.Append(header).Max(t => Measure(t, FontWeight.Bold))) + 8 + 1 + 4; // padding, border, slack
        double whereWidth = Column("Where", rows.Select(r => r.Where));
        double shortcutWidth = Column("Shortcut", rows.Select(r => r.ShortcutText).Append("Press a key").Append("Ctrl+Shift+Page Down"));
        double[] widths = [whereWidth, 0, shortcutWidth];

        Border Cell(Control child, int column, bool header)
        {
            var b = new Border
            {
                Padding = header ? new Thickness(4, 2) : new Thickness(4, 1),
                BorderThickness = header ? new Thickness(0, 0, 1, 2) : new Thickness(0, 0, 1, 1),
                Background = Brushes.Transparent,
                Child = child,
            };
            if (column != 1) b.Width = widths[column];
            else b.MinWidth = 120;
            b.Bind(Border.BorderBrushProperty, b.GetResourceObservable(header ? "ThemeHeaderLine" : "ThemeLine"));
            Grid.SetColumn(b, column);
            return b;
        }
        Grid RowGrid() => new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var header = RowGrid();
        foreach (var (text, column) in new[] { ("Where", 0), ("Command", 1), ("Shortcut", 2) })
        {
            var t = new TextBlock { Text = text, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("ThemeMutedText"));
            header.Children.Add(Cell(t, column, header: true));
        }

        async Task SetBinding(ShortcutRow r, Chord? chord)
        {
            if (chord is { } c)
            {
                var other = rows.FirstOrDefault(o => !ReferenceEquals(o, r) && bindings.GetValueOrDefault(o.Command.Id) == c
                    && Shortcuts.Overlap(o.Command.Scope, r.Command.Scope));
                if (other is not null)
                {
                    if (!await Confirm(dialog, "Shortcut in Use", $"{c} is used by {other.Label}. Move it here?", "Move")) return;
                    bindings[other.Command.Id] = null;
                    Refresh(other);
                }
                else if (BuiltInKeyUse(c, menuKeys) is { } use
                    && !await Confirm(dialog, "Shortcut in Use", $"{c} is used for {use}. Use it here anyway?", "Use It"))
                    return;
            }
            bindings[r.Command.Id] = chord;
            Refresh(r);
        }

        async void Run(string name, Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) { CrashLog.Write(name, ex); }
        }

        void CancelCapture()
        {
            if (capturing is not { } r) return;
            capturing = null;
            Refresh(r);
        }

        void StartCapture(ShortcutRow r)
        {
            var previous = capturing;
            capturing = r;
            if (previous is not null) Refresh(previous);
            Refresh(r);
        }

        var list = new ListBox { SelectionMode = SelectionMode.Single, Background = ThemeBrush("ThemeListBg") };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                new Setter(ListBoxItem.MinHeightProperty, 0d),
            },
        });
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":selected"))
        {
            Setters = { new Setter(ListBoxItem.BackgroundProperty, ThemeBrush("ThemeSelection")) },
        });
        list.ItemTemplate = new FuncDataTemplate<ShortcutRow>((_, _) =>
        {
            var grid = RowGrid();
            static TextBlock Text() => new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var label = Text();
            void UpdateCommandTip()
            {
                if (label.Text is not { Length: > 0 } command || label.Bounds.Width <= 0) return;
                var layout = new TextLayout(command, new Typeface(label.FontFamily, weight: label.FontWeight), label.FontSize, null);
                ToolTip.SetTip(label, layout.Width > label.Bounds.Width + 0.5 ? command : null);
            }
            label.SizeChanged += (_, _) =>
            {
                try { UpdateCommandTip(); }
                catch (Exception ex) { CrashLog.Write("ShortcutsPage.CommandTooltip", ex); }
            };
            var key = Text();
            key.HorizontalAlignment = HorizontalAlignment.Center;
            var whereText = Text();
            whereText.Bind(TextBlock.ForegroundProperty, whereText.GetResourceObservable("ThemeMutedText"));

            // The list recycles row controls, so fill them from whichever row is attached now
            // instead of a reflection binding, which trimming can break.
            ShortcutRow? row = null;
            void Changed(object? sender, PropertyChangedEventArgs e)
            {
                if (row is null) return;
                try
                {
                    if (e.PropertyName is nameof(ShortcutRow.ShortcutText) or null) key.Text = row.ShortcutText;
                    if (e.PropertyName is nameof(ShortcutRow.ShortcutWeight) or null) key.FontWeight = row.ShortcutWeight;
                }
                catch (Exception ex) { CrashLog.Write("ShortcutsPage.RowChanged", ex); }
            }
            void Apply(ShortcutRow? next)
            {
                if (row is not null) row.PropertyChanged -= Changed;
                row = next;
                label.Text = next?.Label;
                key.Text = next?.ShortcutText;
                key.FontWeight = next?.ShortcutWeight ?? FontWeight.Normal;
                whereText.Text = next?.Where;
                if (next is not null) next.PropertyChanged += Changed;
            }
            grid.DataContextChanged += (_, _) => Apply(grid.DataContext as ShortcutRow);

            var keyCell = Cell(key, 2, header: false);
            grid.Cursor = new Cursor(StandardCursorType.Hand);
            grid.DoubleTapped += (_, _) => { if (grid.DataContext is ShortcutRow r) StartCapture(r); };
            grid.Children.Add(Cell(whereText, 0, header: false));
            grid.Children.Add(Cell(label, 1, header: false));
            grid.Children.Add(keyCell);

            var change = new MenuItem { Header = "Change Shortcut" };
            var remove = new MenuItem { Header = "Remove Shortcut" };
            var reset = new MenuItem { Header = "Reset to Default" };
            change.Click += (_, _) =>
            {
                if (grid.DataContext is ShortcutRow r) StartCapture(r);
            };
            reset.Click += (_, _) =>
            {
                if (grid.DataContext is ShortcutRow r) Run("ShortcutsPage.Reset", () => SetBinding(r, r.Command.DefaultChord));
            };
            remove.Click += (_, _) =>
            {
                if (grid.DataContext is ShortcutRow r) Run("ShortcutsPage.Remove", () => SetBinding(r, null));
            };
            grid.ContextMenu = new ContextMenu { ItemsSource = new[] { change, remove, reset } };
            return grid;
        });

        // While capturing, the dialog takes every key first: Tab and Ctrl+Tab must not move focus or switch pages.
        dialog.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (capturing is not { } r)
            {
                // Enter or F2 on the selected row starts a capture, so no mouse is needed.
                if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Enter or Key.F2
                    && list.IsKeyboardFocusWithin && list.SelectedItem is ShortcutRow selected)
                {
                    e.Handled = true;
                    StartCapture(selected);
                }
                return;
            }
            e.Handled = true;
            if (e.Key == Key.Escape) { CancelCapture(); return; }
            var key = Chord.Normalize(e.Key);
            if (!Chord.Bindable(key)) return; // a modifier on its own: keep waiting for the key
            var chord = new Chord(key, e.KeyModifiers);
            CancelCapture();
            if (ReservedChord(chord))
            {
                Run("ShortcutsPage.Capture", () => ShowMessage(dialog, "Shortcut Not Available", $"{chord} is kept by the system and can't be a shortcut."));
                return;
            }
            Run("ShortcutsPage.Capture", () => SetBinding(r, chord));
        }, RoutingStrategies.Tunnel);
        // A capture left waiting (clicked elsewhere, another Preferences page, another window) ends.
        list.PropertyChanged += (_, e) =>
        {
            if (e.Property == InputElement.IsKeyboardFocusWithinProperty && !list.IsKeyboardFocusWithin) CancelCapture();
        };
        dialog.Deactivated += (_, _) => CancelCapture();
        // Clicking anywhere (another row, blank space) also ends it; tunnel runs before a double-click restarts one.
        dialog.AddHandler(InputElement.PointerPressedEvent, (_, _) => CancelCapture(), RoutingStrategies.Tunnel, handledEventsToo: true);

        list.ItemsSource = rows;

        // Run by the dialog's Reset to Defaults while this tab shows.
        void ResetAll()
        {
            capturing = null;
            foreach (var c in Shortcuts.All) bindings[c.Id] = c.DefaultChord;
            foreach (var r in rows) Refresh(r);
        }

        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(header, 0);
        Grid.SetRow(list, 1);
        page.Children.Add(header);
        page.Children.Add(list);
        // Same top gap as the other Preferences pages; the size includes it, as theirs does.
        return (new Border { Padding = new Thickness(0, PreferencesPageTop, 0, 0), Width = width, Height = height, Child = page }, ResetAll);
    }
}
