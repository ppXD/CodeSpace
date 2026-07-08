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

    private readonly string _originalCommand;
    private readonly string _dir;

    public DependencyHandoffFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-dephandoff-fakecli-" + Guid.NewGuid().ToString("N"));
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

    private static string ScriptBody =>
        "#!/bin/sh\n" +
        "if [ -f " + ProducerMarker + " ]; then\n" +
        "  printf 'built on the producer\\n' > " + DependentMarker + "\n" +
        "  printf 'agent work\\n' > agent_2.txt\n" +
        "else\n" +
        "  printf 'step1 complete\\n' > " + ProducerMarker + "\n" +
        "  printf 'agent work\\n' > agent_1.txt\n" +
        "fi\n" +
        "printf '{\"type\":\"agent_message\",\"message\":\"DONE\"}\\n'\n" +
        "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "exit 0\n";
}
