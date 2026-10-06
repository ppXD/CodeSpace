using System.Globalization;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Harnesses.Claude;

/// <summary>
/// The directories a Claude run adds back with <c>--add-dir</c> so their memory loads (see
/// <c>ClaudeCodeHarness.AppendSettingsPin</c>), less every one whose memory would bring in bytes from outside the
/// workspace.
///
/// <para>The pinned 2.1.263 opens an added directory's <c>CLAUDE.md</c> and <c>.claude/CLAUDE.md</c> by path and
/// follows a symlink at either wherever it leads, and a <c>.claude</c> directory that is itself a link brings in what
/// the directory it leads to holds, its rules included. What it reaches is handed to the model as the repository's
/// instructions; RepositoryConfigE2ETests pins all three against the real binary. One such target is
/// <c>/proc/self/environ</c>, which on Linux holds the CLI's own environment and with it the run's broker token. The
/// same CLI does not follow a <c>.claude/rules</c> entry, file or folder, that links outside the added directory, and
/// refuses an import that resolves outside its cwd unless external includes are approved, which a fresh per-run config
/// home never is (both observed against 2.1.263). The guard counts those as escapes too, so a later CLI that follows
/// them reaches nothing: before a directory is added, everything its memory can reach is resolved the way the kernel
/// resolves it (<see cref="PhysicalPath.File"/>) — those memory files, the <c>.claude</c> directory itself, every
/// markdown file and folder under <c>.claude/rules</c>, and every file they @-import, to <see cref="MaxImportHops"/>
/// hops. If any of it resolves outside the workspace, the directory is left out whole and the run's timeline says so
/// (<see cref="Plan.Notices"/>). A link that stays inside the workspace — <c>CLAUDE.md</c> to <c>AGENTS.md</c>, or one
/// repository's rule to a sibling repository the same workspace holds — keeps loading, and so does one that dangles,
/// which gives the CLI nothing to read.</para>
///
/// <para>Any <c>@</c> followed by a run of non-space is taken for an import, inside code blocks too and with no space
/// before it, wherever its target resolves: a superset of the CLI's own grammar. One exception, documented rather than
/// closed: a <c>~</c> import names the run's own home, which under confinement is its config home and does not exist
/// yet when the invocation is built, so there is nothing to resolve; the CLI's refusal is its only guard.</para>
///
/// <para>Every file is opened no-follow, non-blocking and only if regular (<see cref="LocalAcceptanceFileIdentity.Open"/>),
/// so a FIFO in a repository cannot hang the build; the CLI reads no FIFO either. The bounds below only cap what one
/// build may spend; what cannot be checked within them, or at all, leaves the directory out too, and the build never
/// throws for it. The check runs on every build, a revise round's included, and nothing writes the workspace while it
/// does: no agent process is running before a round starts. On a host that is neither Linux nor macOS the directories
/// are added unchecked.</para>
/// </summary>
internal static partial class ClaudeWorkspaceMemory
{
    /// <summary>How many @-imports deep the guard follows: the CLI reads none at depth 5 (<c>wgs=5</c> in 2.1.263). Pinned by a test.</summary>
    internal const int MaxImportHops = 5;

    /// <summary>
    /// The most paths one directory's memory may give the guard to resolve — every entry its rules folders list and every
    /// distinct import — before it is left out unchecked. Each costs a walk of its path, a name that resolves to nothing
    /// included, so this bounds the build, not what the CLI loads. Pinned by a test.
    /// </summary>
    internal const int MaxLookups = 16384;

    /// <summary>The most bytes the guard reads from one directory's memory, every file it scans together; more leaves the directory out. Pinned by a test.</summary>
    internal const int MaxScannedBytes = 4 * 1024 * 1024;

    /// <summary>The longest file or directory name a notice repeats; a longer one is cut. Pinned by a test.</summary>
    internal const int MaxNoticeNameLength = 120;

    /// <summary>The directories to add, in order, and one sentence for each directory left out.</summary>
    internal sealed record Plan(IReadOnlyList<string> Directories, IReadOnlyList<string> Notices);

    public static Plan For(AgentTask task)
    {
        if (string.IsNullOrWhiteSpace(task.WorkspaceDirectory)) return new Plan([], []);

        var roots = RootDirectories(task).ToList();

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return new Plan(roots, []);

        var leftOut = LeftOut(ProvisionedRoot(task), roots);
        var notices = leftOut.Select(item => Notice(task.WorkspaceDirectory, item.Root, item.Why)).ToList();

        return new Plan(roots.Except(leftOut.Select(item => item.Root), StringComparer.Ordinal).ToList(), notices);
    }

    /// <summary>
    /// The workspace, then every repository directory inside it. A repository outside it — a sibling of a cwd at the
    /// primary repository — is left out: the unpinned CLI never loaded its memory either, and an added directory also
    /// widens what the CLI's tools may touch.
    /// </summary>
    private static IEnumerable<string> RootDirectories(AgentTask task)
    {
        var workspace = task.WorkspaceDirectory!;
        var inside = Path.TrimEndingDirectorySeparator(workspace) + Path.DirectorySeparatorChar;

        return new[] { workspace }.Concat((task.WorkspaceRepositoryDirectories ?? []).Where(directory => directory.StartsWith(inside, StringComparison.Ordinal))).Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// The directory the run's workspace was provisioned as, which memory may link anywhere inside: the cwd, or — when the
    /// cwd is one of several repositories that sit side by side (a primary-repository cwd) — the root that holds them,
    /// its parent. Those siblings are the run's own clones, not bytes from outside it. Repository directories laid out
    /// any other way widen nothing.
    /// </summary>
    private static string ProvisionedRoot(AgentTask task)
    {
        var cwd = Path.TrimEndingDirectorySeparator(task.WorkspaceDirectory!);
        var parent = Path.GetDirectoryName(cwd);
        var repositories = (task.WorkspaceRepositoryDirectories ?? []).Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.Ordinal).ToList();

        return parent is not null && repositories.Count > 1 && repositories.Contains(cwd, StringComparer.Ordinal) && repositories.All(directory => Path.GetDirectoryName(directory) == parent) ? parent : cwd;
    }

    /// <summary>Every root whose memory reaches outside the workspace or cannot be checked, in order, with why. The workspace is resolved by the same walker as everything its memory reaches, so the two sides of the comparison cannot disagree on a link.</summary>
    private static List<(string Root, string Why)> LeftOut(string workspace, IEnumerable<string> roots)
    {
        var physical = PhysicalPath.File(workspace) ?? workspace;

        return roots.Select(root => (Root: root, Why: FirstEscape(physical, root))).Where(item => item.Why is not null).Select(item => (item.Root, item.Why!)).ToList();
    }

    /// <summary>Why one root must be left out, or null when all of its memory stays inside the workspace. A directory the guard cannot finish reading is left out rather than failing the launch.</summary>
    private static string? FirstEscape(string workspace, string root)
    {
        try
        {
            return new Closure(workspace, root).FirstEscape();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return "its memory could not be checked";
        }
    }

    private static string Notice(string workspace, string root, string why) => $"Left the memory in {Place(workspace, root)} out of this run: {why}.";

    private static string Place(string workspace, string root) => Path.GetRelativePath(workspace, root) switch
    {
        "." => "the workspace",
        var relative => $"'{Printable(relative)}'",
    };

    /// <summary>A repository-chosen name as a notice repeats it: control and format characters replaced, length cut.</summary>
    private static string Printable(string name)
    {
        var clean = new string(name.Select(c => IsUnprintable(c) ? '?' : c).ToArray());

        return clean.Length <= MaxNoticeNameLength ? clean : clean[..MaxNoticeNameLength] + "…";
    }

    private static bool IsUnprintable(char c) => char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}
