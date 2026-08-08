using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A fake agent CLI — serving BOTH registered harnesses — that ACTUALLY EDITS A FILE in its workspace before
/// emitting the done event — the piece the
/// whole-loop supervisor E2E needs that <see cref="SubtaskAwareFakeCli"/> (stdout-only) can't give. The REAL
/// <c>AgentRunExecutor</c> runs this through the REAL <c>LocalProcessRunner</c> with the process cwd set to the
/// agent's cloned workspace (<c>SandboxSpec.WorkingDirectory</c>), so a file this script writes lands in the clone
/// and the executor's real <c>EnrichWithWorkspaceChangesAsync</c> git-diff captures it as a real
/// <see cref="Messages.Agents.AgentRunResult.Patch"/> + produced branch — the same capture path
/// <c>AgentWorkspacePushFlowTests</c> proves. That real patch is what the supervisor's MERGE turn integrates and the
/// objective acceptance gate grades.
///
/// <para><b>Distinct file per goal.</b> Each spawned agent receives a DIFFERENT goal as Codex's last positional arg
/// (<c>"do alpha"</c> / <c>"do beta"</c>). The script derives a filesystem-safe name from that goal
/// (<c>agent_do_alpha.txt</c> / <c>agent_do_beta.txt</c>), so two agents edit DISJOINT files and their patches
/// integrate cleanly (no spurious conflict). The same one env-var script serves every branch — the per-branch
/// differentiation rides the goal arg, exactly as production keys a CLI's behaviour off the prompt it's handed.</para>
///
/// <para>POSIX <c>/bin/sh</c> only (the runner spawns it via the shebang; may be dash — no bashisms). No env, no
/// network, no codex binary — just <c>/bin/sh</c> + <c>printf</c> + a single file write.</para>
/// </summary>
public sealed class FileWritingFakeCli : IDisposable
{
    /// <summary>The native codex event types this fake emits, in order — mirrors <see cref="SubtaskAwareFakeCli.EmittedEventTypes"/> so the real ParseEvent folds Reasoning/AssistantMessage/Completed.</summary>
    public static readonly IReadOnlyList<string> EmittedEventTypes = new[] { "agent_reasoning", "agent_message", "task_complete" };

    /// <summary>The summary prefix stamped in front of the per-branch goal (mirrors <see cref="SubtaskAwareFakeCli.SummaryPrefix"/>).</summary>
    public const string SummaryPrefix = "DONE: ";

    /// <summary>The filename prefix the script writes (so a test can assert the produced patch touched the agent's file).</summary>
    public const string FilePrefix = "agent_";

    private readonly string _originalCommand;
    private readonly string _originalClaudeCommand;
    private readonly string _dir;

    /// <summary>
    /// The harness kinds this fake actually arms — the single source of truth an evaluator checks the run against
    /// (RealModelGate.ClassifyHarnessControl). BOTH: which harness a live-brain run dispatches is the MODEL's
    /// choice, and an Anthropic-defaulted team pool reconciles to claude-code on its own (HarnessModelReconciler) —
    /// a codex-only stub left those agents running the REAL claude CLI, and the whole-loop arm red honestly as
    /// "lost control" the moment #1299's classifier landed (run 31170717869).
    /// </summary>
    public static readonly IReadOnlyList<string> StubbedHarnessKinds = new[] { "codex-cli", "claude-code" };

    public FileWritingFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-filewriting-fakecli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var script = Path.Combine(_dir, "fake-agent.sh");
        File.WriteAllText(script, ScriptBody);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _originalCommand = Environment.GetEnvironmentVariable(CodexHarness.CommandEnvVar) ?? "";
        _originalClaudeCommand = Environment.GetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar) ?? "";
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, script);
        Environment.SetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar, script);
    }

    /// <summary>The deterministic summary the executor's BuildResult folds for a given branch goal.</summary>
    public static string ExpectedSummaryFor(string goal) => SummaryPrefix + goal;

    /// <summary>
    /// The file the script writes for a given goal — the change the captured patch must contain. Mirrors the
    /// script's <c>tr -c 'A-Za-z0-9' '_' | cut -c1-100</c> slug: TRUNCATED because a MODEL-authored goal is a
    /// multi-sentence instruction whose full slug exceeds the 255-byte filename limit — the write then failed
    /// silently while the script still exited 0, so every such agent reported Succeeded with ZERO captured files
    /// (the run-31200742534 "37 succeeded agents, nothing captured" disease). The script now also exits 90 on a
    /// failed write, so any future overflow reds loudly instead of lying. A scripted short goal ("do alpha") is
    /// byte-identical to before; distinct model goals differ inside their first 100 slug chars in practice.
    /// </summary>
    public static string FileFor(string goal)
    {
        var slug = new string(goal.Select(c => char.IsLetterOrDigit(c) ? c : '_').Take(100).ToArray());
        return $"{FilePrefix}{slug}.txt";
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, _originalCommand.Length == 0 ? null : _originalCommand);
        Environment.SetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar, _originalClaudeCommand.Length == 0 ? null : _originalClaudeCommand);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Walk the positional args so <c>$goal</c> ends as the LAST one (BOTH harnesses put the prompt last —
    /// <c>CodexHarness.BuildInvocation</c> and <c>ClaudeCodeHarness.BuildInvocation</c> alike), derive a
    /// filesystem-safe filename from it (non-alphanumerics → <c>_</c>), WRITE that file into the cwd (the workspace
    /// clone) — the WORK half is dialect-free. Only the STREAM half branches, on the one discriminator that is a
    /// property of the invocation: Codex's argv always starts with <c>exec</c>, Claude's with <c>--print</c>. The
    /// codex branch is BYTE-IDENTICAL to the pre-dual-stub script, so every codex consumer (incl. the Rule-12.5
    /// drift pins) is unperturbed. The claude <c>result</c> line's <c>result</c> property carries the SAME
    /// <c>"DONE: &lt;goal&gt;"</c> as the codex <c>agent_message</c> — Claude folds <c>FinalSummary ?? Completed ??
    /// AssistantMessage</c> while Codex SKIPS Completed, so echoing the summary there (never a literal "completed")
    /// is what keeps the two dialects folding the same Summary the whole-loop assertions read
    /// (<see cref="ExpectedSummaryFor"/>). Pinned parse-level by the drift tests.
    /// </summary>
    internal static string ScriptBody =>
        "#!/bin/sh\n" +
        "goal=\"\"\n" +
        "for goal in \"$@\"; do :; done\n" +
        "esc=$(printf '%s' \"$goal\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
        "fname=$(printf '%s' \"$goal\" | tr -c 'A-Za-z0-9' '_' | cut -c1-100)\n" +
        "printf 'work by the agent for: %s\\n' \"$goal\" > \"" + FilePrefix + "${fname}.txt\" || exit 90\n" +
        "if [ \"$1\" = 'exec' ]; then\n" +
        "  printf '{\"type\":\"agent_reasoning\",\"message\":\"Editing for: %s\"}\\n' \"$esc\"\n" +
        "  printf '{\"type\":\"agent_message\",\"message\":\"" + SummaryPrefix + "%s\"}\\n' \"$esc\"\n" +
        "  printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "else\n" +
        "  printf '{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + SummaryPrefix + "%s\"}]}}\\n' \"$esc\"\n" +
        "  printf '{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"" + SummaryPrefix + "%s\",\"is_error\":false}\\n' \"$esc\"\n" +
        "fi\n" +
        "exit 0\n";
}
