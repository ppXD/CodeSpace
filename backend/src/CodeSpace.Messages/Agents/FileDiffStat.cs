namespace CodeSpace.Messages.Agents;

/// <summary>
/// One changed file's line diffstat — the added / removed line counts, git ground truth (from <c>git diff --numstat</c>),
/// parallel to the changed-file path list. <see cref="Additions"/> / <see cref="Deletions"/> are NULL for a BINARY file
/// (numstat reports "-" for both, having no line concept). The turn-level "+X −Y" a reader sees is the SUM of the
/// non-null counts; a per-file row shows this file's own "+a −d". A data noun (Rule 18.1) — captured at run end onto the
/// durable <c>AgentRunResult</c> so the "+X −Y" survives even when the full patch is offloaded.
/// </summary>
public sealed record FileDiffStat(string Path, int? Additions, int? Deletions);
