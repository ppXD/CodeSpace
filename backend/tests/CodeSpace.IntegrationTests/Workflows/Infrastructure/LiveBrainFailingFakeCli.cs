using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A fake Codex CLI for the LIVE-BRAIN failure→retry whole-loop (real-scenario coverage A2). Unlike
/// <see cref="FailFirstThenSucceedFakeCli"/> — which keys fail-vs-succeed on the literal "beta"/"retry" markers the
/// SCRIPTED decider plants in its instructions — this CLI FAILS EVERY agent unconditionally (exit 1, writes no file → a
/// real <c>Failed</c> agent run with no patch), regardless of the free-form subtask instructions a LIVE model authors.
///
/// <para>That is the only way to deterministically present a real failure to a live brain: a retry stages a fresh agent
/// with a BRAIN-authored revised instruction (no CLI-visible attempt marker), so a "fail first, succeed on retry" CLI
/// can't be keyed without the model's cooperation. With every attempt failing, the brain sees its spawned (and any
/// retried) subtasks fail in its own <c>SupervisorOutcome</c> context and must REACT — author a <c>retry</c> (the
/// recovery action the golden eval scores), or escalate via stop / ask_human — and must NEVER merge over the failure
/// into a falsely-accepted head. The live lane measures which it does; this CLI just guarantees the failure is real.</para>
///
/// <para>Behaviour is a pure function of the GOAL (none — it always fails) → bwrap-safe. POSIX <c>/bin/sh</c> only;
/// writes nothing outside the run's own workspace.</para>
/// </summary>
public sealed class LiveBrainFailingFakeCli : IDisposable
{
    private readonly string _originalCommand;
    private readonly string _dir;

    public LiveBrainFailingFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-livebrainfailing-fakecli-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>EVERY invocation fails: emit an error-flavoured message + exit 1 (a real <c>Failed</c> run, no file/patch), regardless of the goal — so a live brain reliably sees a failed subtask to react to.</summary>
    private static string ScriptBody =>
        "#!/bin/sh\n" +
        "goal=\"\"\n" +
        "for goal in \"$@\"; do :; done\n" +
        "esc=$(printf '%s' \"$goal\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
        "printf '{\"type\":\"agent_message\",\"message\":\"could not complete the task — the build is broken for: %s\"}\\n' \"$esc\"\n" +
        "printf '{\"type\":\"task_complete\",\"message\":\"failed\"}\\n'\n" +
        "exit 1\n";
}
