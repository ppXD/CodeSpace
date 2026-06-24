using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A fake Codex CLI for the CLEAN multi-repo whole-loop (the non-conflicting analogue of
/// <see cref="MultiRepoConflictFakeCli"/>): in a multi-repo workspace the harness runs with cwd = the workspace ROOT
/// and each repository is cloned into its OWN subdirectory by alias (<c>&lt;root&gt;/<see cref="PrimaryAlias"/>/</c>,
/// <c>&lt;root&gt;/<see cref="RelatedAlias"/>/</c>), so this CLI writes PER-SUBDIRECTORY into BOTH repos. Each spawned
/// agent adds a DISJOINT, goal-slugged file in EACH repo, so both repos integrate CLEANLY (no conflict) and the run
/// produces a per-repo reviewable head for EACH — the shape a multi-repo "feature spanning two repos" loop leaves.
///
/// <para>Behaviour is a pure function of the GOAL (Codex's last positional arg) → no external state, bwrap-safe; writes
/// only into the run's own multi-repo workspace. POSIX <c>/bin/sh</c> only. The disjoint per-goal filename means two
/// parallel agents never collide, so a multi-agent fan-out integrates cleanly on BOTH axes.</para>
/// </summary>
public sealed class MultiRepoFeatureFakeCli : IDisposable
{
    /// <summary>The primary repo's alias (the <c>WorkspaceSpec</c> default) → its clone subdirectory under the workspace root.</summary>
    public const string PrimaryAlias = "repo";

    /// <summary>The related repo's alias → its clone subdirectory. The test MUST author the related repo with this alias so the CLI's subdir writes land in its clone.</summary>
    public const string RelatedAlias = "api";

    /// <summary>The filename prefix each agent writes in each repo — the per-repo acceptance check.sh looks for <c>agent_*.txt</c>.</summary>
    public const string FilePrefix = "agent_";

    private readonly string _originalCommand;
    private readonly string _dir;

    public MultiRepoFeatureFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-multirepofeature-fakecli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var script = Path.Combine(_dir, "fake-codex.sh");
        File.WriteAllText(script, ScriptBody);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _originalCommand = Environment.GetEnvironmentVariable(CodexHarness.CommandEnvVar) ?? "";
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, script);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, _originalCommand.Length == 0 ? null : _originalCommand);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Derive a filesystem-safe filename from the goal (Codex's last positional arg) and write a DISJOINT file under
    /// BOTH repo subdirs, then print the three-line codex-shaped JSONL stream the real ParseEvent folds. The per-repo
    /// writes land in each repo's clone → the executor captures a RepositoryRunResult for each → both integrate cleanly.
    /// </summary>
    private static string ScriptBody =>
        "#!/bin/sh\n" +
        "goal=\"\"\n" +
        "for goal in \"$@\"; do :; done\n" +
        "esc=$(printf '%s' \"$goal\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
        "fname=$(printf '%s' \"$goal\" | tr -c 'A-Za-z0-9' '_')\n" +
        "printf 'primary work for: %s\\n' \"$goal\" > \"" + PrimaryAlias + "/" + FilePrefix + "${fname}.txt\"\n" +
        "printf 'api work for: %s\\n' \"$goal\" > \"" + RelatedAlias + "/" + FilePrefix + "${fname}.txt\"\n" +
        "printf '{\"type\":\"agent_reasoning\",\"message\":\"editing both repos for: %s\"}\\n' \"$esc\"\n" +
        "printf '{\"type\":\"agent_message\",\"message\":\"DONE: %s\"}\\n' \"$esc\"\n" +
        "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "exit 0\n";
}
