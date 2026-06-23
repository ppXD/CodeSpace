using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Supervisor;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A fake Codex CLI for the MULTI-repo conflict→resolve whole-loop E2E — the multi-repo analogue of
/// <see cref="ConflictThenResolveFakeCli"/>. In a multi-repo workspace the harness runs with cwd = the workspace ROOT
/// and each repository is cloned into its OWN subdirectory by alias (<c>&lt;root&gt;/<see cref="PrimaryAlias"/>/</c>,
/// <c>&lt;root&gt;/<see cref="RelatedAlias"/>/</c>), so this CLI writes PER-SUBDIRECTORY (the flat-to-cwd
/// <see cref="ConflictThenResolveFakeCli"/> would land its writes at the workspace root, outside any repo clone).
///
/// <para>It stages exactly the "one repo conflicts, one stays clean" shape the per-repo resolve loop is built for: the
/// two parallel agents each add a DISJOINT file in the PRIMARY repo (→ that repo integrates CLEANLY) and each write
/// DIFFERENT content to the same <see cref="SharedFile"/> in the RELATED repo (→ a REAL git conflict on that axis only).
/// The supervisor's multi-repo <c>resolve</c> turn then runs ONE resolver agent — recognised by the alias-independent
/// <see cref="ResolverMarker"/> the multi-repo recipe emits — which writes a single reconciled
/// <see cref="RelatedAlias"/>/<see cref="SharedFile"/> and ends its summary with
/// <see cref="SupervisorResolverRecipe.TestsPassedMarker"/> so the per-repo resolution grades Verified.</para>
///
/// <para>Behaviour is a pure function of the GOAL (no external state) → bwrap-safe. POSIX <c>/bin/sh</c> only; writes
/// only into the run's own multi-repo workspace. The base of BOTH repos must seed <see cref="SharedFile"/> in the
/// related repo (so each side is a real diff against a common base) — the test does this.</para>
/// </summary>
public sealed class MultiRepoConflictFakeCli : IDisposable
{
    /// <summary>The primary repo's alias (the <c>WorkspaceSpec</c> default) → its clone subdirectory under the workspace root. Both agents add a disjoint file here so it integrates cleanly.</summary>
    public const string PrimaryAlias = "repo";

    /// <summary>The related repo's alias → its clone subdirectory. The test MUST author the related repo with this alias so the CLI's subdir writes land in its clone. Both agents conflict here.</summary>
    public const string RelatedAlias = "api";

    /// <summary>The single file both parallel agents edit in the RELATED repo (so their patches conflict) and the resolver reconciles. Must be seeded in the related repo's base.</summary>
    public const string SharedFile = "shared.txt";

    /// <summary>The alias-independent substring the MULTI-repo resolver recipe emits (<c>SupervisorResolverRecipe.BuildMultiRepoInstruction</c>) the CLI keys on to recognise "I am the multi-repo resolver" — present in every multi-repo resolver goal, never in a spawn subtask goal.</summary>
    public const string ResolverMarker = "across multiple repositories";

    /// <summary>A spawn instruction containing this marker takes the FIRST (alpha) side of the related-repo conflict; anything else (and not the resolver) takes the beta side.</summary>
    public const string AlphaMarker = "alpha";

    private readonly string _originalCommand;
    private readonly string _dir;

    public MultiRepoConflictFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-multirepoconflict-fakecli-" + Guid.NewGuid().ToString("N"));
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
    /// Resolve the goal (Codex's last positional arg) PER SUBDIRECTORY. The RESOLVER (goal carries
    /// <see cref="ResolverMarker"/>) writes one reconciled <see cref="RelatedAlias"/>/<see cref="SharedFile"/> + ends
    /// with the verified token. Each spawn agent adds a DISJOINT file in <see cref="PrimaryAlias"/>/ (clean axis) and
    /// writes its side to <see cref="RelatedAlias"/>/<see cref="SharedFile"/> (conflicting axis).
    /// </summary>
    private static string ScriptBody =>
        "#!/bin/sh\n" +
        "goal=\"\"\n" +
        "for goal in \"$@\"; do :; done\n" +
        "esc=$(printf '%s' \"$goal\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
        "case \"$goal\" in\n" +
        "  *\"" + ResolverMarker + "\"*)\n" +
        "    printf 'reconciled: alpha + beta\\n' > " + RelatedAlias + "/" + SharedFile + "\n" +
        "    printf '{\"type\":\"agent_reasoning\",\"message\":\"reconciling the " + RelatedAlias + " branches\"}\\n'\n" +
        "    printf '{\"type\":\"agent_message\",\"message\":\"build + full test suite pass " + SupervisorResolverRecipe.TestsPassedMarker + "\"}\\n'\n" +
        "    printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "    exit 0\n" +
        "    ;;\n" +
        "  *" + AlphaMarker + "*)\n" +
        "    printf 'alpha primary work\\n' > " + PrimaryAlias + "/agent_alpha.txt\n" +
        "    printf 'the alpha side of the change\\n' > " + RelatedAlias + "/" + SharedFile + "\n" +
        "    printf '{\"type\":\"agent_message\",\"message\":\"DONE: %s\"}\\n' \"$esc\"\n" +
        "    printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "    exit 0\n" +
        "    ;;\n" +
        "esac\n" +
        "printf 'beta primary work\\n' > " + PrimaryAlias + "/agent_beta.txt\n" +
        "printf 'the beta side of the change\\n' > " + RelatedAlias + "/" + SharedFile + "\n" +
        "printf '{\"type\":\"agent_message\",\"message\":\"DONE: %s\"}\\n' \"$esc\"\n" +
        "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "exit 0\n";
}
