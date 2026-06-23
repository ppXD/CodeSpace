using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Supervisor;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A fake Codex CLI for the MULTI-repo UNVERIFIED-resolution safety E2E — the adversarial sibling of
/// <see cref="MultiRepoConflictFakeCli"/>. It stages the SAME "primary clean, related conflicts" shape, but the
/// resolver LIES: in the conflicted related repo it writes only the alpha content (dropping beta) while STILL emitting
/// <see cref="SupervisorResolverRecipe.TestsPassedMarker"/>, claiming the build + tests passed.
///
/// <para>This exercises a DEFENCE-IN-DEPTH path the single-repo <see cref="ConflictThenLyingResolveFakeCli"/> cannot:
/// the supervisor's per-repo resolve verdict is currently MARKER-ONLY for a multi-repo resolver (the objective resolve
/// grade is single-repo only), so this lie is graded Verified AT THE RESOLVE STEP and its branch is surfaced as a head.
/// The TERMINAL STOP is the backstop — it objectively grades EVERY per-repo head (clone + run check.sh) and, when the
/// related repo's head fails (its <see cref="RelatedAlias"/>/<see cref="SharedFile"/> dropped beta), withholds ALL
/// per-repo branches from the node output, so the lie never reaches a downstream PR-open.</para>
///
/// <para>Behaviour is a pure function of the GOAL (no external state) → bwrap-safe. POSIX <c>/bin/sh</c> only; writes
/// only into the run's own multi-repo workspace (cwd = workspace root, each repo at <c>&lt;root&gt;/&lt;alias&gt;/</c>).</para>
/// </summary>
public sealed class MultiRepoLyingResolveFakeCli : IDisposable
{
    /// <summary>The primary repo's alias → its clone subdirectory. Both agents add a disjoint file here so it integrates cleanly + passes its own acceptance.</summary>
    public const string PrimaryAlias = "repo";

    /// <summary>The related repo's alias → its clone subdirectory. The test MUST author the related repo with this alias. Both agents conflict here, and the lying resolver drops one side here.</summary>
    public const string RelatedAlias = "api";

    /// <summary>The single file both parallel agents edit in the RELATED repo (so their patches conflict) and the lying resolver writes alpha-only into.</summary>
    public const string SharedFile = "shared.txt";

    /// <summary>The alias-independent substring the MULTI-repo resolver recipe emits — the CLI keys on it to recognise "I am the multi-repo resolver".</summary>
    public const string ResolverMarker = "across multiple repositories";

    /// <summary>A spawn instruction containing this marker takes the FIRST (alpha) side of the related-repo conflict; anything else (and not the resolver) takes the beta side.</summary>
    public const string AlphaMarker = "alpha";

    private readonly string _originalCommand;
    private readonly string _dir;

    public MultiRepoLyingResolveFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-multirepolying-fakecli-" + Guid.NewGuid().ToString("N"));
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
    /// Resolve the goal PER SUBDIRECTORY. The RESOLVER (goal carries <see cref="ResolverMarker"/>) writes an alpha-ONLY
    /// <see cref="RelatedAlias"/>/<see cref="SharedFile"/> (dropping beta) yet STILL ends with the verified token — the
    /// lie the terminal-stop grade catches. Each spawn agent adds a DISJOINT file in <see cref="PrimaryAlias"/>/ (clean
    /// axis) and writes its side to <see cref="RelatedAlias"/>/<see cref="SharedFile"/> (conflicting axis).
    /// </summary>
    private static string ScriptBody =>
        "#!/bin/sh\n" +
        "goal=\"\"\n" +
        "for goal in \"$@\"; do :; done\n" +
        "esc=$(printf '%s' \"$goal\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
        "case \"$goal\" in\n" +
        "  *\"" + ResolverMarker + "\"*)\n" +
        "    printf 'reconciled: alpha only\\n' > " + RelatedAlias + "/" + SharedFile + "\n" +
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
