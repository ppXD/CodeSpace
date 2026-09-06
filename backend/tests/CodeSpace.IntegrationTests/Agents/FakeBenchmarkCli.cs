using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// A no-op fake codex CLI: emits a minimal codex-shaped event stream and exits 0 WITHOUT touching the
/// workspace. The real <c>AgentRunExecutor</c> + <c>LocalProcessRunner</c> drive it; because it never edits
/// code, the grade is determined PURELY by the fixture's start-state — which is exactly what makes this a
/// deterministic PLUMBING proof, not a real-quality claim. Restores the env var + deletes the dir on dispose.
///
/// <para>A caller may supply its OWN <c>/bin/sh</c> body instead (the gateway-fault cases below) — everything else
/// about the pipeline stays the real one, so the only fake remains the CLI's intelligence.</para>
/// </summary>
public sealed class FakeBenchmarkCli : IDisposable
{
    /// <summary>The default body: succeed, touch nothing.</summary>
    public const string NoOpScript =
        "printf '{\"type\":\"agent_message\",\"message\":\"done (no-op benchmark CLI)\"}\\n'\n" +
        "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "exit 0\n";

    private readonly string? _original;
    private readonly string _dir;

    public FakeBenchmarkCli(string body = NoOpScript)
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-bench-fakecli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var script = Path.Combine(_dir, "fake-codex.sh");
        File.WriteAllText(script, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _original = Environment.GetEnvironmentVariable(CodexHarness.CommandEnvVar);
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, script);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, _original);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
