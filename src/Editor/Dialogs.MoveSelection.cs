using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace Editor;

/// <summary>Where Move Selection puts the selected items.</summary>
public enum MoveTarget { Top, Bottom, Before }

/// <summary>An item Move Selection can move before: its list position, the text the box takes
/// when it's picked (e.g. "5000,1"), and how the suggestions show it (e.g. "5000,1 (#58)").</summary>
public sealed record MoveCandidate(int Position, string Key, string Label)
{
    // The autocomplete box writes an item's ToString into the box when it's picked
    public override string ToString() => Key;
}

/// <summary>A Move Selection answer: where to, the typed text, and the list position to move before (Before only, else -1).</summary>
public sealed record MoveSelectionChoice(MoveTarget Target, string Text, int Position = -1);

public static partial class Dialogs
{
    /// <summary>
    /// Asks where to move the selected list items: to the top, the bottom, or before an item. Typing in
    /// the Before box suggests the candidates that matches accepts; picking one targets that exact item.
    /// Typed text that wasn't picked goes through find (list position, or -1 for no match). Order is
    /// Top, Before, Bottom, as in the list.
    /// Move stays disabled until Before has a target. Returns null if canceled.
    /// </summary>
    public static Task<MoveSelectionChoice?> PromptMoveSelection(Window owner, string watermark,
        IReadOnlyList<MoveCandidate> candidates, Func<int, string, bool> matches, Func<string, int> find,
        MoveSelectionChoice initial)
    {
        var result = new TaskCompletionSource<MoveSelectionChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var top = new RadioButton { Content = "Top", GroupName = "MoveTarget", IsChecked = initial.Target == MoveTarget.Top };
        var bottom = new RadioButton { Content = "Bottom", GroupName = "MoveTarget", IsChecked = initial.Target == MoveTarget.Bottom };
        var before = new RadioButton
        {
            Content = "Before", GroupName = "MoveTarget", IsChecked = initial.Target == MoveTarget.Before,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new AutoCompleteBox
        {
            ItemsSource = candidates,
            FilterMode = AutoCompleteFilterMode.Custom,
            MinimumPrefixLength = 1,
            IsTextCompletionEnabled = false,
            Watermark = watermark,
            Text = initial.Text,
            MinWidth = 220,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
            ItemTemplate = new FuncDataTemplate<MoveCandidate>((c, _) => new TextBlock { Text = c?.Label }),
        };
        box.ItemFilter = (search, item) =>
        {
            if (item is not MoveCandidate c) return false;
            string text = (search ?? "").Trim();
            if (text.Length == 0) return false;
            try { return matches(c.Position, text); }
            catch (Exception ex) { CrashLog.Write("PromptMoveSelection.matches", ex); return false; }
        };
        var ok = new Button { Content = "Move", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };

        MoveCandidate? picked = null; // the suggestion last picked, while the box still shows its key

        MoveCandidate? Target()
        {
            string text = (box.Text ?? "").Trim();
            if (text.Length == 0) return null;
            if (picked is not null && picked.Key == text) return picked;
            int position;
            try { position = find(text); }
            catch (Exception ex) { CrashLog.Write("PromptMoveSelection.find", ex); return null; }
            return candidates.FirstOrDefault(c => c.Position == position);
        }

        void Refresh() => ok.IsEnabled = before.IsChecked != true || Target() is not null;

        var beforeRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { before, box } };
        var body = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 10,
            Children =
            {
                top, beforeRow, bottom,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0), Children = { ok, cancel },
                },
            },
        };

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "Move Selection", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };

        // Typing in the box or clicking into it means Before
        box.TextChanged += (_, _) =>
        {
            // Only the user's typing; the box can also set its text while it sets itself up
            if (box.IsKeyboardFocusWithin) before.IsChecked = true;
            Refresh();
        };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is MoveCandidate c) picked = c;
            Refresh();
        };
        box.GotFocus += (_, _) => { before.IsChecked = true; Refresh(); };
        top.IsCheckedChanged += (_, _) => Refresh();
        bottom.IsCheckedChanged += (_, _) => Refresh();
        before.IsCheckedChanged += (_, _) => Refresh();

        void Accept()
        {
            if (!ok.IsEnabled) return;
            string text = (box.Text ?? "").Trim();
            var choice = top.IsChecked == true ? new MoveSelectionChoice(MoveTarget.Top, text)
                : bottom.IsChecked == true ? new MoveSelectionChoice(MoveTarget.Bottom, text)
                : Target() is { } target ? new MoveSelectionChoice(MoveTarget.Before, text, target.Position)
                : null;
            if (choice is null) return;
            result.TrySetResult(choice);
            dialog.Close();
        }

        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.Opened += (_, _) =>
        {
            Refresh();
            if (before.IsChecked == true) box.Focus();
            else if (bottom.IsChecked == true) bottom.Focus();
            else top.Focus();
        };

        dialog.ShowModal(owner);
        return result.Task;
    }
}
