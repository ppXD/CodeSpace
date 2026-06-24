using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A fake agent CLI that writes a CONTROLLED <c>solution.sh</c> into its workspace — unlike <see cref="FileWritingFakeCli"/>
/// (which writes a goal-named marker file so the acceptance check is STRUCTURAL: "did an agent file integrate?"), this
/// lets a whole-loop test seed a real task (a <c>solution.sh</c> stub returning the WRONG value) + a GOAL-RELEVANCE
/// <c>check.sh</c> oracle (<c>[ "$(sh solution.sh 7 5)" = "12" ]</c>), so a green acceptance means the agent's edit
/// actually SOLVED the task (correct output), not merely that some file was produced. Pass <see cref="CorrectSolution"/>
/// to prove a real fix is accepted, or <see cref="WrongSolution"/> to prove the oracle catches a plausible-but-wrong edit
/// (the teeth that distinguishes "解對任務" from "drove the arc"). Emits the same three-line codex JSONL stream as
/// <see cref="FileWritingFakeCli"/> so the real Codex ParseEvent folds it identically.
/// </summary>
public sealed class SolutionWritingFakeCli : IDisposable
{
    /// <summary>The file the agent edits — the seeded task's source. The goal-relevance check.sh runs it.</summary>
    public const string SolutionFile = "solution.sh";

    /// <summary>A CORRECT implementation: prints <c>$1 + $2</c> → <c>sh solution.sh 7 5</c> = <c>12</c> → the oracle passes.</summary>
    public const string CorrectSolution = "#!/bin/sh\necho $(($1 + $2))\n";

    /// <summary>A plausible-but-WRONG implementation (subtracts instead of adds): <c>sh solution.sh 7 5</c> = <c>2</c> ≠ <c>12</c> → the oracle FAILS, proving it grades correctness, not file presence.</summary>
    public const string WrongSolution = "#!/bin/sh\necho $(($1 - $2))\n";

    /// <summary>The goal-relevance acceptance oracle to seed as <c>check.sh</c>: passes iff the integrated <c>solution.sh</c> actually computes A+B (output equality), not iff a file exists.</summary>
    public const string GoalRelevanceCheckSh = "#!/bin/sh\n[ \"$(sh solution.sh 7 5)\" = \"12\" ]\n";

    /// <summary>The WRONG stub to seed as the base <c>solution.sh</c> — the agent must FIX it; absent a fix the oracle fails.</summary>
    public const string SeededStub = "#!/bin/sh\necho 0\n";

    /// <summary>The summary prefix the executor's BuildResult folds (mirrors <see cref="FileWritingFakeCli.SummaryPrefix"/>).</summary>
    public const string SummaryPrefix = "DONE: ";

    private readonly string _originalCommand;
    private readonly string _dir;

    public SolutionWritingFakeCli(string solutionBody)
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-solution-fakecli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var script = Path.Combine(_dir, "fake-codex.sh");
        File.WriteAllText(script, ScriptBody(solutionBody));
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
    /// Overwrite <c>solution.sh</c> in the cwd (the workspace clone) with <paramref name="solutionBody"/> via a
    /// QUOTED heredoc (so the body's <c>$(($1+$2))</c> is written LITERALLY, not expanded at write time), then print
    /// the three-line codex-shaped JSONL stream whose final assistant message is <c>"DONE: &lt;goal&gt;"</c>.
    /// </summary>
    private static string ScriptBody(string solutionBody) =>
        "#!/bin/sh\n" +
        "goal=\"\"\n" +
        "for goal in \"$@\"; do :; done\n" +
        "esc=$(printf '%s' \"$goal\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
        "cat > " + SolutionFile + " <<'CS_SOLUTION_EOF'\n" +
        solutionBody +
        (solutionBody.EndsWith("\n") ? "" : "\n") +
        "CS_SOLUTION_EOF\n" +
        "chmod +x " + SolutionFile + "\n" +
        "printf '{\"type\":\"agent_reasoning\",\"message\":\"Editing " + SolutionFile + " for: %s\"}\\n' \"$esc\"\n" +
        "printf '{\"type\":\"agent_message\",\"message\":\"" + SummaryPrefix + "%s\"}\\n' \"$esc\"\n" +
        "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "exit 0\n";
}
