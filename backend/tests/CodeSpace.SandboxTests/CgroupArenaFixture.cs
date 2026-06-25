namespace CodeSpace.SandboxTests;

/// <summary>
/// A xUnit COLLECTION fixture that bootstraps the cgroup-v2 arena EXACTLY ONCE for the whole <c>Cgroup</c> collection
/// (every cgroup E2E class — <see cref="CgroupResourceE2ETests"/> + the durable-launch wiring) — so the
/// controller-enablement + the (process-moving) leaf-ify run a single time on the clean current cgroup, and every test
/// reuses the captured <see cref="CgroupTestArena.Root"/>. Doing it per-test (or per-class) mutated shared cgroupfs
/// state: the leaf-ify moves the test runner's OWN process into a sibling leaf, so a SECOND bootstrap's
/// <c>/proc/self/cgroup</c> resolves to that leaf → a cascade of EBUSY / missing-controllers. The shared-once fixture +
/// the serialised collection close that — both within a class and across the cgroup classes (Rule-12 isolation).
/// </summary>
public sealed class CgroupArenaFixture : IDisposable
{
    /// <summary>The bootstrapped arena, or null when the environment can't delegate cgroups (then the E2E skips loudly / fails under REQUIRE_CGROUP).</summary>
    public CgroupTestArena? Arena { get; }

    /// <summary>The reason the arena could not be bootstrapped (empty on success) — surfaced in the skip/fail message.</summary>
    public string Why { get; }

    public CgroupArenaFixture()
    {
        Arena = CgroupTestArena.TryCreate(out var why);
        Why = why;
    }

    public void Dispose() => Arena?.Dispose();
}
