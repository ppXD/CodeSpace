using CodeSpace.Core.Services.Agents.Harnesses.Codex.Exceptions;

namespace CodeSpace.Core.Services.Agents.Harnesses.Codex;

/// <summary>
/// Every <c>SKILL.md</c> the Codex CLI would load out of a checkout, so a task that loads no repository instructions — an
/// output reviewer, whose cwd is the branch under review — can switch each one off by its path (see
/// <c>CodexHarness.AppendRepositorySkillExclusion</c>). The pinned 0.142.2 reads a repository's skills from two roots in
/// its cwd: <c>.agents/skills</c>, and the <c>skills</c> folder of the cwd's <c>.codex</c> config layer, which it reads
/// even with the workspace marked untrusted. It lists each skill to the model, and a <c>$name</c> in the goal injects that
/// file into the turn before the model acts. A reviewer's goal holds the producer's goal and the names of the files the
/// producer changed, so without this the reviewed party could name a skill it also wrote.
///
/// <para>The walk is the CLI's own (codex-rs <c>core-skills/loader.rs</c>, 0.142.2): breadth first from each root, at
/// most <see cref="MaxScanDepth"/> directories down, skipping every entry whose name starts with a dot, following a linked
/// directory wherever it leads and reading each physical directory once. A regular file named <c>SKILL.md</c> is a
/// skill; a link named so is not. The CLI stops reading a root after <see cref="MaxDirectoriesPerRoot"/> directories, and
/// which ones it read by then depends on the order it read them in, so a root past that is refused rather than half
/// switched off (<see cref="CodexSkillScanTruncatedException"/>). A directory below the cwd is never a root: the CLI looks
/// only from the project root down to the cwd, and a checkout's root is both.</para>
///
/// <para>Each file is named as reached from the workspace as given, and again from the workspace's physical directory
/// when that differs, as <c>CodexHarness.AppendWorkspaceDistrust</c> keys the workspace. The CLI resolves a rule's path
/// before comparing it, in its own view of the filesystem, which under confinement is the workspace bound at the given
/// path. All of it was measured against 0.142.2 (<c>GoalChannelE2ETests.CodexSkills</c>, <c>CodexRepositorySkillsTests</c>).</para>
/// </summary>
internal static class CodexRepositorySkills
{
    /// <summary>How many directories below a root the CLI reads (codex-rs <c>MAX_SCAN_DEPTH</c>, 0.142.2). Pinned by a test.</summary>
    internal const int MaxScanDepth = 6;

    /// <summary>The most directories the CLI reads under one root, the root included (codex-rs <c>MAX_SKILLS_DIRS_PER_ROOT</c>, 0.142.2). Pinned by a test.</summary>
    internal const int MaxDirectoriesPerRoot = 2000;

    private const string SkillFileName = "SKILL.md";

    /// <summary>The roots in its cwd the CLI reads a repository's skills from.</summary>
    private static readonly string[] Roots = [Path.Combine(".agents", "skills"), Path.Combine(".codex", "skills")];

    /// <summary>Every entry of a directory, dot-named ones included (the walk skips those itself), and none it may not read.</summary>
    private static readonly EnumerationOptions EveryEntry = new() { AttributesToSkip = 0, IgnoreInaccessible = true };

    /// <summary>Every skill file the CLI would load from <paramref name="workspace"/>, under each spelling of the workspace, in a stable order so the same checkout always builds the same argv.</summary>
    public static IReadOnlyList<string> In(string workspace)
    {
        var skills = Roots.SelectMany(root => Under(Path.Combine(workspace, root))).Order(StringComparer.Ordinal).ToList();
        var physical = PhysicalPath.Directory(workspace);

        return skills.Concat(skills.Select(skill => Path.Combine(physical, Path.GetRelativePath(workspace, skill)))).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The skill files below one root, read breadth first the way the CLI reads them.</summary>
    private static List<string> Under(string root)
    {
        var skills = new List<string>();

        if (!Directory.Exists(root)) return skills;

        var visited = new HashSet<string>(StringComparer.Ordinal) { PhysicalPath.Directory(root) };
        var pending = new Queue<(string Directory, int Depth)>([(root, 0)]);

        while (pending.TryDequeue(out var next))
            foreach (var entry in Entries(next.Directory))
                Visit(entry, next.Depth, visited, pending, skills);

        return skills;
    }

    /// <summary>One entry of a directory at <paramref name="depth"/>: a directory, or a link to one, is read next; a regular file named <see cref="SkillFileName"/> is a skill.</summary>
    private static void Visit(FileSystemInfo entry, int depth, HashSet<string> visited, Queue<(string Directory, int Depth)> pending, List<string> skills)
    {
        if (entry.Name.StartsWith('.')) return;

        if (Directory.Exists(entry.FullName)) Enqueue(entry.FullName, depth + 1, visited, pending);
        else if (entry.LinkTarget is null && entry.Name == SkillFileName) skills.Add(entry.FullName);
    }

    /// <summary>A directory the CLI reads too, unless it is past the depth it reads to or a physical directory it has read already; one past its per-root limit refuses the task.</summary>
    private static void Enqueue(string directory, int depth, HashSet<string> visited, Queue<(string Directory, int Depth)> pending)
    {
        if (depth > MaxScanDepth) return;

        var physical = PhysicalPath.Directory(directory);

        if (visited.Contains(physical)) return;

        if (visited.Count >= MaxDirectoriesPerRoot)
            throw new CodexSkillScanTruncatedException($"a skill folder of this checkout holds more than the {MaxDirectoriesPerRoot} directories codex reads under one root, so which of its skills codex would load cannot be told and none can be reliably switched off; the run was refused before it started.");

        visited.Add(physical);
        pending.Enqueue((directory, depth));
    }

    /// <summary>The entries of a directory; none when it cannot be read, which the CLI skips the same way.</summary>
    private static FileSystemInfo[] Entries(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).GetFileSystemInfos("*", EveryEntry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
