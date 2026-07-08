using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A fake Codex CLI for the LIVE-BRAIN S2 read-only-acceptance whole-loop: EVERY invocation SUCCEEDS (exit 0) but
/// writes NO file — a real <c>Succeeded</c> agent run with no patch, no changed files, no produced branch —
/// mirroring a genuine investigate-only agent that inspects the repo and reports findings without touching it.
/// Unlike <see cref="LiveBrainFailingFakeCli"/> (unconditional failure) this is unconditional SUCCESS-with-no-diff,
/// the exact shape <see cref="AgentAcceptanceContract"/>'s S2 "no branch, no patch" fallback must resolve to
/// NOT-APPLICABLE (never fail-closed) when the subtask genuinely expected no changes.
///
/// <para>Behaviour is a pure function of the GOAL (none — it always succeeds with no diff) → bwrap-safe. POSIX
/// <c>/bin/sh</c> only; writes nothing outside the run's own workspace (in fact writes nothing at all).</para>
/// </summary>
public sealed class InvestigateOnlyFakeCli : IDisposable
{
    private readonly string _originalCommand;
    private readonly string _dir;

    public InvestigateOnlyFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-investigateonly-fakecli-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>EVERY invocation succeeds without writing anything: emit a findings-flavoured message + exit 0 (a real <c>Succeeded</c> run, no file/patch/branch), regardless of the goal.</summary>
    private static string ScriptBody =>
        "#!/bin/sh\n" +
        "goal=\"\"\n" +
        "for goal in \"$@\"; do :; done\n" +
        "esc=$(printf '%s' \"$goal\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
        "printf '{\"type\":\"agent_reasoning\",\"message\":\"Investigating: %s\"}\\n' \"$esc\"\n" +
        "printf '{\"type\":\"agent_message\",\"message\":\"Findings for: %s — no code changes were necessary.\"}\\n' \"$esc\"\n" +
        "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "exit 0\n";
}
