using System.Globalization;
using System.Text;

namespace Editor;

/// <summary>
/// The round-trip export .txt: a manifest the program reads back, not prose.
/// Each entry is a section such as "[sprite 0,2]" with one key/value per line.
/// File values may contain spaces; ';' starts a comment.
/// </summary>
internal static class ExportManifest
{
    internal sealed record Entry(string Type, int Group, int Index, string File,
        (int X, int Y)? Axis = null, (int Group, int Index)? Palette = null);

    private sealed class PendingEntry(string type, int group, int index, int line)
    {
        public string Type { get; } = type;
        public int Group { get; } = group;
        public int Index { get; } = index;
        public int Line { get; } = line;
        public string? File { get; set; }
        public (int X, int Y)? Axis { get; set; }
        public (int Group, int Index)? Palette { get; set; }
    }

    public static string Write(IEnumerable<Entry> entries)
    {
        var sb = new StringBuilder();
        sb.Append("; Ikemen Media Editor export manifest - read by Import All\n\n");
        foreach (var e in entries)
        {
            sb.Append('[').Append(e.Type).Append(' ').Append(Pair(e.Group, e.Index)).Append("]\n");
            sb.Append("file = ").Append(e.File).Append('\n');
            if (e.Axis is { } a) sb.Append("axis = ").Append(Pair(a.X, a.Y)).Append('\n');
            if (e.Palette is { } p) sb.Append("palette = ").Append(Pair(p.Group, p.Index)).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Parses a manifest. Bad lines are reported in Errors (with line numbers), never fatal.</summary>
    public static (List<Entry> Entries, List<string> Errors) Read(string text)
    {
        PendingEntry? current = null;
        var entries = new List<Entry>();
        var errors = new List<string>();

        void FinishEntry()
        {
            if (current is null) return;
            if (string.IsNullOrWhiteSpace(current.File))
                errors.Add($"line {current.Line}: {current.Type} {Pair(current.Group, current.Index)} has no file");
            else
                entries.Add(new Entry(current.Type, current.Group, current.Index, current.File,
                    current.Axis, current.Palette));
            current = null;
        }

        var lines = text.Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n].Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                FinishEntry();
                var section = line[1..^1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (section.Length != 2 || !TryPair(section[1], out int group, out int index))
                {
                    errors.Add($"line {n + 1}: can't read section \"{line}\"");
                    continue;
                }
                current = new PendingEntry(section[0].ToLowerInvariant(), group, index, n + 1);
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq < 0)
            {
                errors.Add($"line {n + 1}: missing '='");
                continue;
            }
            string key = line[..eq].Trim().ToLowerInvariant();
            string value = line[(eq + 1)..].Trim();
            if (current is null)
            {
                errors.Add($"line {n + 1}: '{key}' is outside an entry section");
                continue;
            }

            switch (key)
            {
                case "file":
                    if (current.File is not null) errors.Add($"line {n + 1}: duplicate file field");
                    else if (value.Length == 0) errors.Add($"line {n + 1}: file value is empty");
                    else current.File = value;
                    break;
                case "axis":
                    if (current.Axis is not null) errors.Add($"line {n + 1}: duplicate axis field");
                    else if (TryPair(value, out int x, out int y)) current.Axis = (x, y);
                    else errors.Add($"line {n + 1}: bad axis \"{value}\"");
                    break;
                case "palette":
                    if (current.Palette is not null) errors.Add($"line {n + 1}: duplicate palette field");
                    else if (TryPair(value, out int pg, out int pi)) current.Palette = (pg, pi);
                    else errors.Add($"line {n + 1}: bad palette \"{value}\"");
                    break;
                default:
                    errors.Add($"line {n + 1}: unknown field \"{key}\"");
                    break;
            }
        }
        FinishEntry();
        return (entries, errors);
    }

    private static string Pair(int a, int b) =>
        a.ToString(CultureInfo.InvariantCulture) + "," + b.ToString(CultureInfo.InvariantCulture);

    private static bool TryPair(string s, out int a, out int b)
    {
        a = b = 0;
        var p = s.Split(',');
        return p.Length == 2
            && int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out a)
            && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out b);
    }
}
