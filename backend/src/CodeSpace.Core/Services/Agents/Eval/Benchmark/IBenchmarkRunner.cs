using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// The INSTRUMENT: drives one fixed <see cref="BenchmarkTask"/> through one <see cref="BenchmarkMode"/> against
/// a pre-staged workspace, grades the result with the task's objective oracle, and returns a recorded
/// <see cref="BenchmarkResult"/>. Repeatable: same task + same staged workspace → same plumbing every time. The
/// agent MODEL is the only non-determinism — in CI it's the deterministic fake CLI (proving the plumbing); a
/// real-model run on demand produces the real comparison numbers.
///
/// <para>The runner does NOT stage the fixture or manage the workspace lifetime — the caller (a console
/// entrypoint / the integration harness) stages a fresh copy of the fixture per (task, mode) and disposes it.
/// This keeps the runner a pure "run-one-and-grade" function the same way <c>AgentRunExecutor</c> is — and lets
/// a test stage a fixture in exactly the pre/post state it wants to assert on.</para>
/// </summary>
public interface IBenchmarkRunner
{
    /// <summary>
    /// Run <paramref name="task"/> through <paramref name="mode"/> against the workspace at
    /// <paramref name="workspaceDirectory"/> (already staged to the fixture's failing start-state), grade it,
    /// and return the result. The <paramref name="teamId"/> scopes the created agent run (multi-tenant). The
    /// agent run is created + executed through the REAL <c>IAgentRunExecutor</c>; the model behind the harness
    /// CLI is whatever the environment's <c>CommandEnvVar</c> points at (a fake CLI in CI, the real binary on
    /// demand).
    /// </summary>
    Task<BenchmarkResult> RunAsync(BenchmarkTask task, BenchmarkMode mode, string workspaceDirectory, Guid teamId, CancellationToken cancellationToken);
}
