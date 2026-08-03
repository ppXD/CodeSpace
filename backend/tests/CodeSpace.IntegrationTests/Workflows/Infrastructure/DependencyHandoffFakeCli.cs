using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A fake Codex CLI for the LIVE-BRAIN S1 dependency-handoff whole-loop (re-enacting run 28fec923: a dependent
/// subtask's fresh clone of the repository DEFAULT branch never saw its producer's committed work). Like
/// <see cref="LiveBrainConflictFakeCli"/>, it is keyed only on a signal a live model can't avoid producing — here,
/// OBSERVABLE WORKSPACE STATE rather than goal text: whether <see cref="ProducerMarker"/> is ALREADY PRESENT in the
/// agent's own clone.
///
/// <list type="bullet">
///   <item>The FIRST agent to run (no <see cref="ProducerMarker"/> in its clone yet) writes it, plus an
///         <c>agent_*.txt</c> satisfying the seeded acceptance floor.</item>
///   <item>Any LATER agent (a real dependent, or a homogeneous parallel spawn that simply landed second) whose clone
///         ALREADY CONTAINS <see cref="ProducerMarker"/> writes <see cref="DependentMarker"/> — this can ONLY happen
///         if the agent's workspace was staged from a ref that actually carries the producer's commit (its own
///         pushed branch, or a fresh default-branch clone AFTER the producer's branch was merged into it — which
///         never happens in this test's un-merged setup). So <see cref="DependentMarker"/> existing anywhere in the
///         run's final captured work is the mechanism proof: SOME agent's clone genuinely saw a producer's commit.</item>
/// </list>
///
/// <para>Behaviour is a pure function of the CLONE'S OWN FILESYSTEM STATE (no external state, no goal parsing) →
/// bwrap-safe and independent of the live brain's exact wording. POSIX <c>/bin/sh</c> only.</para>
/// </summary>
public sealed class DependencyHandoffFakeCli : IDisposable
{
    /// <summary>Written by the first agent to run; a later agent's clone carrying this proves it was staged from a ref that includes the producer's commit.</summary>
    public const string ProducerMarker = "step1-done.txt";

    /// <summary>Written ONLY by an agent whose clone already contains <see cref="ProducerMarker"/> — the S1 handoff mechanism proof.</summary>
    public const string DependentMarker = "step2-done.txt";

    private readonly string _originalCodexCommand;
    private readonly string _originalClaudeCommand;
    private readonly string _dir;

    public DependencyHandoffFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-dephandoff-fakecli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var script = Path.Combine(_dir, "fake-agent.sh");
        File.WriteAllText(script, ScriptBody);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        // BOTH harnesses, not just codex: which harness a live-brain run actually dispatches is the MODEL's choice
        // (RealSupervisorActionExecutor.Spawn's authored agents[].harness), and an Anthropic-defaulted team pool
        // reconciles to claude-code on its own (HarnessModelReconciler). Stubbing codex alone left the dependent
        // agents running the REAL claude CLI, which can never write this fake's markers — so the probe silently
        // measured the harness lottery instead of the handoff.
        _originalCodexCommand = Environment.GetEnvironmentVariable(CodexHarness.CommandEnvVar) ?? "";
        _originalClaudeCommand = Environment.GetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar) ?? "";
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, script);
        Environment.SetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar, script);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, _originalCodexCommand.Length == 0 ? null : _originalCodexCommand);
        Environment.SetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar, _originalClaudeCommand.Length == 0 ? null : _originalClaudeCommand);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// POSIX <c>/bin/sh</c>, harness-agnostic. The WORK half is unchanged and dialect-free (a pure function of the
    /// clone's own filesystem state). Only the STREAM half branches, on the one discriminator that is a property of
    /// the invocation rather than of the runner: Codex's argv always starts with <c>exec</c>
    /// (<c>CodexHarness.BuildInvocation</c>) and Claude's always with <c>--print</c> (<c>ClaudeCodeHarness</c>).
    /// The codex branch is BYTE-IDENTICAL to before, so every existing codex consumer is unperturbed.
    ///
    /// <para>The two branches must stay 1:1 in EVENT terms — one AssistantMessage then one Completed — so a fake run
    /// folds the same Summary either way. Emitting both dialects unconditionally instead would CORRUPT codex: a
    /// Claude <c>{"type":"assistant","message":{…}}</c> line parses under <c>CodexHarness</c> as an AssistantMessage
    /// whose text is the literal string "assistant", and being last it would win the summary. Do not emit Claude's
    /// <c>{"type":"system","subtype":"init"}</c> line either — it maps to a leading Started event with no codex
    /// counterpart, breaking the 1:1.</para>
    /// </summary>
    private static string ScriptBody =>
        "#!/bin/sh\n" +
        "if [ -f " + ProducerMarker + " ]; then\n" +
        "  printf 'built on the producer\\n' > " + DependentMarker + "\n" +
        "  printf 'agent work\\n' > agent_2.txt\n" +
        "else\n" +
        "  printf 'step1 complete\\n' > " + ProducerMarker + "\n" +
        "  printf 'agent work\\n' > agent_1.txt\n" +
        "fi\n" +
        "if [ \"$1\" = 'exec' ]; then\n" +
        "  printf '{\"type\":\"agent_message\",\"message\":\"DONE\"}\\n'\n" +
        "  printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "else\n" +
        "  printf '{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"DONE\"}]}}\\n'\n" +
        "  printf '{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"completed\",\"is_error\":false}\\n'\n" +
        "fi\n" +
        "exit 0\n";
}
