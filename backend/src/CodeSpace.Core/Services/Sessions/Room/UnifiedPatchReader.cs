namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>How a single file changed within a unified diff — read off the git per-file header, not guessed.</summary>
public enum PatchFileChange { Added, Modified, Deleted, Renamed, Binary }

/// <summary>
/// One file's view extracted from a larger git unified diff (Rule 18.1: an internal Core parse noun, never crosses a
/// seam — the seam DTO is <c>RoomFilePreview</c>). <see cref="PostImage"/> is the reconstructed full content and is
/// non-null ONLY for an ADDED text file (whose every body line is an addition, so the post-image is fully specified by
/// the diff); a modified file can't be reconstructed without its base, so only <see cref="DiffText"/> (the raw per-file
/// section) is offered.
/// </summary>
public sealed record PatchFileView(string Path, PatchFileChange Change, bool IsBinary, string? PostImage, string DiffText);

/// <summary>
/// PURE extraction of ONE file's section out of a git unified diff (the format <c>git diff</c> emits, WITH
/// <c>diff --git a/… b/…</c> headers) — no IO, unit-tested like <c>FileContentDecoder</c>. Splits the patch into
/// per-file blocks, matches the requested repo-relative path against a block's post-image path (or its rename/delete
/// target), classifies the change off the git header, and — for an added text file — reconstructs the full content
/// from the addition lines. Returns null when no block targets the path.
/// </summary>
public static class UnifiedPatchReader
{
    private const string BlockMarker = "diff --git ";

    public static PatchFileView? Read(string patch, string path)
    {
        if (string.IsNullOrEmpty(patch) || string.IsNullOrEmpty(path)) return null;

        foreach (var block in SplitBlocks(patch))
            if (string.Equals(TargetPath(block), path, StringComparison.Ordinal))
                return BuildView(block, path);

        return null;
    }

    /// <summary>Split the diff into per-file blocks, each starting at a <c>diff --git</c> line and running to the next. Split on the LF git separates lines with — a CRLF file's content keeps its <c>\r</c> so the reconstructed post-image is byte-faithful.</summary>
    private static IEnumerable<IReadOnlyList<string>> SplitBlocks(string patch)
    {
        var lines = patch.Split('\n');

        var current = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith(BlockMarker, StringComparison.Ordinal) && current.Count > 0)
            {
                yield return current;
                current = new List<string>();
            }

            current.Add(line);
        }

        if (current.Count > 0 && current[0].StartsWith(BlockMarker, StringComparison.Ordinal)) yield return current;
    }

    /// <summary>The file a block targets: its post-image path (<c>+++ b/…</c>), else the rename target, else the deleted a-side, else the header's b-side.</summary>
    private static string? TargetPath(IReadOnlyList<string> block)
    {
        foreach (var line in block)
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var p = StripPrefix(line[4..]);
                if (p != null) return p;   // "/dev/null" (a delete) falls through to the a-side below
            }

        foreach (var line in block)
            if (line.StartsWith("rename to ", StringComparison.Ordinal))
                return line["rename to ".Length..].Trim();

        foreach (var line in block)
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                var p = StripPrefix(line[4..]);
                if (p != null) return p;
            }

        return HeaderBSide(block[0]);
    }

    /// <summary>Parse the b-side path out of a <c>diff --git a/X b/Y</c> header (best-effort; the +++/--- lines are preferred).</summary>
    private static string? HeaderBSide(string header)
    {
        var rest = header[BlockMarker.Length..].Trim();

        var marker = rest.IndexOf(" b/", StringComparison.Ordinal);
        if (marker < 0) return null;

        return rest[(marker + 3)..].Trim();
    }

    /// <summary>Strip git's <c>a/</c> or <c>b/</c> prefix and a trailing tab-metadata; null for <c>/dev/null</c>.</summary>
    private static string? StripPrefix(string sidePath)
    {
        var value = sidePath.Trim();

        if (value == "/dev/null") return null;

        // git c-quotes a path with a tab / quote / backslash / non-ASCII byte: `"b/…"`. Strip the a//b/ INSIDE the
        // quotes so the result keeps the SAME c-quoted form git also emits in --name-only (the ChangedFiles the caller
        // matched against) — unquoting here would mismatch that quoted request.
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            if (value.StartsWith("\"a/", StringComparison.Ordinal) || value.StartsWith("\"b/", StringComparison.Ordinal))
                value = "\"" + value[3..];

            return value.Length <= 2 ? null : value;
        }

        var tab = value.IndexOf('\t');
        if (tab >= 0) value = value[..tab];

        if (value.StartsWith("a/", StringComparison.Ordinal) || value.StartsWith("b/", StringComparison.Ordinal))
            value = value[2..];

        return value.Length == 0 ? null : value;
    }

    private static PatchFileView BuildView(IReadOnlyList<string> block, string path)
    {
        var isBinary = block.Any(l => l.StartsWith("Binary files ", StringComparison.Ordinal) || l.StartsWith("GIT binary patch", StringComparison.Ordinal));

        var change = Classify(block, isBinary);

        var diffText = string.Join("\n", block).TrimEnd('\n');

        var postImage = change == PatchFileChange.Added && !isBinary ? ReconstructAdded(block) : null;

        return new PatchFileView(path, change, isBinary, postImage, diffText);
    }

    private static PatchFileChange Classify(IReadOnlyList<string> block, bool isBinary)
    {
        if (isBinary) return PatchFileChange.Binary;

        if (block.Any(l => l.StartsWith("rename from ", StringComparison.Ordinal) || l.StartsWith("rename to ", StringComparison.Ordinal)))
            return PatchFileChange.Renamed;

        if (block.Any(l => l.StartsWith("new file mode", StringComparison.Ordinal))) return PatchFileChange.Added;

        if (block.Any(l => l.StartsWith("deleted file mode", StringComparison.Ordinal))) return PatchFileChange.Deleted;

        return PatchFileChange.Modified;
    }

    /// <summary>The full content of an added file = every hunk-body line prefixed with '+' (the header <c>+++</c> is skipped), sans that '+'.</summary>
    private static string ReconstructAdded(IReadOnlyList<string> block)
    {
        var body = new List<string>();

        var inHunk = false;

        foreach (var line in block)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal)) { inHunk = true; continue; }

            if (!inHunk) continue;

            if (line.StartsWith("+", StringComparison.Ordinal)) body.Add(line[1..]);
        }

        return string.Join("\n", body);
    }
}
