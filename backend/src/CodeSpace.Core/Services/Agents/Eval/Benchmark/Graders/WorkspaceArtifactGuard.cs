namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;

/// <summary>
/// The SHARED containment + read layer every deliverable-reading grader stands on (artifact-present, LLM-judge,
/// citations, schema): a repo-relative path counts ONLY when it resolves to a real filesystem entry STRICTLY within
/// the workspace root — a blank path, a <c>../</c> escape, an absolute path, the root itself, and a SYMLINK whose
/// final target leaves the clone all read as missing (fail-closed). One home for the clamp so a hardening fix lands
/// in every oracle at once, never in one grader's private copy.
/// </summary>
internal static class WorkspaceArtifactGuard
{
    /// <summary>
    /// True when the repo-relative path resolves to an existing file or directory STRICTLY within the workspace root.
    /// Every way of NOT being a real in-clone deliverable reads as missing (fail-closed): a blank path; a <c>../</c>
    /// escape or absolute path (lexically clamped); the workspace root itself (<c>.</c> / <c>""</c> — the clone dir is
    /// never a deliverable, and it always exists, so admitting it would be a silent pass); and a SYMLINK whose final
    /// target leaves the clone. The last guard matters because <see cref="File.Exists(string)"/> /
    /// <see cref="Directory.Exists(string)"/> FOLLOW symlinks — a committed <c>report.md → /etc/passwd</c> spells
    /// in-bounds but its target is outside — so the link is resolved to its final target and re-clamped to root.
    /// </summary>
    public static bool ExistsWithin(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        var full = Path.GetFullPath(Path.Combine(root, relativePath));

        // STRICT containment: must live UNDER root — a ../ escape, an absolute path, OR the root dir itself all fail
        // (root never ends with the separator, so `root.StartsWith(root + sep)` is false → the root-self case is rejected).
        if (!IsStrictlyWithin(root, full)) return false;

        if (!File.Exists(full) && !Directory.Exists(full)) return false;

        // The lexical path exists + spells in-bounds, but the existence probe followed any symlink — resolve the link to
        // its FINAL target and re-clamp, so an in-clone symlink pointing OUT of the clone reads as missing (fail-closed).
        var info = Directory.Exists(full) ? (FileSystemInfo)new DirectoryInfo(full) : new FileInfo(full);
        var resolved = info.ResolveLinkTarget(returnFinalTarget: true);

        return resolved is null || IsStrictlyWithin(root, Path.GetFullPath(resolved.FullName));   // non-symlink → the in-bounds lexical path IS the real path
    }

    /// <summary>
    /// Read a deliverable FILE's text under the same containment rules as <see cref="ExistsWithin"/>, bounded by
    /// <paramref name="maxBytes"/> (an over-cap file is truncated with a visible marker — a judged/parsed artifact must
    /// never balloon a prompt or the heap). False — with a fail-closed <paramref name="error"/> — when the path is not
    /// a real in-clone FILE (a directory is not readable content).
    /// </summary>
    public static bool TryReadWithin(string root, string relativePath, long maxBytes, out string content, out string? error)
    {
        content = "";
        error = null;

        if (!ExistsWithin(root, relativePath))
        {
            error = $"artifact-missing: {relativePath}";
            return false;
        }

        var full = Path.GetFullPath(Path.Combine(root, relativePath));

        if (Directory.Exists(full))
        {
            error = $"artifact-not-a-file: {relativePath}";
            return false;
        }

        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);

        var buffer = new char[maxBytes];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);

        content = new string(buffer, 0, read);

        if (reader.Peek() >= 0) content += "\n[... truncated for grading ...]";

        return true;
    }

    /// <summary>True when <paramref name="candidate"/> lives STRICTLY under <paramref name="root"/> (a proper descendant — not root itself, not an escape).</summary>
    public static bool IsStrictlyWithin(string root, string candidate) =>
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
