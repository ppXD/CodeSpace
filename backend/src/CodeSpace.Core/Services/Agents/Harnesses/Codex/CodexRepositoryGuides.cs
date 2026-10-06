using System.Text;
using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Harnesses.Codex;

/// <summary>
/// The <c>AGENTS.md</c> of every repository below a multi-repo run's cwd, for the run's own <c>CODEX_HOME/AGENTS.md</c>
/// (see <c>CodexHarness.BuildConfigHomeFiles</c>). Codex reads a project doc from its cwd and the directories above it,
/// never from one below (observed against 0.142.2), and a multi-repo run's cwd is the workspace root, which holds each
/// repository in a folder of its own and is no repository itself, so none of theirs reached the model. A run whose cwd
/// is a repository — single-repo, or at the primary repository — gets nothing here: Codex loads that doc itself, and
/// appending it would hand the model the doc twice.
///
/// <para>Each repository strictly inside the cwd gives the doc Codex would pick at its root: <c>AGENTS.override.md</c>
/// whenever that is a file or a link, even one that holds nothing, else <c>AGENTS.md</c>. It is read as Codex reads a
/// project doc — its first <see cref="MaxProjectDocBytes"/> decoded lossily, nothing at all when that is only
/// whitespace — and appended after the persona and the operating contract under a separator that names it,
/// <c>--- project-doc (&lt;repository&gt;/AGENTS.md) ---</c>, beside Codex's own <c>--- project-doc ---</c>. That is the
/// instruction block a single-repo run's doc reaches, at the same authority. Codex gives the file no import or mention
/// semantics (observed against 0.142.2: an <c>@</c> path, a <c>~</c> path or a <c>$skill</c> in it attaches nothing),
/// so the repository's text reaches the model and nothing it names does.</para>
///
/// <para>A doc that resolves outside the workspace (<see cref="PhysicalPath.File"/>) is left out, and so is one whose
/// repository's path below the cwd holds anything but <see cref="SafeRelativePath"/>'s characters, since the separator
/// repeats it; the run's timeline says so, and says when a doc was cut. A doc that dangles or is no regular file gives
/// nothing. Every file is opened no-follow, non-blocking and only if regular, so a FIFO cannot hang the build. The read
/// runs on every build, a revise round's included, when no agent process is running. On a host that is neither Linux nor
/// macOS nothing is appended.</para>
/// </summary>
internal static partial class CodexRepositoryGuides
{
    /// <summary>The most bytes of one repository's doc a run is given: Codex's own <c>project_doc_max_bytes</c> default, past which 0.142.2 hands the model no more of a project doc. Pinned by a test.</summary>
    internal const int MaxProjectDocBytes = 32 * 1024;

    /// <summary>The docs Codex looks for at a repository's root, the one it picks first.</summary>
    private static readonly string[] DocNames = ["AGENTS.override.md", "AGENTS.md"];

    private static readonly Plan None = new("", []);

    private static readonly Doc Nothing = new("", []);

    /// <summary>The characters a repository's path below the cwd may hold to be named in a separator.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._@+\-/]+\z")]
    private static partial Regex SafeRelativePath();

    [GeneratedRegex(@"[^A-Za-z0-9._@+\-/]")]
    private static partial Regex UnsafeCharacter();

    /// <summary>What the run's <c>AGENTS.md</c> gains after the persona and the operating contract — empty for a run whose cwd is a repository — and one sentence for each doc left out or cut.</summary>
    internal sealed record Plan(string Appendix, IReadOnlyList<string> Notices);

    /// <summary>One repository's block, empty when it gives nothing, and what the timeline says about it.</summary>
    private sealed record Doc(string Block, IReadOnlyList<string> Notices);

    public static Plan For(AgentTask task)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return None;

        var repositories = RepositoriesBelow(task);

        if (repositories.Count == 0) return None;

        var cwd = Normalized(task.WorkspaceDirectory!);
        var workspace = PhysicalPath.File(cwd) ?? cwd;
        var docs = repositories.Select(repository => DocOf(cwd, workspace, repository)).ToList();

        return new Plan(string.Concat(docs.Select(doc => doc.Block)), docs.SelectMany(doc => doc.Notices).ToList());
    }

    /// <summary>Every repository strictly inside the cwd, once each, in the order the executor names them; none when the cwd is itself a repository or no workspace was materialised.</summary>
    private static List<string> RepositoriesBelow(AgentTask task)
    {
        if (string.IsNullOrWhiteSpace(task.WorkspaceDirectory)) return [];

        var cwd = Normalized(task.WorkspaceDirectory);
        var repositories = (task.WorkspaceRepositoryDirectories ?? []).Select(Normalized).Distinct(StringComparer.Ordinal).ToList();

        if (repositories.Contains(cwd, StringComparer.Ordinal)) return [];

        return repositories.Where(directory => directory.StartsWith(cwd + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToList();
    }

    private static string Normalized(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    /// <summary>The doc one repository gives, as the physical <paramref name="workspace"/> bounds it.</summary>
    private static Doc DocOf(string cwd, string workspace, string repository)
    {
        if (Pick(repository) is not { } name) return Nothing;

        var relative = Path.GetRelativePath(cwd, repository);

        if (!SafeRelativePath().IsMatch(relative)) return LeftOut(relative, name, "its path holds a character other than a letter, a digit or one of . _ @ + - /");

        if (PhysicalPath.File(Path.Combine(repository, name)) is not { } physical) return Nothing;

        if (!PhysicalPath.StaysInside(workspace, physical)) return LeftOut(relative, name, "it resolves outside the workspace");

        return Read(physical) is { } bytes ? Rendered(relative, name, bytes) : Nothing;
    }

    /// <summary>The doc Codex picks at the repository's root: the first of <see cref="DocNames"/> that is a file or a link, whatever it holds or leads to; null when there is neither.</summary>
    private static string? Pick(string repository) => DocNames.FirstOrDefault(name => IsFileOrLink(Path.Combine(repository, name)));

    private static bool IsFileOrLink(string path)
    {
        try
        {
            var entry = new FileInfo(path);

            return entry.LinkTarget is not null || entry.Exists;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A regular file's bytes, opened no-follow and non-blocking, read to one past <see cref="MaxProjectDocBytes"/> so a longer doc is known to be cut; null for anything else.</summary>
    private static byte[]? Read(string physical)
    {
        try
        {
            using var stream = new FileStream(LocalAcceptanceFileIdentity.Open(physical, directory: false), FileAccess.Read);
            var buffer = new byte[MaxProjectDocBytes + 1];
            var total = 0;

            for (int read; total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0;) total += read;

            return buffer[..total];
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The doc's block: its first <see cref="MaxProjectDocBytes"/> decoded as Codex decodes them, a character split at the cut included, under the separator naming it; nothing for a doc of only whitespace.</summary>
    private static Doc Rendered(string relative, string name, byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaxProjectDocBytes));

        if (string.IsNullOrWhiteSpace(text)) return Nothing;

        var cut = bytes.Length > MaxProjectDocBytes ? [$"Left all but the first {MaxProjectDocBytes} bytes of the {name} of '{relative}' out of this run: Codex reads no more of a project doc."] : Array.Empty<string>();

        return new Doc($"\n\n--- project-doc ({relative}/{name}) ---\n\n{text}", cut);
    }

    /// <summary>No block, and the one sentence that says why; the repository's path as the sentence repeats it has every character the separator could not carry replaced.</summary>
    private static Doc LeftOut(string relative, string name, string why) => new("", [$"Left the {name} of '{UnsafeCharacter().Replace(relative, "?")}' out of this run: {why}."]);
}
