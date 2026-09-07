using System.Text;

namespace CodeSpace.Core.Services.Agents;

/// <summary>One bounded data rendering for oracle diagnosis across worker repair and supervisor prompts. An artifact reference identifies captured evidence; neither the reference nor the text promotes an unverified verdict.</summary>
public static class AcceptanceEvidenceRenderer
{
    public const int TailMaxChars = 2048;

    public static string? ClipTail(string? tail)
    {
        if (string.IsNullOrEmpty(tail)) return null;
        var start = Math.Max(0, tail.Length - TailMaxChars);
        if (start > 0 && char.IsLowSurrogate(tail[start]) && char.IsHighSurrogate(tail[start - 1])) start++;
        return tail[start..];
    }

    public static string Render(string? tail, Guid? artifactId, string linePrefix = "| ")
    {
        var builder = new StringBuilder();
        if (ClipTail(tail) is { } clipped)
        {
            foreach (var line in clipped.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
                builder.Append(linePrefix).AppendLine(line);
        }
        if (artifactId is { } id && id != Guid.Empty) builder.Append(linePrefix).Append("Captured evidence artifact: ").AppendLine(id.ToString("D"));
        return builder.ToString().TrimEnd('\r', '\n');
    }
}
