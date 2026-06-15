using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>
/// A fake Codex CLI whose output is derived from its goal arg — the same honest binary-boundary fake the
/// integration suite's <c>SubtaskAwareFakeCli</c> uses, inlined here (the E2E project doesn't reference the
/// integration test assembly). The REAL <c>AgentRunExecutor</c> + <c>LocalProcessRunner</c> spawn THIS script as
/// the agent process (<see cref="CodexHarness.CommandEnvVar"/> points at it); the script echoes a
/// <c>codex exec --json</c>-shaped event stream whose final <c>agent_message</c> is <c>"DONE: &lt;goal&gt;"</c>,
/// so the executor's real ParseEvent + BuildResult fold a summary the run surfaces. Only the CLI's intelligence
/// is faked — the executor / runner / harness driving it are all real (Rule 12 high fidelity).
/// </summary>
public sealed class FakeCodexCli : IDisposable
{
    public const string SummaryPrefix = "DONE: ";

    private readonly string _originalCommand;
    private readonly string _dir;

    public FakeCodexCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-e2e-fakecli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var script = Path.Combine(_dir, "fake-codex.sh");
        File.WriteAllText(script, ScriptBody);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _originalCommand = Environment.GetEnvironmentVariable(CodexHarness.CommandEnvVar) ?? "";
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, script);
    }

    /// <summary>The deterministic summary the executor folds for a given goal — the value the run surfaces.</summary>
    public static string ExpectedSummaryFor(string goal) => SummaryPrefix + goal;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, _originalCommand.Length == 0 ? null : _originalCommand);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Strictly-POSIX emitter (the runner spawns this via its <c>#!/bin/sh</c> shebang — no bashisms): take the LAST positional arg as the goal (Codex puts the prompt last), JSON-escape it, print a three-line codex-shaped JSONL stream whose final assistant message is <c>"DONE: &lt;goal&gt;"</c>.</summary>
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
