using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A fake agent CLI that does the two things a secret-taint test needs from a real agent: it RECORDS the exact goal it
/// was launched with (appended to <see cref="CapturedGoals"/>, a file outside the database), and it ECHOES that goal
/// into every stream the executor persists — a reasoning line, the assistant message the result folds into its
/// summary, and the closing completion. So a test can prove both halves at once: the agent was handed the real secret,
/// and nothing the run stored kept it.
///
/// <para>The REAL <c>AgentRunExecutor</c> + <c>LocalProcessRunner</c> spawn this script (both harness command env vars
/// point at it, and it serves both dialects via <see cref="FakeAgentCliDialect"/>), so only the CLI's intelligence is
/// faked. Codex event shapes mirror <see cref="SubtaskAwareFakeCli"/>, whose drift detector pins them.</para>
/// </summary>
public sealed class GoalCapturingFakeCli : IDisposable
{
    /// <summary>The prefix of the assistant message the script prints in front of the goal.</summary>
    public const string EchoPrefix = "I will call the API with ";

    private readonly string _originalCommand;
    private readonly string _originalClaudeCommand;
    private readonly string _dir;

    public GoalCapturingFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-goalcapture-fakecli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var script = Path.Combine(_dir, "fake-agent.sh");
        File.WriteAllText(script, ScriptBody(CapturedGoalsPath));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _originalCommand = Environment.GetEnvironmentVariable(CodexHarness.CommandEnvVar) ?? "";
        _originalClaudeCommand = Environment.GetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar) ?? "";
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, script);
        Environment.SetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar, script);
    }

    private string CapturedGoalsPath => Path.Combine(_dir, "goals.txt");

    /// <summary>Every goal an agent was launched with, in launch order — read from the file the script appended to, never from the database.</summary>
    public string CapturedGoals => File.Exists(CapturedGoalsPath) ? File.ReadAllText(CapturedGoalsPath) : "";

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, _originalCommand.Length == 0 ? null : _originalCommand);
        Environment.SetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar, _originalClaudeCommand.Length == 0 ? null : _originalClaudeCommand);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Strictly POSIX: read the goal, append it to the capture file, JSON-escape it, and print a stream whose every event repeats it.</summary>
    private static string ScriptBody(string capturePath) =>
        "#!/bin/sh\n" +
        FakeAgentCliDialect.ReadGoal +
        "printf '%s\\n' \"$goal\" >> '" + capturePath + "'\n" +
        "esc=$(printf '%s' \"$goal\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
        FakeAgentCliDialect.Dialects(
            "printf '{\"type\":\"agent_reasoning\",\"message\":\"Planning work for: %s\"}\\n' \"$esc\"\n" +
            "printf '{\"type\":\"agent_message\",\"message\":\"" + EchoPrefix + "%s\"}\\n' \"$esc\"\n" +
            "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n",
            EchoPrefix + "%s") +
        "exit 0\n";
}
