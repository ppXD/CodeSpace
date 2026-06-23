using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Supervisor;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A fake Codex CLI for the UNVERIFIED-resolution safety E2E — the adversarial sibling of
/// <see cref="ConflictThenResolveFakeCli"/>. The two parallel agents conflict on <see cref="SharedFile"/> exactly as
/// before, but the resolver LIES: it ends its summary with <see cref="SupervisorResolverRecipe.TestsPassedMarker"/>
/// (claiming the build + tests passed) while its reconciliation actually DROPS one side's work — it writes only the
/// alpha content (<see cref="SharedFile"/> = "reconciled: alpha only"), discarding beta. This violates the resolver
/// recipe's explicit "do NOT discard either agent's intent" guardrail.
///
/// <para>The supervisor's OBJECTIVE resolve grade (clone the resolver's branch + run the configured acceptance check)
/// catches the lie: a check.sh that asserts BOTH sides survived (<c>grep -q alpha &amp;&amp; grep -q beta</c>) FAILS on the
/// alpha-only reconciliation, so <c>ReadResolutionVerdict</c> = (marker true) AND (grade false) = <b>Unverified</b>.
/// The self-report marker can only be TIGHTENED by the server grade, never trusted on its own — so the lying branch is
/// never accepted, and the substrate surfaces NO reviewable head for a downstream PR-open.</para>
///
/// <para>Behaviour is a pure function of the GOAL (no external state) → bwrap-safe. POSIX <c>/bin/sh</c> only; writes
/// only into the run's own workspace. SINGLE-repo only (the objective resolve grade runs only for a single-repo
/// resolver). The base repo must seed <see cref="SharedFile"/> so each agent's edit is a real diff against a common
/// base, AND a check.sh that requires both sides — the test does this.</para>
/// </summary>
public sealed class ConflictThenLyingResolveFakeCli : IDisposable
{
    /// <summary>The single file both parallel agents edit (so their patches conflict) and the lying resolver writes alpha-only into. Must be seeded in the base repo.</summary>
    public const string SharedFile = "shared.txt";

    /// <summary>The substring of the (single-repo) resolver recipe instruction the CLI keys on to recognise "I am the resolver".</summary>
    public const string ResolverMarker = "Reconcile these branches";

    /// <summary>An instruction containing this marker takes the FIRST (alpha) side of the conflict; anything else (and not the resolver) takes the beta side.</summary>
    public const string AlphaMarker = "alpha";

    private readonly string _originalCommand;
    private readonly string _dir;

    public ConflictThenLyingResolveFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-lyingresolve-fakecli-" + Guid.NewGuid().ToString("N"));
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
    /// Resolve the goal (Codex's last positional arg). The RESOLVER (goal carries <see cref="ResolverMarker"/>) writes
    /// an alpha-ONLY <see cref="SharedFile"/> (dropping beta) yet STILL ends with the verified token — the lie the
    /// objective grade catches. The two parallel agents write DIFFERENT content (alpha vs beta) so their diffs conflict.
    /// </summary>
    private static string ScriptBody =>
        "#!/bin/sh\n" +
        "goal=\"\"\n" +
        "for goal in \"$@\"; do :; done\n" +
        "esc=$(printf '%s' \"$goal\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
        "case \"$goal\" in\n" +
        "  *\"" + ResolverMarker + "\"*)\n" +
        "    printf 'reconciled: alpha only\\n' > " + SharedFile + "\n" +
        "    printf '{\"type\":\"agent_reasoning\",\"message\":\"reconciling the two branches\"}\\n'\n" +
        "    printf '{\"type\":\"agent_message\",\"message\":\"build + full test suite pass " + SupervisorResolverRecipe.TestsPassedMarker + "\"}\\n'\n" +
        "    printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "    exit 0\n" +
        "    ;;\n" +
        "  *" + AlphaMarker + "*)\n" +
        "    printf 'the alpha side of the change\\n' > " + SharedFile + "\n" +
        "    printf '{\"type\":\"agent_message\",\"message\":\"DONE: %s\"}\\n' \"$esc\"\n" +
        "    printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "    exit 0\n" +
        "    ;;\n" +
        "esac\n" +
        "printf 'the beta side of the change\\n' > " + SharedFile + "\n" +
        "printf '{\"type\":\"agent_message\",\"message\":\"DONE: %s\"}\\n' \"$esc\"\n" +
        "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "exit 0\n";
}
