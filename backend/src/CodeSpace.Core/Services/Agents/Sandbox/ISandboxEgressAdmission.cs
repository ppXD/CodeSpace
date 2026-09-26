using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Sandbox;

/// <summary>
/// Optional capability a sandbox runner MAY implement alongside <see cref="ISandboxRunner"/> (Rule 7 / ISP — a sibling
/// interface, never a widening of the base contract): refuse, BEFORE anything is spent, a spec this runner could only
/// launch with a network that leaves the agent unable to do its work. The runner is the one place that knows what it
/// can actually build on this host, and the executor asks it at the one moment a refusal still costs nothing — after
/// the spec is built, before the spend is admitted and the process started.
///
/// <para>The first such spec is a network-off run whose model is brokered (<see cref="SandboxSpec.ModelBrokerPort"/>)
/// on a host that confines but cannot seal: severing it would cut its broker off too. A runner without this capability
/// simply launches, exactly as it always has.</para>
/// </summary>
public interface ISandboxEgressAdmission
{
    /// <summary>
    /// Throws an <see cref="CodeSpace.Messages.Failures.IFailure"/> naming the wall when this runner cannot give
    /// <paramref name="spec"/> the egress it needs; returns otherwise. <paramref name="modelBrokerReachableFromNamespace"/>
    /// is whether the run's broker listens where a per-run namespace can reach it (<c>BrokeredModelCredential.ReachableFromNamespace</c>).
    /// </summary>
    void EnsureEgressAdmissible(SandboxSpec spec, bool modelBrokerReachableFromNamespace);
}
