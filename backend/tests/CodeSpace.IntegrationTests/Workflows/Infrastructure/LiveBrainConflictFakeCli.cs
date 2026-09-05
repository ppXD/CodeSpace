using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Supervisor;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A fake Codex CLI for the LIVE-BRAIN conflict→resolve whole-loop (real-scenario coverage A1). Unlike
/// <see cref="ConflictThenResolveFakeCli"/> — which keys the two conflicting sides on the literal subtask instructions
/// "alpha"/"beta" the SCRIPTED decider emits — this CLI is keyed only on signals a LIVE model can't avoid producing, so
/// it works no matter what free-form subtask instructions a real supervisor brain authors:
/// <list type="bullet">
///   <item>EVERY spawn agent writes its OWN (brain-authored) goal text into the single shared <see cref="SharedFile"/>.
///         Two distinct subtasks therefore write DIFFERENT content to the same file → their real git diffs against the
///         common base genuinely CONFLICT, with no dependence on the brain choosing any particular wording.</item>
///   <item>The RESOLVER is recognised by <see cref="ResolverMarker"/> — a phrase the deterministic
///         <see cref="SupervisorResolverRecipe"/> (NOT the model) bakes into the resolver agent's goal — so the resolver
///         arm fires correctly even though the model never authored that text. It writes one reconciled
///         <see cref="SharedFile"/> and ends with <see cref="SupervisorResolverRecipe.TestsPassedMarker"/> so the
///         resolution grades Verified.</item>
/// </list>
///
/// <para>Behaviour is a pure function of the GOAL (no external state) → bwrap-safe. POSIX <c>/bin/sh</c> only; writes
/// only into the run's own workspace. The base repo must seed <see cref="SharedFile"/> so each agent's edit is a real
/// diff against a common base.</para>
/// </summary>
public sealed class LiveBrainConflictFakeCli : IDisposable
{
    /// <summary>The single file every spawn agent overwrites (with its own goal text) — so two distinct brain-authored subtasks conflict — and the resolver reconciles. Must be seeded in the base repo.</summary>
    public const string SharedFile = "shared.txt";

    /// <summary>The substring the SINGLE-repo resolver recipe (<see cref="SupervisorResolverRecipe.BuildInstruction"/>) emits — engine-authored, so the CLI recognises the resolver no matter what the model wrote for the spawn subtasks.</summary>
    public const string ResolverMarker = "Reconcile these branches";

    /// <summary>The summary prefix a SPAWN agent's stream folds to — the same in both dialects.</summary>
    public const string SummaryPrefix = "DONE: ";

    private readonly string _originalCommand;
    private readonly string _originalClaudeCommand;
    private readonly string _dir;

    public LiveBrainConflictFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-livebrainconflict-fakecli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var script = Path.Combine(_dir, "fake-codex.sh");
        File.WriteAllText(script, ScriptBody);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _originalCommand = Environment.GetEnvironmentVariable(CodexHarness.CommandEnvVar) ?? "";
        _originalClaudeCommand = Environment.GetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar) ?? "";
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, script);
        Environment.SetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar, script);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, _originalCommand.Length == 0 ? null : _originalCommand);
        Environment.SetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar, _originalClaudeCommand.Length == 0 ? null : _originalClaudeCommand);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Resolve the goal (Codex's last positional arg). The RESOLVER (goal carries <see cref="ResolverMarker"/>) writes one
    /// reconciled <see cref="SharedFile"/> + ends with the verified token. EVERY other (spawn) agent writes its OWN goal
    /// text into <see cref="SharedFile"/> — two distinct brain-authored subtasks therefore conflict against the base.
    /// </summary>
    internal static string ScriptBody =>
        "#!/bin/sh\n" +
        "goal=\"\"\n" +
        "for goal in \"$@\"; do :; done\n" +
        "esc=$(printf '%s' \"$goal\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
        "case \"$goal\" in\n" +
        "  *\"" + ResolverMarker + "\"*)\n" +
        "    printf 'reconciled by the resolver\\n' > " + SharedFile + "\n" +
        // No conversion specification in the resolver's summary → NO printf args (POSIX leaves a conversion-free
        // format handed arguments UNSPECIFIED), so the verified marker survives verbatim in both dialects.
        FakeAgentCliDialect.Dialects(
            "    printf '{\"type\":\"agent_reasoning\",\"message\":\"reconciling the two branches\"}\\n'\n" +
            "    printf '{\"type\":\"agent_message\",\"message\":\"" + ResolvedSummary + "\"}\\n'\n" +
            "    printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n",
            ResolvedSummary, printfArgs: "") +
        "    exit 0\n" +
        "    ;;\n" +
        "esac\n" +
        "printf '%s\\n' \"$goal\" > " + SharedFile + "\n" +
        FakeAgentCliDialect.Dialects(
            "printf '{\"type\":\"agent_message\",\"message\":\"" + SummaryPrefix + "%s\"}\\n' \"$esc\"\n" +
            "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n",
            SummaryPrefix + "%s") +
        "exit 0\n";

    /// <summary>The RESOLVER's summary — it must carry <see cref="SupervisorResolverRecipe.TestsPassedMarker"/> verbatim, or the resolution never grades Verified.</summary>
    private static string ResolvedSummary => "build + full test suite pass " + SupervisorResolverRecipe.TestsPassedMarker;
}
