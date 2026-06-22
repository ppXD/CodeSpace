using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A fake Codex CLI for the failure→retry whole-loop E2E whose success is a pure function of its GOAL — no external
/// state, so it works identically confined (bwrap) or not. A goal containing <see cref="FailMarker"/> (and NOT
/// <see cref="RetryMarker"/>) FAILS: exit 1, writes no file → a real <c>Failed</c> agent run with no patch. A goal
/// containing <see cref="RetryMarker"/> — or any other goal — SUCCEEDS: writes its file + emits the done stream like
/// <see cref="FileWritingFakeCli"/>. So a supervisor that spawns two subtasks (one whose instruction carries the fail
/// marker), sees it fail, and RETRIES it with a revised instruction carrying the retry marker drives a real
/// failure-then-recovery through the real <c>AgentRunExecutor</c> + <c>LocalProcessRunner</c> + git capture — the
/// retry's patch is what the merge integrates and the acceptance gate grades.
///
/// <para>POSIX <c>/bin/sh</c> only. Stateless: the decider drives fail-vs-succeed entirely through the instruction it
/// hands each run (the first spawn's instruction vs the retry's <c>RevisedInstruction</c>), so nothing is written
/// outside the run's own workspace — bwrap-safe.</para>
/// </summary>
public sealed class FailFirstThenSucceedFakeCli : IDisposable
{
    /// <summary>An instruction containing this marker FAILS (unless it also carries <see cref="RetryMarker"/>). The default plan's "do beta" subtask carries it.</summary>
    public const string FailMarker = "beta";

    /// <summary>An instruction containing this marker always SUCCEEDS — the retry's revised instruction carries it.</summary>
    public const string RetryMarker = "retry";

    /// <summary>Mirrors <see cref="FileWritingFakeCli.FilePrefix"/> so the same produced-file assertions apply.</summary>
    public const string FilePrefix = FileWritingFakeCli.FilePrefix;

    public const string SummaryPrefix = "DONE: ";

    private readonly string _originalCommand;
    private readonly string _dir;

    public FailFirstThenSucceedFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-failretry-fakecli-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>The file a SUCCEEDING run writes for a goal — mirrors <see cref="FileWritingFakeCli.FileFor"/>.</summary>
    public static string FileFor(string goal) => FileWritingFakeCli.FileFor(goal);

    /// <summary>
    /// Resolve the goal (Codex's last positional arg). A goal carrying <see cref="FailMarker"/> but NOT
    /// <see cref="RetryMarker"/> emits an error event + exits 1 (a real failed run, no file). Any other goal — incl.
    /// the retry's revised instruction — writes its file + emits the success stream.
    /// </summary>
    private static string ScriptBody =>
        "#!/bin/sh\n" +
        "goal=\"\"\n" +
        "for goal in \"$@\"; do :; done\n" +
        "esc=$(printf '%s' \"$goal\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
        "fname=$(printf '%s' \"$goal\" | tr -c 'A-Za-z0-9' '_')\n" +
        "case \"$goal\" in\n" +
        "  *" + RetryMarker + "*) ;;                                  # a retried run always succeeds (falls through)\n" +
        "  *" + FailMarker + "*)\n" +
        "    printf '{\"type\":\"agent_message\",\"message\":\"first attempt failed for: %s\"}\\n' \"$esc\"\n" +
        "    printf '{\"type\":\"task_complete\",\"message\":\"failed\"}\\n'\n" +
        "    exit 1\n" +
        "    ;;\n" +
        "esac\n" +
        "printf 'work by the agent for: %s\\n' \"$goal\" > \"" + FilePrefix + "${fname}.txt\"\n" +
        "printf '{\"type\":\"agent_reasoning\",\"message\":\"Editing for: %s\"}\\n' \"$esc\"\n" +
        "printf '{\"type\":\"agent_message\",\"message\":\"" + SummaryPrefix + "%s\"}\\n' \"$esc\"\n" +
        "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "exit 0\n";
}
