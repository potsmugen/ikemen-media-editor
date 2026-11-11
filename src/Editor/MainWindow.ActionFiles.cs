using System.Text;
using Air;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Editor;

/// <summary>
/// Exports selected actions as plain .air text and imports them back, either
/// after the last action or in number order. Numbers stay as exported; a
/// clash asks whether to overwrite or skip.
/// </summary>
public partial class MainWindow
{
    private static readonly FilePickerFileType ActionFileType =
        new("Action files") { Patterns = ["*.air", "*.txt"] };

    private void OnExportActions(object? sender, RoutedEventArgs e)
    {
        _ = ExportActions();
    }

    private async Task ExportActions()
    {
        try
        {
            if (_air is null) return;
            var targets = SelectedActionsInOrder();
            if (targets.Count == 0 && _action is not null) targets = [_action];
            if (targets.Count == 0)
            {
                StatusText.Text = "No actions to export.";
                return;
            }

            string source = _airPath is null ? "this animation" : Path.GetFileName(_airPath);
            string suggested = (_airPath is null ? "actions" : Path.GetFileNameWithoutExtension(_airPath) + "-actions") + ".air";
            var file = await PickSave(new FilePickerSaveOptions
            {
                Title = "Export Actions",
                SuggestedFileName = suggested,
                DefaultExtension = "air",
                FileTypeChoices = [ActionFileType],
                ShowOverwritePrompt = true,
            });
            if (file?.TryGetLocalPath() is not { } path) return;

            var text = new StringBuilder();
            // The blank line keeps the header out of the first action's name, which
            // is the comment line directly above its [Begin Action.
            text.Append("; Actions exported from ").Append(source).AppendLine().AppendLine();
            foreach (var action in targets) text.Append(AirWriter.WriteAction(action));
            var bytes = new UTF8Encoding(false).GetBytes(NormalizeLineEndings(text.ToString()));
            File.WriteAllBytes(path, bytes);

            StatusText.Text = $"Exported {Plural(targets.Count, "action", "actions")} to {Path.GetFileName(path)}.";
            CrashLog.Trace($"[actions] exported {targets.Count}: {string.Join(", ", targets.Select(a => a.Number))}");
        }
        catch (Exception ex)
        {
            await Failed("OnExportActions", ex);
        }
    }

    private static string NormalizeLineEndings(string text) =>
        Environment.NewLine == "\n" ? text : text.Replace(Environment.NewLine, "\n");

    private async void OnImportActions(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_air is null) return;
            var files = await PickOpen(new FilePickerOpenOptions
            {
                Title = "Import Actions",
                AllowMultiple = false,
                FileTypeFilter = [ActionFileType],
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

            AirFile imported;
            try
            {
                imported = AirFile.Open(path);
            }
            catch (Exception ex)
            {
                CrashLog.Write("OnImportActions.read", ex);
                ShowPopup("Import Actions", $"Could not read {Path.GetFileName(path)}: {ex.Message}");
                return;
            }
            if (imported.Actions.Count == 0)
            {
                ShowPopup("Import Actions", $"{Path.GetFileName(path)} has no actions.");
                return;
            }

            if (_air is not { } air) return;
            string name = Path.GetFileName(path);
            var existing = new HashSet<int>(air.Actions.Select(a => a.Number));
            var clashes = imported.Actions.Select(a => a.Number).Where(existing.Contains).Distinct().Order().ToList();
            bool overwrite = false;
            if (clashes.Count > 0)
            {
                if (await Dialogs.PromptActionImportConflicts(this, clashes) is not { } choice) return;
                if (!ReferenceEquals(_air, air)) return;
                overwrite = choice == Dialogs.ActionImportConflictChoice.Overwrite;
            }

            // Only the actions whose numbers are free need a place chosen.
            bool anyAppended = imported.Actions.Any(a => !existing.Contains(a.Number));
            bool afterLast = true;
            // An empty file with sorted imports gives the same list either way, so don't ask
            bool sameEitherWay = air.Actions.Count == 0
                && imported.Actions.Zip(imported.Actions.Skip(1)).All(p => p.First.Number <= p.Second.Number);
            if (anyAppended && !sameEitherWay)
            {
                if (await Dialogs.PromptActionImportPlacement(this, imported.Actions.Count, name) is not { } chosen) return;
                if (!ReferenceEquals(_air, air)) return;
                afterLast = chosen;
            }

            var before = air.Actions.ToList();
            var after = before.ToList();
            var importedNow = new List<AnimationAction>();
            var skipped = new List<int>();
            var replaced = new List<int>();
            foreach (var action in imported.Actions)
            {
                int at = after.FindIndex(a => a.Number == action.Number);
                if (at >= 0)
                {
                    if (!overwrite)
                    {
                        skipped.Add(action.Number);
                        continue;
                    }
                    after[at] = action; // overwritten actions keep their place
                    replaced.Add(action.Number);
                }
                else if (afterLast)
                {
                    after.Add(action);
                }
                else
                {
                    int insert = after.FindIndex(a => a.Number > action.Number);
                    after.Insert(insert < 0 ? after.Count : insert, action);
                }
                importedNow.Add(action);
            }

            foreach (var number in skipped)
                CrashLog.Error("OnImportActions", $"action {number} skipped: the number is already used");

            if (importedNow.Count == 0)
            {
                StatusText.Text = "No actions imported" + (skipped.Count > 0 ? $", skipped {skipped.Count} (see errors.log)" : "") + ".";
                ShowSkipped("Actions Skipped", skipped.Select(n => $"Action {n} (number already used)").ToList());
                return;
            }

            void Apply(List<AnimationAction> order)
            {
                int at = _action is { } current ? air.Actions.IndexOf(current) : -1;
                air.Actions = [.. order];
                RebuildActionList();
                // An overwritten or undo-removed action would otherwise stay on screen
                // and take frame edits that never reach the file.
                if (_action is not null && !air.Actions.Contains(_action))
                {
                    _action = null;
                    if (air.Actions.Count > 0) SelectAction(air.Actions[Math.Clamp(at, 0, air.Actions.Count - 1)]);
                    else UpdateFrameButtons();
                }
            }
            _undo.Do("import actions", () => Apply(after), () => Apply(before));
            SelectActions(importedNow);
            MarkAirDirty();

            string summary = importedNow.Count == 1 ? "Imported 1 action" : $"Imported {importedNow.Count} actions";
            if (replaced.Count > 0) summary += $", replacing {Plural(replaced.Count, "existing action", "existing actions")}";
            if (skipped.Count > 0) summary += $", skipped {skipped.Count} (see errors.log)";
            StatusText.Text = summary + ".";
            CrashLog.Trace($"[actions] imported {importedNow.Count} from {name}; replaced: {string.Join(", ", replaced)}");
            ShowSkipped("Actions Skipped", skipped.Select(n => $"Action {n} (number already used)").ToList());
        }
        catch (Exception ex)
        {
            await Failed("OnImportActions", ex);
        }
    }
}
