using CodeSpace.Core.Services.Tasks.Capabilities;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the generic capability-probe registry — the SAME IEnumerable&lt;T&gt;+dedup+resolve shape
/// <c>AgentHarnessRegistry</c> uses. Resolution is <c>IsAvailable(openString)</c> with no per-capability switch:
/// a probe is consulted purely by registering its class, a duplicate capability is rejected in the ctor, and an
/// UNKNOWN capability fails OPEN to true (the router degrade is a UX nicety — the projection's own
/// execution-time gate is the real enforcer, so an unprobed capability never blocks routing).
/// </summary>
[Trait("Category", "Unit")]
public class CapabilityProbeRegistryTests
{
    [Fact]
    public void Resolves_a_registered_probe_to_its_verdict()
    {
        var registry = new CapabilityProbeRegistry(new ICapabilityProbe[] { new FakeProbe("cap-on", available: true), new FakeProbe("cap-off", available: false) });

        registry.IsAvailable("cap-on").ShouldBeTrue();
        registry.IsAvailable("cap-off").ShouldBeFalse();
    }

    [Fact]
    public void Unknown_capability_fails_open_to_available()
    {
        var registry = new CapabilityProbeRegistry(new ICapabilityProbe[] { new FakeProbe("known", available: false) });

        registry.IsAvailable("never-probed").ShouldBeTrue("an unprobed capability fails OPEN — the router degrade is a UX nicety, not the enforcer");
    }

    [Fact]
    public void Capabilities_lists_every_registered_probe()
    {
        var registry = new CapabilityProbeRegistry(new ICapabilityProbe[] { new FakeProbe("a", true), new FakeProbe("b", false) });

        registry.Capabilities.ShouldBe(new[] { "a", "b" }, ignoreOrder: true);
    }

    [Fact]
    public void Construction_rejects_duplicate_capabilities()
    {
        Should.Throw<InvalidOperationException>(() =>
            new CapabilityProbeRegistry(new ICapabilityProbe[] { new FakeProbe("same", true), new FakeProbe("same", false) }));
    }

    private sealed class FakeProbe : ICapabilityProbe
    {
        private readonly bool _available;
        public FakeProbe(string capability, bool available) { Capability = capability; _available = available; }
        public string Capability { get; }
        public bool IsAvailable() => _available;
    }
}
