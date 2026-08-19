namespace CodeSpace.Messages.Agents;

/// <summary>
/// The per-run resource ceilings one autonomy tier grants — the pair the durable launch turns into this run's
/// cgroup-v2 <c>memory.max</c> + <c>cpu.max</c>. Derived from the tier by <c>AgentAutonomyPolicy.Ceilings</c>, which
/// owns the committed table; this record only carries the answer to the sandbox spec.
///
/// <para>Both are ceilings on the agent AND every descendant, not on the CLI process alone: the point is that a
/// runaway build / test / fork subtree is charged to the run that spawned it. Neither is ever zero for a known tier —
/// zero means "unlimited" to the sandbox spec, which is the state this pair exists to end.</para>
/// </summary>
public sealed record AgentResourceCeilings
{
    /// <summary>Max resident memory for the run's whole subtree, in MiB — becomes <c>SandboxSpec.MaxMemoryMb</c>. Exceeding it is a kernel OOM kill of the subtree, not a throttle.</summary>
    public required int MemoryMb { get; init; }

    /// <summary>Max CPU for the run's whole subtree, as a percent of ONE core (400 ⇒ four cores' worth) — becomes <c>SandboxSpec.MaxCpuPercent</c>. Exceeding it THROTTLES; nothing is killed.</summary>
    public required int CpuPercent { get; init; }
}
