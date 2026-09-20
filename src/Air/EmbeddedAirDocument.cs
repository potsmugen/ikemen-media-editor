using System.Text;

namespace Air;

/// <summary>Replaces embedded [Begin Action] blocks without re-encoding or rewriting the surrounding definition.</summary>
public static class EmbeddedAirDocument
{
    private readonly record struct SourceLine(int Start, int End);
    private readonly record struct ActionBlock(int Start, int ContentEnd, int End);
    private readonly record struct Section(int Line, bool IsAction);

    /// <summary>Replaces action blocks in source order, preserving bytes outside those blocks.</summary>
    public static byte[] ReplaceActions(byte[] source, AirFile air)
    {
        ArgumentNullException.ThrowIfNull(air);
        return ReplaceActions(source, air, air.Actions, out _);
    }

    /// <summary>
    /// Replaces source action blocks using their original action objects as location anchors.
    /// New actions are appended after the last source action block.
    /// </summary>
    public static byte[] ReplaceActions(byte[] source, AirFile air,
        IReadOnlyList<AnimationAction> sourceActionOrder, out List<AnimationAction> writtenActionOrder)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(air);
        ArgumentNullException.ThrowIfNull(sourceActionOrder);

        writtenActionOrder = [];
        if (source.Length == 0)
        {
            if (air.Actions.Count == 0) return [];
            using var emptyOutput = new MemoryStream();
            foreach (var action in air.Actions)
            {
                WriteAction(emptyOutput, action, new UTF8Encoding(false, true), "\n", trailingBlankLine: true);
                writtenActionOrder.Add(action);
            }
            return emptyOutput.ToArray();
        }

        var (text, encodingKind, legacyName) = AirFile.Decode(source);
        var lines = ScanLines(source);
        var textLines = text.Split('\n');
        if (textLines.Length != lines.Count)
            throw new InvalidDataException("Could not map the definition's text lines to its original bytes.");

        for (int i = 0; i < textLines.Length; i++)
            textLines[i] = textLines[i].TrimEnd('\r');

        var sections = new List<Section>();
        for (int i = 0; i < textLines.Length; i++)
            if (SectionName(textLines[i]) is { } name)
                sections.Add(new(i, IsActionSection(name)));

        var actionSections = sections.Where(s => s.IsAction).ToList();
        if (actionSections.Count != AirParser.Parse(text).Actions.Count)
            throw new InvalidDataException("Could not safely map every embedded action block in the definition.");

        var newline = DetectNewline(source);
        var encoding = TextEncoding(encodingKind, legacyName);
        if (actionSections.Count == 0)
        {
            if (air.Actions.Count == 0) return [.. source];
            using var appended = new MemoryStream(source.Length);
            appended.Write(source);
            if (source[^1] is not ((byte)'\n' or (byte)'\r'))
                appended.Write(encoding.GetBytes(newline));
            foreach (var action in air.Actions)
            {
                WriteAction(appended, action, encoding, newline, trailingBlankLine: true);
                writtenActionOrder.Add(action);
            }
            return appended.ToArray();
        }

        var blocks = new List<ActionBlock>(actionSections.Count);
        for (int sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++)
        {
            var section = sections[sectionIndex];
            if (!section.IsAction) continue;

            int startLine = section.Line;
            if (startLine > 0 && IsActionNameComment(textLines[startLine - 1]))
                startLine--;

            int nextSectionLine = sectionIndex + 1 < sections.Count ? sections[sectionIndex + 1].Line : lines.Count;
            if (nextSectionLine > section.Line && IsActionNameComment(textLines[nextSectionLine - 1]))
                nextSectionLine--;

            int lastContentLine = -1;
            for (int line = startLine; line < nextSectionLine; line++)
                if (!string.IsNullOrWhiteSpace(textLines[line]))
                    lastContentLine = line;
            if (lastContentLine < startLine)
                throw new InvalidDataException("An embedded action block has no section header.");

            int end = nextSectionLine < lines.Count ? lines[nextSectionLine].Start : source.Length;
            blocks.Add(new(lines[startLine].Start, lines[lastContentLine].End, end));
        }

        EnsureNonOverlapping(blocks, source.Length);
        var anchoredActions = new List<AnimationAction?>(blocks.Count);
        var anchoredSet = new HashSet<AnimationAction>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < blocks.Count; i++)
        {
            AnimationAction? anchored = i < sourceActionOrder.Count ? sourceActionOrder[i] : null;
            if (anchored is not null && air.Actions.Any(a => ReferenceEquals(a, anchored)))
            {
                anchoredActions.Add(anchored);
                anchoredSet.Add(anchored);
                writtenActionOrder.Add(anchored);
            }
            else anchoredActions.Add(null);
        }
        var added = air.Actions.Where(a => !anchoredSet.Contains(a)).ToList();

        using var output = new MemoryStream(source.Length);
        int cursor = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            output.Write(source, cursor, block.Start - cursor);
            if (anchoredActions[i] is { } anchored)
                WriteAction(output, anchored, encoding, newline, trailingBlankLine: false);
            output.Write(source, block.ContentEnd, block.End - block.ContentEnd);
            cursor = block.End;

            if (i == blocks.Count - 1)
                foreach (var action in added)
                {
                    WriteAction(output, action, encoding, newline, trailingBlankLine: true);
                    writtenActionOrder.Add(action);
                }
        }

        output.Write(source, cursor, source.Length - cursor);
        return output.ToArray();
    }

    private static List<SourceLine> ScanLines(byte[] source)
    {
        var lines = new List<SourceLine>();
        int start = source.Length >= 3 && source[0] == 0xEF && source[1] == 0xBB && source[2] == 0xBF ? 3 : 0;
        for (int i = start; i < source.Length; i++)
        {
            if (source[i] != (byte)'\n') continue;
            lines.Add(new(start, i + 1));
            start = i + 1;
        }
        if (start < source.Length)
            lines.Add(new(start, source.Length));
        else if (source.Length > 0)
            lines.Add(new(source.Length, source.Length));
        return lines;
    }

    private static string? SectionName(string line)
    {
        int comment = line.IndexOf(';');
        if (comment >= 0) line = line[..comment];
        line = line.Trim();
        return line.StartsWith('[') && line.IndexOf(']') > 0 ? line : null;
    }

    private static bool IsActionSection(string name)
    {
        const string prefix = "[Begin Action";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && name.Length > prefix.Length
            && (char.IsWhiteSpace(name[prefix.Length]) || name[prefix.Length] == ']');
    }

    /// <summary>A comment-only line directly above an action header: the action's name.
    /// Any text counts, an empty ";" included, since AirParser reads a name the
    /// same way.</summary>
    private static bool IsActionNameComment(string line)
    {
        int comment = line.IndexOf(';');
        return comment >= 0 && line[..comment].Trim().Length == 0;
    }

    private static string DetectNewline(byte[] source)
    {
        int lf = Array.IndexOf(source, (byte)'\n');
        return lf > 0 && source[lf - 1] == (byte)'\r' ? "\r\n" : "\n";
    }

    private static Encoding TextEncoding(AirEncoding kind, string? legacyName)
    {
        if (kind is AirEncoding.Utf8 or AirEncoding.Utf8Bom)
            return new UTF8Encoding(false, throwOnInvalidBytes: true);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int codePage = string.Equals(legacyName, "Shift-JIS", StringComparison.OrdinalIgnoreCase) ? 932 : 1252;
        return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static void WriteAction(Stream output, AnimationAction action, Encoding encoding, string newline, bool trailingBlankLine)
    {
        var text = AirWriter.WriteAction(action)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", newline, StringComparison.Ordinal)
            .TrimEnd('\r', '\n') + newline;
        if (trailingBlankLine) text += newline;
        output.Write(encoding.GetBytes(text));
    }

    private static void EnsureNonOverlapping(List<ActionBlock> blocks, int sourceLength)
    {
        int previousEnd = 0;
        foreach (var block in blocks)
        {
            if (block.Start < previousEnd || block.ContentEnd < block.Start || block.End < block.ContentEnd || block.End > sourceLength)
                throw new InvalidDataException("Embedded action blocks overlap or have invalid boundaries.");
            previousEnd = block.End;
        }
    }
}
