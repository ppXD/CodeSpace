using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Supervisor;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A fake Codex CLI for the conflict→resolve whole-loop E2E. Two parallel agents EDIT THE SAME file (<see cref="SharedFile"/>)
/// with DIFFERENT content, so their real patches conflict in a real git merge; the supervisor's <c>resolve</c> turn then
/// runs a third (resolver) agent — recognised by the deterministic recipe text the executor hands it — which writes a
/// single reconciled version and ends its summary with the <see cref="SupervisorResolverRecipe.TestsPassedMarker"/> token
/// so the resolution grades Verified. Behaviour is a pure function of the GOAL (no external state) → bwrap-safe.
///
/// <para>The base repo must seed <see cref="SharedFile"/> so each agent's edit is a real diff against a common base
/// (that's what makes the two edits conflict). POSIX <c>/bin/sh</c> only; writes only into the run's own workspace.</para>
/// </summary>
public sealed class ConflictThenResolveFakeCli : IDisposable
{
    /// <summary>The single file both parallel agents edit (so their patches conflict) and the resolver reconciles. Must be seeded in the base repo.</summary>
    public const string SharedFile = "shared.txt";

    /// <summary>The substring of the resolver recipe instruction the CLI keys on to recognise "I am the resolver".</summary>
    public const string ResolverMarker = "Reconcile these branches";

    /// <summary>An instruction containing this marker takes the FIRST (alpha) side of the conflict; anything else (and not the resolver) takes the beta side.</summary>
    public const string AlphaMarker = "alpha";

    private readonly string _originalCommand;
    private readonly string _dir;

    public ConflictThenResolveFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-conflictresolve-fakecli-" + Guid.NewGuid().ToString("N"));
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
    /// Resolve the goal (Codex's last positional arg). The RESOLVER (goal carries <see cref="ResolverMarker"/>) writes one
    /// reconciled <see cref="SharedFile"/> + ends its summary with the verified token. The two parallel agents write
    /// DIFFERENT content to <see cref="SharedFile"/> (alpha vs beta) so their diffs against the common base conflict.
    /// </summary>
    private static string ScriptBody =>
        "#!/bin/sh\n" +
        "goal=\"\"\n" +
        "for goal in \"$@\"; do :; done\n" +
        "esc=$(printf '%s' \"$goal\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
        "case \"$goal\" in\n" +
        "  *\"" + ResolverMarker + "\"*)\n" +
        "    printf 'reconciled: alpha + beta\\n' > " + SharedFile + "\n" +
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
