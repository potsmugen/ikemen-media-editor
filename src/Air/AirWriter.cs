using System.Globalization;
using System.Text;

namespace Air;

/// <summary>
/// Writes .air text back out. An action's Clsn1Default / Clsn2Default segments
/// are each written right before the first element they govern (usually just
/// once, right after the header, for an action with a single default); a
/// frame only gets its own Clsn1 / Clsn2 block when it actually has one of
/// its own. A count of 0 is written back when the model holds one (from a
/// loaded file: Ikemen reads it as "no boxes"); the editor never creates one.
/// </summary>
public static class AirWriter
{
    public static string Write(AirFile file)
    {
        var sb = new StringBuilder();
        foreach (var l in file.Header) sb.AppendLine(l); // header lines exactly as read, blank ones included

        foreach (var action in file.Actions) WriteAction(sb, action);
        return sb.ToString();
    }

    /// <summary>Writes one action, usable on its own for the "edit as text" view.</summary>
    public static string WriteAction(AnimationAction action)
    {
        var sb = new StringBuilder();
        WriteAction(sb, action);
        return sb.ToString();
    }

    /// <summary>
    /// Same text as WriteAction, plus each element's character span within it
    /// (start, length of its own data line — not the Clsn/Loopstart/Interpolate
    /// lines that might sit above it), for a text view that needs to highlight
    /// "the current frame". Empty when the action is a Copy Action, since that
    /// has no elements of its own.
    /// </summary>
    public static (string Text, IReadOnlyList<(int Start, int Length)> ElementSpans) WriteActionWithSpans(AnimationAction action)
    {
        var sb = new StringBuilder();
        var spans = new List<(int Start, int Length)>(action.Elements.Count);
        WriteAction(sb, action, spans);
        return (sb.ToString(), spans);
    }

    private static void WriteAction(StringBuilder sb, AnimationAction action, List<(int Start, int Length)>? spans = null)
    {
        foreach (var c in action.Comments)
            sb.Append(c.Length == 0 ? ";" : "; ").AppendLine(c); // an empty name is a bare ";"
        sb.Append("[Begin Action ").Append(action.Number.ToString(CultureInfo.InvariantCulture)).Append(']');
        EndOfLine(sb, action.HeaderComment);

        if (action.CopyAction is { } copy)
        {
            sb.Append("Copy Action ").AppendLine(copy.ToString(CultureInfo.InvariantCulture));
            WriteTrailingComments(sb, action);
            sb.AppendLine();
            return;
        }

        for (int i = 0; i < action.Elements.Count; i++)
        {
            var e = action.Elements[i];
            WriteComments(sb, e.Comments); // above all of the frame's lines
            WriteDefaultSegmentsAt(sb, action, i);
            if (i == action.LoopStart && action.LoopStart > 0) sb.AppendLine("Loopstart");
            if (action.InterpolateOffset.Contains(i)) sb.AppendLine("Interpolate Offset");
            if (action.InterpolateScale.Contains(i)) sb.AppendLine("Interpolate Scale");
            if (action.InterpolateAngle.Contains(i)) sb.AppendLine("Interpolate Angle");
            if (action.InterpolateBlend.Contains(i)) sb.AppendLine("Interpolate Blend");

            if (e.OwnAttackBoxes) WriteDeclaration(sb, "Clsn1", e.Attack);
            if (e.OwnHurtBoxes) WriteDeclaration(sb, "Clsn2", e.Hurt);
            int start = sb.Length;
            WriteElement(sb, e);
            spans?.Add((start, sb.Length - start));
            EndOfLine(sb, e.EndComment);
        }
        // A segment with nowhere left to attach — an empty action, or a
        // declaration trailing past the last element — still needs writing.
        WriteDefaultSegmentsAt(sb, action, action.Elements.Count);
        WriteTrailingComments(sb, action);
        sb.AppendLine();
    }

    /// <summary>Comments after the last frame (AnimationAction.TrailingComments).</summary>
    private static void WriteTrailingComments(StringBuilder sb, AnimationAction action) => WriteComments(sb, action.TrailingComments);

    private static void WriteComments(StringBuilder sb, List<string> comments)
    {
        foreach (var c in comments) sb.Append(';').AppendLine(c); // as written after the semicolon
    }

    /// <summary>Ends a line, with its end-of-line comment if it has one.</summary>
    private static void EndOfLine(StringBuilder sb, string? comment)
    {
        if (comment is not null) sb.Append(" ;").Append(comment);
        sb.AppendLine();
    }

    /// <summary>
    /// Writes any Clsn1Default/Clsn2Default segment starting exactly at this
    /// element index — Attack before Hurt, the same order a single default
    /// always used to write in. An empty segment (only from a loaded
    /// "Clsn2Default: 0") is written back: it resets later frames to no
    /// boxes, and dropping it would leave the previous default in force.
    /// </summary>
    private static void WriteDefaultSegmentsAt(StringBuilder sb, AnimationAction action, int index)
    {
        foreach (var s in action.DefaultAttackSegments)
            if (s.At == index) WriteDefaultDeclaration(sb, "Clsn1Default", s.Boxes);
        foreach (var s in action.DefaultHurtSegments)
            if (s.At == index) WriteDefaultDeclaration(sb, "Clsn2Default", s.Boxes);
    }

    private static void WriteDefaultDeclaration(StringBuilder sb, string tag, List<ClsnBox> boxes) =>
        WriteDeclaration(sb, tag, boxes);

    /// <summary>Writes a declaration, including a count of 0 (a loaded "Clsn2: 0" means no boxes on that frame).</summary>
    private static void WriteDeclaration(StringBuilder sb, string tag, List<ClsnBox> boxes)
    {
        sb.Append(tag).Append(": ").Append(boxes.Count).AppendLine();
        for (int i = 0; i < boxes.Count; i++)
            sb.Append(' ').Append(tag[..5]).Append('[').Append(i).Append("] = ").AppendLine(boxes[i].ToString());
    }

    /// <summary>
    /// Writes the element's fields, dropping trailing ones that carry nothing.
    /// The fields are positional, so a later one forces the empty ones before it
    /// to be written as bare commas.
    /// </summary>
    private static void WriteElement(StringBuilder sb, AnimationElement e)
    {
        var tail = new List<string>
        {
            (e.FlipH ? "H" : "") + (e.FlipV ? "V" : ""),
            e.Blend.ToString(),
            e.ScaleX == 1 ? "" : e.ScaleX.ToString(CultureInfo.InvariantCulture),
            e.ScaleY == 1 ? "" : e.ScaleY.ToString(CultureInfo.InvariantCulture),
            e.Angle == 0 ? "" : e.Angle.ToString(CultureInfo.InvariantCulture),
        };
        while (tail.Count > 0 && tail[^1].Length == 0) tail.RemoveAt(tail.Count - 1);

        sb.Append(e.Group).Append(',').Append(e.Index).Append(", ")
          .Append(e.OffsetX).Append(',').Append(e.OffsetY).Append(", ")
          .Append(e.Duration);
        foreach (var t in tail) sb.Append(", ").Append(t);
    }
}
