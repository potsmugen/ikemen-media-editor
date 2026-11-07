using System.Globalization;
using Air;
using Sff;
using Snd;

namespace Editor;

// Move Selection: moves the selected list items to the top, the bottom, or before an item found with
// the list's filter syntax. It calls each list's drop move, so it follows the same rules as a drag.
public partial class MainWindow
{
    // Last answer per list, kept for this session only
    private readonly Dictionary<string, MoveSelectionChoice> _lastMoveChoice = [];

    /// <summary>The item to move before: an exact match wins, otherwise the first item the filter rule matches.</summary>
    private static int FindMoveTarget<T>(IReadOnlyList<T> items, string text, Func<T, string, bool> exact, Func<T, string, bool> matches)
    {
        // Only separators would leave the filter rules nothing to compare
        if (text.Trim(',', ' ').Length == 0) return -1;
        for (int i = 0; i < items.Count; i++) if (exact(items[i], text)) return i;
        for (int i = 0; i < items.Count; i++) if (matches(items[i], text)) return i;
        return -1;
    }

    /// <summary>"200" is an exact match for group 200, "200,5" for group 200 number 5.</summary>
    private static bool ExactGroupNumber(int group, int number, string text)
    {
        var parts = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        string g = group.ToString(CultureInfo.InvariantCulture);
        return parts.Length switch
        {
            1 => parts[0] == g,
            2 => parts[0] == g && parts[1] == number.ToString(CultureInfo.InvariantCulture),
            _ => false,
        };
    }

    /// <summary>"1,16" is an exact match for that group and index, "F1" or "S3" for that labeled palette, "1" for group 1.</summary>
    private static bool ExactPalette(PaletteRow row, string text)
    {
        var parts = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
            return row.GroupText.Equals(parts[0], StringComparison.OrdinalIgnoreCase)
                && row.IndexText.Equals(parts[1], StringComparison.OrdinalIgnoreCase);
        if (parts.Length != 1) return false;
        if (parts[0].Length > 1 && char.IsLetter(parts[0][0]))
            return row.GroupText.Equals(parts[0][..1], StringComparison.OrdinalIgnoreCase)
                && row.IndexText.Equals(parts[0][1..], StringComparison.OrdinalIgnoreCase);
        return row.GroupText.Equals(parts[0], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Asks where to move one list's selection and moves it there. name gives the item at a list
    /// position as its key and suggestion label; matchesAt is the list's filter rule for one position;
    /// find picks the position for typed text (-1 for no match).</summary>
    private async Task MoveSelectionTo<T>(string list, IReadOnlyList<T> items, IReadOnlyList<T> selected, string watermark,
        Func<int, (string Key, string Label)> name, Func<int, string, bool> matchesAt, Func<string, int> find,
        Action<IReadOnlyList<T>, int> move)
    {
        if (selected.Count == 0) return;
        var candidates = Enumerable.Range(0, items.Count)
            .Select(i =>
            {
                var (key, label) = name(i);
                return new MoveCandidate(i, key, label);
            })
            .ToList();
        var initial = _lastMoveChoice.GetValueOrDefault(list) ?? new MoveSelectionChoice(MoveTarget.Top, "");
        var choice = await Dialogs.PromptMoveSelection(this, watermark, candidates, matchesAt, find, initial);
        if (choice is null) return;
        // The position is only good for this list as it is now; the text is what comes back next time
        _lastMoveChoice[list] = choice with { Position = -1 };
        int to = choice.Target switch
        {
            MoveTarget.Top => 0,
            MoveTarget.Bottom => items.Count,
            _ => choice.Position,
        };
        if (to < 0 || to > items.Count)
        {
            CrashLog.Error("MoveSelectionTo", $"{list}: position {to} for \"{choice.Text}\" is outside 0..{items.Count}");
            return;
        }
        move(selected, to);
    }

    private async Task MoveSelectedSprites()
    {
        try
        {
            if (_file is null) return;
            var sprites = _file.Sprites.ToList();
            await MoveSelectionTo("sprites", sprites, SelectedSpriteTargets(), "e.g. 5000 or 5000,10",
                i => GroupNumberName(sprites[i].Group, sprites[i].Index, i),
                (i, t) => Matches(sprites[i], t),
                t => FindMoveTarget(sprites, t, (s, x) => ExactGroupNumber(s.Group, s.Index, x), Matches),
                MoveSprites);
        }
        catch (Exception ex) { await Failed("MoveSelectedSprites", ex); }
    }

    private async Task MoveSelectedPalettes()
    {
        try
        {
            var palettes = OrderedPalettes.ToList();
            var rows = AllPaletteRows();
            await MoveSelectionTo("palettes", palettes, SelectedPalettesInOrder(), "e.g. 1,2 or F1",
                i =>
                {
                    string key = PaletteLabel(palettes[i]);
                    return (key, $"{key} (#{rows[i].Number})");
                },
                (i, t) => PaletteRowMatches(rows[i], t),
                t => FindMoveTarget(rows, t, ExactPalette, PaletteRowMatches),
                MovePalettes);
        }
        catch (Exception ex) { await Failed("MoveSelectedPalettes", ex); }
    }

    private async Task MoveSelectedActions()
    {
        try
        {
            if (_air is null) return;
            var actions = _air.Actions.ToList();
            await MoveSelectionTo("actions", actions, SelectedActionsInOrder(), "e.g. 200 or punch",
                i =>
                {
                    var a = actions[i];
                    string key = a.Number.ToString(CultureInfo.InvariantCulture);
                    string? comment = a.Comments.Count > 0 ? a.Comments[0].Trim() : SuggestedActionName(a.Number);
                    return (key, string.IsNullOrEmpty(comment) ? key : $"{key} ({comment})");
                },
                (i, t) => MatchesAction(actions[i], t),
                t => FindMoveTarget(actions, t, (a, x) => a.Number.ToString(CultureInfo.InvariantCulture) == x, MatchesAction),
                MoveActions);
        }
        catch (Exception ex) { await Failed("MoveSelectedActions", ex); }
    }

    private async Task MoveSelectedSounds()
    {
        try
        {
            if (_snd is null) return;
            var sounds = _snd.Sounds.ToList();
            await MoveSelectionTo("sounds", sounds, GetSelectedSounds(), "e.g. 0 or 0,1",
                i => GroupNumberName(sounds[i].Group, sounds[i].Index, i),
                (i, t) => MatchesSound(sounds[i], t),
                t => FindMoveTarget(sounds, t, (s, x) => ExactGroupNumber(s.Group, s.Index, x), MatchesSound),
                MoveSounds);
        }
        catch (Exception ex) { await Failed("MoveSelectedSounds", ex); }
    }

    /// <summary>Key and suggestion label for a sprite or sound, e.g. "5000,1" and "5000,1 (#58)".</summary>
    private static (string Key, string Label) GroupNumberName(int group, int number, int position)
    {
        string key = $"{group},{number}";
        return (key, $"{key} (#{position + 1})");
    }
}
