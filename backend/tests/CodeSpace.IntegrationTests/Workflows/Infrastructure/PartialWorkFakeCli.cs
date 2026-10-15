using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A fake agent CLI — serving BOTH registered harnesses — for the subtask that EDITS FILES and then DIES: every agent
/// writes its goal-slugged file into its cloned workspace exactly as <see cref="FileWritingFakeCli"/> does (so the
/// executor's real git-diff captures a real patch), but an instruction carrying <see cref="CrashMarker"/> then reports
/// failure and exits non-zero instead of emitting its done event. The result is a REAL <c>Failed</c> agent run that
/// nevertheless captured a diff — the shape of an agent that crashed or timed out after editing — which is what the
/// integrate step must still name when it lands that partial, unverified work on the candidate.
///
/// <para>Behaviour is a pure function of the GOAL on stdin (where both harnesses hand the prompt) → bwrap-safe. POSIX
/// <c>/bin/sh</c> only; writes only into the run's own workspace. The file name is the one
/// <see cref="FileWritingFakeCli.FileFor"/> derives, so a test asserts on it the same way. The stream half is served in
/// the dialect of whichever harness invoked the script (<see cref="FakeAgentCliDialect"/>).</para>
/// </summary>
public sealed class PartialWorkFakeCli : IDisposable
{
    /// <summary>An instruction containing this marker makes the agent write its file and then fail.</summary>
    public const string CrashMarker = "crash";

    /// <summary>The message a crashing agent reports before it exits — the summary BOTH dialects fold.</summary>
    public const string FailureMessageFormat = "process died after editing: %s";

    private readonly string _originalCommand;
    private readonly string _originalClaudeCommand;
    private readonly string _dir;

    public PartialWorkFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-partialwork-fakecli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var script = Path.Combine(_dir, "fake-agent.sh");
        File.WriteAllText(script, ScriptBody);
        if (!OperatingSystem.IsWindows())   // POSIX-only: the runner spawns the script by its shebang, and every test arming this fake returns early on Windows
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
    /// Read <c>$goal</c> from stdin, write its slugged file (the work half — <c>exit 90</c> when the write itself fails, so
    /// a filename overflow reds loudly instead of lying, as in <see cref="FileWritingFakeCli"/>), then branch on the
    /// goal: a goal carrying <see cref="CrashMarker"/> reports <see cref="FailureMessageFormat"/> and exits 1; any other
    /// goal emits its done event and exits 0.
    /// </summary>
    internal static string ScriptBody =>
        "#!/bin/sh\n" +
        "goal=\"$(cat)\"\n" +
        "esc=$(printf '%s' \"$goal\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
        "fname=$(printf '%s' \"$goal\" | tr -c 'A-Za-z0-9' '_' | cut -c1-100)\n" +
        "printf 'work by the agent for: %s\\n' \"$goal\" > \"" + FileWritingFakeCli.FilePrefix + "${fname}.txt\" || exit 90\n" +
        "case \"$goal\" in\n" +
        "  *" + CrashMarker + "*)\n" +
        FakeAgentCliDialect.Dialects(
            "printf '{\"type\":\"agent_message\",\"message\":\"" + FailureMessageFormat + "\"}\\n' \"$esc\"\n" +
            "printf '{\"type\":\"task_complete\",\"message\":\"failed\"}\\n'\n",
            FailureMessageFormat, isError: true) +
        "    exit 1\n" +
        "    ;;\n" +
        "esac\n" +
        FakeAgentCliDialect.Dialects(
            "printf '{\"type\":\"agent_reasoning\",\"message\":\"Editing for: %s\"}\\n' \"$esc\"\n" +
            "printf '{\"type\":\"agent_message\",\"message\":\"" + FileWritingFakeCli.SummaryPrefix + "%s\"}\\n' \"$esc\"\n" +
            "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n",
            FileWritingFakeCli.SummaryPrefix + "%s") +
        "exit 0\n";
}
