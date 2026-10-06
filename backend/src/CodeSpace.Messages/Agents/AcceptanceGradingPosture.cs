namespace CodeSpace.Messages.Agents;

/// <summary>
/// The sandbox posture an acceptance grade runs its steps under — the setup command and the check — taken from the
/// run that PRODUCED the work being graded: its autonomy tier, its network grant and its egress allowlist, already
/// clamped by the deployment's autonomy ceiling. A grade executes the candidate's own bytes (a manifest a setup step
/// installs from, a script the check imports), after the producing run's sandbox is gone, so it may never hand those
/// bytes more than the run that wrote them had.
///
/// <para>Derived ONCE per producing run by <c>AcceptanceGradingPosturePolicy</c> and carried on the grade request;
/// applied NARROW-ONLY at the grader's one choke point. A request with no posture grades with network off under the
/// Confined tier's resource ceilings.</para>
/// </summary>
public sealed record AcceptanceGradingPosture
{
    /// <summary>The producer's tier after the deployment ceiling: the row <see cref="MaxMemoryMb"/> and <see cref="MaxCpuPercent"/> come from, and the tier a narrowed-setup notice names.</summary>
    public required AgentAutonomyLevel Autonomy { get; init; }

    /// <summary>Whether a grade step that asks for the network may have it. False ⇒ every step is severed (where the sandbox confines).</summary>
    public required bool AllowNetwork { get; init; }

    /// <summary>The hosts a networked grade step is narrowed to — the producer's operator-configured egress allowlist. Null ⇒ no narrowing beyond <see cref="AllowNetwork"/>. Never empty: an allowlist with no host is <see cref="AllowNetwork"/> false.</summary>
    public IReadOnlyList<string>? EgressAllowlist { get; init; }

    /// <summary>The memory ceiling every grade step runs under, in MiB — the producer tier's committed row, narrowed by the host budget.</summary>
    public required int MaxMemoryMb { get; init; }

    /// <summary>The CPU ceiling every grade step runs under, as a percent of one core — the producer tier's committed row.</summary>
    public required int MaxCpuPercent { get; init; }
}
