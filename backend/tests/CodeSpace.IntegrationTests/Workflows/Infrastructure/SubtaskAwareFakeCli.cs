using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A fake Codex CLI whose output is DERIVED FROM ITS PER-BRANCH INPUT — the piece the headline-flow E2E needs
/// that the static-fixture <c>FakeCli</c> in <c>RealHarnessExecutionTests</c> can't give. The REAL
/// <c>AgentRunExecutor</c> drives the REAL <c>LocalProcessRunner</c>, which spawns THIS script as the agent
/// process (the harness's <see cref="CodexHarness.CommandEnvVar"/> points at it). Each map branch passes a
/// DIFFERENT goal (<c>"Work on alpha"</c> / <c>"Work on beta"</c> / …) as Codex's last positional arg; the
/// script echoes a <c>codex exec --json</c>-shaped event stream whose final <c>agent_message</c> is
/// <c>"DONE: &lt;goal&gt;"</c>, so the executor's real <c>ParseEvent</c> + <c>BuildResult</c> fold a per-branch
/// <see cref="Messages.Agents.AgentRunResult.Summary"/> the synthesizer composes.
///
/// <para><b>One env var, three branches.</b> <see cref="CodexHarness.CommandEnvVar"/> is process-wide, so all
/// branches share this one script — the per-branch differentiation rides the GOAL ARG the harness passes, not
/// env (which can't differ per branch). That is exactly the production seam: a CLI's behaviour keys off the
/// prompt it's handed.</para>
///
/// <para><b>Rule 12.5 drift guard.</b> The emitted JSONL mirrors the documented <c>codex exec --json</c> event
/// shapes (<c>agent_reasoning</c> → Reasoning, <c>agent_message</c> → AssistantMessage, <c>task_complete</c> →
/// Completed) — the SAME shapes the committed <c>RealHarnessExecutionTests.CodexFixture</c> mirror uses. A
/// drift-detector test (<c>SubtaskAwareFakeCliDriftTests</c>) pins this script's event types against that
/// canonical mirror so a divergence fails loudly rather than silently passing a stale shape.</para>
/// </summary>
public sealed class SubtaskAwareFakeCli : IDisposable
{
    /// <summary>The native codex event types this fake emits, in order — the contract the Rule-12.5 drift detector pins against the canonical mirror.</summary>
    public static readonly IReadOnlyList<string> EmittedEventTypes = new[] { "agent_reasoning", "agent_message", "task_complete" };

    /// <summary>The summary prefix the script stamps in front of the per-branch goal — the deterministic transform the synthesizer's composition is asserted against.</summary>
    public const string SummaryPrefix = "DONE: ";

    private readonly string _originalCommand;
    private readonly string _dir;

    public SubtaskAwareFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-subtask-fakecli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var script = Path.Combine(_dir, "fake-codex.sh");
        File.WriteAllText(script, ScriptBody);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _originalCommand = Environment.GetEnvironmentVariable(CodexHarness.CommandEnvVar) ?? "";
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, script);
    }

    /// <summary>The deterministic summary the executor's BuildResult folds for a given branch goal — the value the synthesizer composes per element.</summary>
    public static string ExpectedSummaryFor(string goal) => SummaryPrefix + goal;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, _originalCommand.Length == 0 ? null : _originalCommand);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Strictly-POSIX emitter (the runner spawns this via its <c>#!/bin/sh</c> shebang, which may be dash — no
    /// bashisms). Walk the positional args so <c>$goal</c> ends as the LAST one (Codex puts the prompt last in
    /// BuildInvocation), JSON-escape its quotes/backslashes, and print a three-line codex-shaped JSONL stream
    /// whose final assistant message is <c>"DONE: &lt;goal&gt;"</c>. No env, no network, no codex binary — just
    /// /bin/sh + printf.
    /// </summary>
    private static string ScriptBody =>
        "#!/bin/sh\n" +
        "goal=\"\"\n" +
        "for goal in \"$@\"; do :; done\n" +
        "esc=$(printf '%s' \"$goal\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
        "printf '{\"type\":\"agent_reasoning\",\"message\":\"Planning work for: %s\"}\\n' \"$esc\"\n" +
        "printf '{\"type\":\"agent_message\",\"message\":\"" + SummaryPrefix + "%s\"}\\n' \"$esc\"\n" +
        "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "exit 0\n";
}
