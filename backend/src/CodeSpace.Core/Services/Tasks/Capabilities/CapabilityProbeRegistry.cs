using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Tasks.Capabilities;

/// <summary>
/// Default <see cref="ICapabilityProbeRegistry"/> — indexes every registered <see cref="ICapabilityProbe"/> by
/// its <see cref="ICapabilityProbe.Capability"/>. Mirrors <c>AgentHarnessRegistry</c> EXACTLY: DI injects all
/// probes, this dedups (a duplicate capability throws in the ctor) + resolves by the open string. Registered via
/// the <see cref="ISingletonDependency"/> marker, so adding a probe needs no wiring here.
///
/// <para><b>Fail-OPEN by design.</b> <see cref="IsAvailable"/> returns true for an UNPROBED capability rather
/// than false. The router degrade this gate drives is a UX nicety — it picks a softer recipe when a lane is off
/// so the operator gets an honest fallback instead of an execution-time failure. The REAL enforcer is the
/// projection's own execution-time gate (a gated node fails closed when its own flag is off). So an unknown
/// capability defaulting to "available" can never bypass safety — at worst the projected run hits the node's own
/// fail-closed gate. (Fail-CLOSED is the deferred policy, documented in PR6.)</para>
/// </summary>
public sealed class CapabilityProbeRegistry : ICapabilityProbeRegistry, ISingletonDependency
{
    private readonly IReadOnlyDictionary<string, ICapabilityProbe> _byCapability;

    public CapabilityProbeRegistry(IEnumerable<ICapabilityProbe> probes)
    {
        var list = probes.ToList();

        var duplicates = list.GroupBy(p => p.Capability).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate ICapabilityProbe capabilities: {string.Join(", ", duplicates)}");

        _byCapability = list.ToDictionary(p => p.Capability);
        Capabilities = list.Select(p => p.Capability).ToList();
    }

    public IReadOnlyList<string> Capabilities { get; }

    public bool IsAvailable(string capability) =>
        _byCapability.TryGetValue(capability, out var probe) ? probe.IsAvailable() : true;
}
