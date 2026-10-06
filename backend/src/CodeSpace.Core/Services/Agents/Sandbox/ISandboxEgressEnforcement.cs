using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Sandbox;

/// <summary>
/// Optional capability a sandbox runner MAY implement alongside <see cref="ISandboxRunner"/> (Rule 7 / ISP — a sibling
/// interface, never a widening of the base contract): say, before it runs <c>spec</c>, what egress it would actually
/// ENFORCE for it — which is not always what the spec asks. A spec with <see cref="SandboxSpec.AllowNetwork"/> false is
/// severed only where the runner confines; one with an allowlist is filtered only where the host can filter, and
/// otherwise severed or, where nothing confines, not narrowed at all. The runner is the one place that knows what it
/// can build on this host, so a caller that must report what a narrowing actually did asks it here rather than
/// reading the spec back as if it were the outcome. A runner without this capability makes no claim.
/// </summary>
public interface ISandboxEgressEnforcement
{
    /// <summary>The egress <paramref name="spec"/> would actually get on this host: <see cref="SandboxEgressMode.None"/> severed, <see cref="SandboxEgressMode.Filtered"/> held to its allowlist, <see cref="SandboxEgressMode.Full"/> the host network.</summary>
    SandboxEgressMode EnforcedEgress(SandboxSpec spec);
}
