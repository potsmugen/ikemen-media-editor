using System.Globalization;

namespace Air;

/// <summary>
/// Reads .air text. The format is line-based: [Begin Action n] starts an action,
/// Clsn lines declare collision boxes, Loopstart marks the loop point, and a
/// line beginning with a digit or minus sign is an element. In a .def, another
/// section header ends the current action so its configuration lines are ignored.
///
/// Ikemen adds several things MUGEN never had: scale and angle fields, a richer
/// set of blend spellings, "Copy Action", and interpolation hints. They are
/// parsed and preserved even though nothing here acts on them yet, because
/// dropping them would quietly damage a file on save.
/// </summary>
public static class AirParser
{
    public static AirFile Parse(string text)
    {
        var file = new AirFile();
        AnimationAction? action = null;
        var headerLines = new List<string>();
        // The previous line's comment, but only when that line was nothing but
        // a comment — used for an action's name, which should be exactly the
        // line immediately above [Begin Action], not "whichever comment line
        // was most recently seen".
        string? adjacentComment = null;

        // A non-default Clsn declaration applies to the next element only; the
        // engine resets to the action's default after every element.
        List<ClsnBox> currentAttack = [], currentHurt = [];
        bool useDefaultAttack = true, useDefaultHurt = true;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // Comment lines waiting for the next frame, which they belong to. lastIsName: the last entry was the
        // previous line, comment-only, so it may still turn out to be the next action's name.
        var pending = new List<string>();
        bool lastIsName = false;
        void Moved(string c) { pending.Add(c); action!.MovedComments++; }
        // Ends the action: comments left over sit after its last frame.
        void Close(bool dropName)
        {
            if (dropName) pending.RemoveAt(pending.Count - 1);
            action?.TrailingComments.AddRange(pending);
            pending.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var (line, comment) = StripComment(raw);
            var rawComment = RawComment(raw); // kept as written, empty ";" lines included
            bool nameBefore = lastIsName;
            lastIsName = false;
            bool sectionHeader = line.StartsWith('[');
            bool header = sectionHeader && IsActionHeader(line);

            // Before the first action: comment and blank lines are the file header, kept line for
            // line as written. Any other line (a .def's own sections) starts the header over.
            if (action is null && !header)
            {
                if (line.Length == 0) headerLines.Add(raw.TrimEnd('\r'));
                else headerLines.Clear();
            }

            // A genuinely blank line breaks adjacency: a comment above it isn't the next action's name.
            if (raw.Trim().Length == 0)
            {
                adjacentComment = null;
                continue;
            }
            // Inside an action, a comment-only line waits for the next frame, and may be the next action's name.
            if (action is not null && rawComment is not null && line.Length == 0)
            {
                pending.Add(rawComment);
                lastIsName = true;
            }

            if (line.Length == 0)
            {
                // A comment-only line directly above a header is that action's
                // name, whatever it says — an empty ";" is an empty name and a
                // "----" rule is a name too, so saving leaves such a line where
                // the file had it instead of moving it to another action.
                adjacentComment = comment;
                continue;
            }

            // Any other real content line breaks adjacency to whatever
            // [Begin Action] might come next, whether or not this turns out to
            // be one — captured before resetting, for that line to use below.
            var adjacent = adjacentComment;
            adjacentComment = null;

            if (sectionHeader)
            {
                if (header)
                {
                    // The comment line right above becomes this action's name, not the previous one's comment.
                    Close(adjacent is not null && nameBefore);
                    // First action: the lines above it are the file header, except its own name line
                    // (always the line right before, when there is one).
                    if (action is null)
                    {
                        if (adjacent is not null && headerLines.Count > 0) headerLines.RemoveAt(headerLines.Count - 1);
                        file.Header.AddRange(headerLines);
                    }
                    action = new AnimationAction
                    {
                        Number = ParseActionNumber(line),
                        Comments = adjacent is { } a ? [a] : [],
                        HeaderComment = rawComment, // "[Begin Action 200] ; jab"
                    };
                    file.Actions.Add(action);
                    currentAttack = currentHurt = [];
                    useDefaultAttack = useDefaultHurt = true;
                }
                else
                {
                    Close(adjacent is not null && nameBefore);
                    action = null;
                    currentAttack = currentHurt = [];
                    useDefaultAttack = useDefaultHurt = true;
                }
                continue;
            }

            if (action is null) continue;

            var lower = line.ToLowerInvariant();

            if (lower.StartsWith("copy action "))
            {
                action.CopyAction = int.TryParse(line[12..].Trim(), out int n) ? n : -1;
                if (rawComment is not null) Moved(rawComment);
                continue;
            }
            if (lower.StartsWith("loopstart"))
            {
                action.LoopStart = action.Elements.Count;
                if (rawComment is not null) Moved(rawComment);
                continue;
            }
            if (lower.StartsWith("interpolate "))
            {
                Interpolation(action, lower)?.Add(action.Elements.Count);
                if (rawComment is not null) Moved(rawComment);
                continue;
            }
            if (lower.StartsWith("clsn"))
            {
                if (rawComment is not null) Moved(rawComment);
                ReadClsn(lines, ref i, lower, action, ref currentAttack, ref currentHurt,
                    ref useDefaultAttack, ref useDefaultHurt, (c, ownLine) => { if (ownLine) pending.Add(c); else Moved(c); });
                continue;
            }

            // Anything else is an element, but only if it starts like a number.
            if (line[0] is not ('-' or >= '0' and <= '9') || ParseElement(line) is not { } element)
            {
                if (rawComment is not null) Moved(rawComment); // an unreadable line is dropped, its comment kept
                continue;
            }

            element.Attack = [.. useDefaultAttack ? action.DefaultAttack : currentAttack];
            element.Hurt = [.. useDefaultHurt ? action.DefaultHurt : currentHurt];
            element.OwnAttackBoxes = !useDefaultAttack;
            element.OwnHurtBoxes = !useDefaultHurt;
            useDefaultAttack = useDefaultHurt = true;

            element.Comments = [.. pending];
            element.EndComment = rawComment;
            pending.Clear();

            action.Elements.Add(element);
        }
        Close(false);

        foreach (var a in file.Actions)
            if (a.LoopStart >= a.Elements.Count) a.LoopStart = 0; // a loop point past the end is meaningless
        return file;
    }

    private static bool IsActionHeader(string line)
    {
        const string prefix = "[Begin Action";
        return line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && line.Length > prefix.Length
            && (char.IsWhiteSpace(line[prefix.Length]) || line[prefix.Length] == ']');
    }

    private static (string Line, string Comment) StripComment(string raw)
    {
        int semi = raw.IndexOf(';');
        return semi < 0 ? (raw.Trim(), "") : (raw[..semi].Trim(), raw[(semi + 1)..].Trim());
    }

    /// <summary>Everything after the ';' exactly as written (kept comments are written back as ";" + this); null if none.</summary>
    private static string? RawComment(string raw)
    {
        int semi = raw.IndexOf(';');
        return semi < 0 ? null : raw[(semi + 1)..].TrimEnd('\r');
    }

    private static SortedSet<int>? Interpolation(AnimationAction a, string lower) =>
        lower.Contains("offset") ? a.InterpolateOffset
        : lower.Contains("scale") ? a.InterpolateScale
        : lower.Contains("angle") ? a.InterpolateAngle
        : lower.Contains("blend") ? a.InterpolateBlend
        : null;

    private static int ParseActionNumber(string line)
    {
        var digits = new string(line.Where(char.IsDigit).ToArray());
        return digits.Length > 0 ? int.Parse(digits, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>
    /// Reads a "Clsn1: n" or "Clsn2Default: n" declaration and the box lines
    /// after it (all of them; n only feeds the lint count). A declaration with
    /// no box lines is meaningful: it clears the boxes rather than
    /// leaving the previous ones in force. A Default declaration starts a new
    /// segment governing from the very next element onward (see
    /// DefaultSegment) — it also governs that next element directly, exactly
    /// as a non-default declaration would.
    /// </summary>
    private static void ReadClsn(string[] lines, ref int i, string lower, AnimationAction action,
        ref List<ClsnBox> currentAttack, ref List<ClsnBox> currentHurt,
        ref bool useDefaultAttack, ref bool useDefaultHurt, Action<string, bool> keepComment)
    {
        int colon = lower.IndexOf(':');
        if (colon < 0 || lower.Length < 5) return;

        bool attack = lower[4] == '1';
        if (!attack && lower[4] != '2') return;

        if (!int.TryParse(lower[(colon + 1)..].Trim(), out int count) || count < 0) return;
        bool isDefault = lower.Contains("default");

        var boxes = new List<ClsnBox>();
        if (isDefault)
        {
            var segment = new DefaultSegment { At = action.Elements.Count, Boxes = boxes };
            if (attack) action.DefaultAttackSegments.Add(segment); else action.DefaultHurtSegments.Add(segment);
        }
        else
        {
            if (attack) { currentAttack = boxes; useDefaultAttack = false; }
            else { currentHurt = boxes; useDefaultHurt = false; }
        }

        // Every box line under the declaration is read, whatever the count says.
        while (NextBoxLine(lines, i) is { } found)
        {
            // The [n] is ignored (boxes go in file order); count lines where it's off or missing.
            if (found.Number != boxes.Count) action.MisnumberedClsnLines++;
            boxes.Add(found.Box);
            // The main loop never sees these lines, so their comments are kept here (and whether on a line of their own).
            for (int k = i + 1; k <= found.Line; k++)
                if (RawComment(lines[k]) is { } c) keepComment(c, StripComment(lines[k]).Line.Length == 0);
            i = found.Line;
        }
        if (boxes.Count != count) action.MismatchedClsnCounts++;
    }

    /// <summary>The next box line after line i, skipping blank ones, without consuming anything; null if none.
    /// Number is its [n], or -1 when missing or unreadable.</summary>
    private static (int Line, ClsnBox Box, int Number)? NextBoxLine(string[] lines, int i)
    {
        for (int j = i + 1; j < lines.Length; j++)
        {
            var (next, _) = StripComment(lines[j]);
            if (next.Length == 0) continue;
            if (!next.StartsWith("Clsn", StringComparison.OrdinalIgnoreCase)) return null;
            int eq = next.IndexOf('=');
            if (eq < 0 || ParseBox(next[(eq + 1)..]) is not { } box) return null;
            int open = next.IndexOf('['), close = next.IndexOf(']');
            int n = open >= 0 && close > open && close < eq
                && int.TryParse(next[(open + 1)..close].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1;
            return (j, box, n);
        }
        return null;
    }

    /// <summary>Reads four numbers and orders them so x1,y1 is the top left.</summary>
    private static ClsnBox? ParseBox(string values)
    {
        var parts = Split(values);
        if (parts.Length < 4) return null;
        if (!TryInt(parts[0], out int x1) || !TryInt(parts[1], out int y1) ||
            !TryInt(parts[2], out int x2) || !TryInt(parts[3], out int y2)) return null;

        if (x1 > x2) (x1, x2) = (x2, x1);
        if (y1 > y2) (y1, y2) = (y2, y1);
        return new ClsnBox(x1, y1, x2, y2);
    }

    /// <summary>
    /// An element is "group, image, offsetX, offsetY, time" with optional flip,
    /// blend, x scale, y scale and angle after it.
    /// </summary>
    private static AnimationElement? ParseElement(string line)
    {
        var parts = Split(line);
        if (parts.Length < 5) return null;
        if (!TryInt(parts[0], out int group) || !TryInt(parts[1], out int index)) return null;
        if (!TryInt(parts[2], out int ox) || !TryInt(parts[3], out int oy)) return null;
        if (!TryInt(parts[4], out int time)) return null;

        var e = new AnimationElement
        {
            Group = group, Index = index,
            OffsetX = ox, OffsetY = oy,
            Duration = time,
        };

        if (parts.Length > 5 && parts[5].Length > 0)
        {
            var flip = parts[5].ToUpperInvariant();
            e.FlipH = flip.Contains('H');
            e.FlipV = flip.Contains('V');
        }
        if (parts.Length > 6 && parts[6].Length > 0) e.Blend = ParseBlend(parts[6]);
        if (parts.Length > 7 && TryFloat(parts[7], out float sx)) e.ScaleX = sx;
        if (parts.Length > 8 && TryFloat(parts[8], out float sy)) e.ScaleY = sy;
        if (parts.Length > 9 && TryFloat(parts[9], out float angle)) e.Angle = angle;
        return e;
    }

    /// <summary>
    /// Reads the blend parameter. MUGEN documents "A", "S", "A1" and
    /// "AS___D___"; Ikemen adds "SA", "SAS___D___" and "SS___D___". Note that a
    /// bare "AS" defaults its destination alpha to 0, not 256, which is what
    /// MUGEN does and is easy to get wrong.
    /// </summary>
    public static Blend ParseBlend(string text)
    {
        var raw = text.Trim();
        if (raw.Length == 0) return Blend.None;

        int start = raw.IndexOfAny(['A', 'S', 'a', 's']);
        if (start < 0) return Blend.None;
        var a = raw[start..].ToLowerInvariant();

        if (a == "a1") return new Blend(BlendMode.Add, 255, 128, raw);
        if (a.StartsWith("sas")) return WithAlpha(BlendMode.SubAdd, a, 3, 255, 255, raw);
        if (a.StartsWith("sa")) return new Blend(BlendMode.SubAdd, 255, 255, raw);
        if (a.StartsWith("as")) return WithAlpha(BlendMode.Add, a, 2, 255, 0, raw);
        if (a.StartsWith("ss")) return WithAlpha(BlendMode.Subtract, a, 2, 255, 255, raw);
        if (a[0] == 'a') return new Blend(BlendMode.Add, 255, 255, raw);
        if (a[0] == 's') return new Blend(BlendMode.Subtract, 255, 255, raw);
        return Blend.None;
    }

    private static Blend WithAlpha(BlendMode mode, string a, int at, int defSrc, int defDst, string raw)
    {
        int src = defSrc, dst = defDst, i = at, n = 0;
        bool any = false;
        for (; i < a.Length && char.IsAsciiDigit(a[i]); i++, any = true) n = n * 10 + (a[i] - '0');
        if (any) src = Math.Min(n, 256);

        if (i < a.Length && a[i] == 'd')
        {
            i++;
            n = 0;
            any = false;
            for (; i < a.Length && char.IsAsciiDigit(a[i]); i++, any = true) n = n * 10 + (a[i] - '0');
            if (any) dst = Math.Min(n, 256);
        }
        return new Blend(mode, src, dst, raw);
    }

    private static string[] Split(string s) => s.Split(',').Select(p => p.Trim()).ToArray();

    private static bool TryInt(string s, out int value) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryFloat(string s, out float value) =>
        float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
