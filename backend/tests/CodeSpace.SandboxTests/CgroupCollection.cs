using Xunit;

namespace CodeSpace.SandboxTests;

/// <summary>
/// The xUnit collection that serialises ALL cgroup E2E classes onto a SINGLE shared <see cref="CgroupArenaFixture"/>.
/// Two consequences, both required for correctness: (1) the arena's controller-enablement + (process-moving) leaf-ify
/// run EXACTLY ONCE for the whole suite — concurrent bootstraps across classes would re-leaf-ify the shared cgroupfs
/// and cascade (the same hazard the per-test fixture closed, now across classes); (2) the classes run sequentially, so
/// the process-global <c>CODESPACE_AGENT_CGROUP_ROOT</c> a durable-launch test sets/restores never overlaps another
/// cgroup test. Non-cgroup sandbox classes still parallelise — they never touch the arena or the env var.
/// </summary>
[CollectionDefinition("Cgroup")]
public sealed class CgroupCollection : ICollectionFixture<CgroupArenaFixture>
{
}
