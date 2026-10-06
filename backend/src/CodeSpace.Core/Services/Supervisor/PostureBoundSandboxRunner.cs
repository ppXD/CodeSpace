using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// A grading runner bound to one producing run's posture: every spec a grade step hands it is narrowed by
/// <see cref="AcceptanceGradingPosturePolicy.Apply"/> before it runs. A grade's setup step and its oracle's own command
/// (handed to graders through <c>BenchmarkGradingContext.Runner</c>) both go through it, so an oracle added later is
/// bound without knowing the posture exists. Built only by <see cref="AcceptanceGradingPosturePolicy.Bind"/>; it carries
/// no DI marker, so the container never registers it.
/// </summary>
internal sealed class PostureBoundSandboxRunner(ISandboxRunner inner, AcceptanceGradingPosture posture) : ISandboxRunner
{
    public AcceptanceGradingPosture Posture { get; } = posture;

    public string Kind => inner.Kind;

    public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken) => inner.RunAsync(AcceptanceGradingPosturePolicy.Apply(spec, Posture), cancellationToken);

    /// <summary>The egress <paramref name="spec"/> actually gets once narrowed, as the runner beneath says it enforces it (<see cref="ISandboxEgressEnforcement"/>), or null when that runner cannot say.</summary>
    public SandboxEgressMode? EnforcedEgress(SandboxSpec spec) => inner is ISandboxEgressEnforcement enforcement ? enforcement.EnforcedEgress(AcceptanceGradingPosturePolicy.Apply(spec, Posture)) : null;
}
