using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A high-VOLUME variant of <see cref="SubtaskAwareFakeCli"/>: instead of one summary line, the spawned fake
/// codex process emits <see cref="LineCount"/> ordered <c>agent_message</c> lines tagged with its per-branch goal
/// (<c>"&lt;goal&gt;#001" … "&lt;goal&gt;#060"</c>) then <c>task_complete</c>. Used by the D1 map-fan-out test:
/// several REAL durable agent tails write CONCURRENTLY into the shared <c>agent_run_event</c> table through the
/// batched writer, and each branch's per-run event log must read back complete, in order, and tagged with ONLY
/// its own goal — proving the batched write keeps per-run isolation under genuine engine fan-out.
///
/// <para>Like <see cref="SubtaskAwareFakeCli"/>, one process-wide <see cref="CodexHarness.CommandEnvVar"/> serves
/// every branch; the per-branch differentiation rides the GOAL ARG the harness passes (the production seam).
/// Strictly POSIX (the runner spawns it via <c>#!/bin/sh</c> — dash-safe, no bashisms).</para>
/// </summary>
public sealed class HighVolumeSubtaskFakeCli : IDisposable
{
    /// <summary>Ordered <c>agent_message</c> lines emitted per branch — comfortably above the 256 buffer cap is unnecessary; 60 spans several spool polls + checkpoints while staying fast.</summary>
    public const int LineCount = 60;

    private readonly string _originalCommand;
    private readonly string _dir;

    public HighVolumeSubtaskFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-hv-subtask-fakecli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var script = Path.Combine(_dir, "fake-codex.sh");
        File.WriteAllText(script, ScriptBody);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _originalCommand = Environment.GetEnvironmentVariable(CodexHarness.CommandEnvVar) ?? "";
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, script);
    }

    /// <summary>The ordered AssistantMessage texts the harness parses for a given branch goal — the per-branch log this test asserts complete + uncontaminated.</summary>
    public static IReadOnlyList<string> ExpectedLinesFor(string goal) =>
        Enumerable.Range(1, LineCount).Select(i => $"{goal}#{i:D3}").ToList();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, _originalCommand.Length == 0 ? null : _originalCommand);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static string ScriptBody =>
        "#!/bin/sh\n" +
        "goal=\"\"\n" +
        "for goal in \"$@\"; do :; done\n" +
        "esc=$(printf '%s' \"$goal\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
        "i=1\n" +
        "while [ $i -le " + LineCount + " ]; do\n" +
        "  n=$(printf '%03d' \"$i\")\n" +
        "  printf '{\"type\":\"agent_message\",\"message\":\"%s#%s\"}\\n' \"$esc\" \"$n\"\n" +
        "  i=$((i+1))\n" +
        "done\n" +
        "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "exit 0\n";
}
