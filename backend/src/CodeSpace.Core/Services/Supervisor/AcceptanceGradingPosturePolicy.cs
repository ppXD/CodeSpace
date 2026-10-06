using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// Derives the <see cref="AcceptanceGradingPosture"/> a grade runs under from the run that produced the work, and
/// applies it to each grade step. The grade runs the candidate's own bytes after the producing run's sandbox has gone:
/// a setup step installs from a manifest the agent wrote, and the check imports scripts beside the ones it names. So a
/// grade step gets no more network, egress or resources than the producing run had.
///
/// <para>NARROW-ONLY at every layer. A step gets the network only when the producer's own grant
/// (<see cref="AgentPermissions.Network"/>) has it AND its tier, clamped by the deployment ceiling
/// (<c>Sandbox:MaxAutonomy</c>, the same ceiling <c>RunCommandService.BuildSpec</c> narrows <c>agent.run_command</c>
/// to), derives it (<see cref="AgentAutonomyPolicy.Derive"/>). A clamped tier never derives network its ceiling denies. An
/// <see cref="AgentEgressPolicy.Allowlist"/> producer is narrowed to its operator-configured hosts only. The model API
/// and git hosts it also reached are not added: a grade carries no model key, and its clone is already on disk. An
/// allowlist that names no host severs the step, the same fail-closed rule <c>AgentRunExecutor.ApplyEgressPolicy</c>
/// applies to the agent. The memory and CPU ceilings are the clamped tier's committed row
/// (<see cref="AgentAutonomyPolicy.Ceilings"/>), narrowed by the operator's host budget.</para>
/// </summary>
public static class AcceptanceGradingPosturePolicy
{
    /// <summary>
    /// The opening of the notice a grade records when its setup step ran with less network than it asked for. A pinned
    /// literal (Rule 8): an operator whose setup stopped downloading searches for it.
    /// </summary>
    public const string SetupNetworkNoticePrefix = "setup network: ";

    /// <summary>The posture of a grade whose producing run is unknown: network off and the Confined tier's resource ceilings. A grade that cannot say whose work it runs gets the least anyone could have had.</summary>
    public static AcceptanceGradingPosture FailClosed => For(AgentAutonomyLevel.Confined, AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Confined));

    /// <summary>The posture of a grade of <paramref name="producer"/>'s work: the tier and permissions it ran with, as stored after admission.</summary>
    public static AcceptanceGradingPosture For(AgentTask producer) => For(producer.Autonomy, producer.Permissions);

    /// <summary>
    /// The posture of a grade of the work a run with this stored <c>TaskJson</c> produced, or null when the task cannot
    /// be read — the caller then grades <see cref="FailClosed"/>. The one reader every lane that only holds the
    /// producer's row goes through.
    /// </summary>
    public static AcceptanceGradingPosture? ForStoredTask(string? taskJson)
    {
        if (string.IsNullOrWhiteSpace(taskJson)) return null;

        try
        {
            var task = JsonSerializer.Deserialize<AgentTask>(taskJson, AgentJson.Options);
            return task is null ? null : For(task);
        }
        catch (JsonException) { return null; }
    }

    /// <summary><paramref name="runner"/> bound to <paramref name="posture"/>: every spec handed to the result runs narrowed by <see cref="Apply"/>. The ONE way a grade hands an oracle a runner.</summary>
    internal static PostureBoundSandboxRunner Bind(ISandboxRunner runner, AcceptanceGradingPosture posture) => new(runner, posture);

    /// <summary>The posture of a grade of work produced under <paramref name="autonomy"/> and <paramref name="permissions"/>, on this deployment.</summary>
    public static AcceptanceGradingPosture For(AgentAutonomyLevel autonomy, AgentPermissions permissions) =>
        Derive(autonomy, permissions, AgentAutonomyPolicy.DeploymentCeiling, RuntimeSettings.Current.AgentMemoryCeilingMb);

    /// <summary>
    /// <see cref="For(AgentAutonomyLevel, AgentPermissions)"/> over an explicit deployment ceiling and host memory
    /// budget, so the table is pinned without staging configuration. An unknown tier reads as Confined: a value this
    /// policy cannot recognise must not widen a grade.
    /// </summary>
    internal static AcceptanceGradingPosture Derive(AgentAutonomyLevel autonomy, AgentPermissions permissions, AgentAutonomyLevel deploymentCeiling, int? hostMemoryBudgetMb)
    {
        var tier = Enum.IsDefined(autonomy) ? AgentAutonomyPolicy.Clamp(autonomy, deploymentCeiling) : AgentAutonomyLevel.Confined;
        var ceilings = AgentAutonomyPolicy.Ceilings(tier, hostMemoryBudgetMb);
        var network = permissions.Network == AgentNetworkAccess.On && AgentAutonomyPolicy.Derive(tier).Network == AgentNetworkAccess.On;
        var allowlist = network && permissions.Egress == AgentEgressPolicy.Allowlist ? EgressAllowlistBuilder.Build(null, null, Array.Empty<string>(), permissions.EgressAllowHosts) : null;

        return new AcceptanceGradingPosture
        {
            Autonomy = tier,
            AllowNetwork = network && allowlist is not { Count: 0 },
            EgressAllowlist = allowlist is { Count: > 0 } ? allowlist : null,
            MaxMemoryMb = ceilings.MemoryMb,
            MaxCpuPercent = ceilings.CpuPercent,
        };
    }

    /// <summary>
    /// <paramref name="spec"/> as a grade step may run it under <paramref name="posture"/>. A grade step says only
    /// WHETHER it needs a remote: the setup step asks, the check does not. Which hosts it reaches is the posture's
    /// decision alone, and no grade step carries an allowlist of its own. The step keeps network only when it asked
    /// and the posture allows. Each resource ceiling is the narrower of the step's and the posture's, where 0 means
    /// unlimited.
    /// </summary>
    internal static SandboxSpec Apply(SandboxSpec spec, AcceptanceGradingPosture posture)
    {
        var network = spec.AllowNetwork && posture.AllowNetwork;

        return spec with
        {
            AllowNetwork = network,
            EgressAllowlist = network ? posture.EgressAllowlist : null,
            MaxMemoryMb = Narrower(spec.MaxMemoryMb, posture.MaxMemoryMb),
            MaxCpuPercent = Narrower(spec.MaxCpuPercent, posture.MaxCpuPercent),
        };
    }

    /// <summary>
    /// What a grade records when its setup step, which asks for the network, runs under <paramref name="posture"/> and
    /// the runner beneath enforces <paramref name="enforced"/> for it: null when the setup kept the host network — the
    /// posture shares it, or nothing on this host narrowed it — or the runner cannot say. Otherwise one line saying the
    /// sandbox severed or filtered it, and what an operator whose setup must download can do about it. Keyed on what was
    /// ENFORCED, never on the spec, so a host that does not confine never reads "off" for a setup that kept its network.
    /// </summary>
    public static string? SetupNetworkNotice(AcceptanceGradingPosture posture, SandboxEgressMode? enforced) => (enforced, posture.EgressAllowlist) switch
    {
        (SandboxEgressMode.None, { Count: > 0 } hosts) => $"{SetupNetworkNoticePrefix}off — this host cannot filter to the producing run's egress allowlist ({string.Join(", ", hosts)}), so the sandbox severed it; a setup that downloads needs a host that filters egress, or a higher tier",
        (SandboxEgressMode.None, _) => $"{SetupNetworkNoticePrefix}off, as the producing run had it ({posture.Autonomy}) — the sandbox severed it; a setup that downloads needs an egress allowlist or a higher tier",
        (SandboxEgressMode.Filtered, { Count: > 0 } hosts) => $"{SetupNetworkNoticePrefix}narrowed to the producing run's egress allowlist ({string.Join(", ", hosts)}) — the sandbox filtered it",
        _ => null,
    };

    private static int Narrower(int step, int posture) => step <= 0 ? posture : posture <= 0 ? step : Math.Min(step, posture);
}
