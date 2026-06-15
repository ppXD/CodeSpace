using CodeSpace.Core.Services.Tasks.Launch;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

/// <summary>
/// The seed-provider registry — the GENERIC dispatch spine of the launch surface. Pins the
/// <c>AgentHarnessRegistry</c>-shaped contract: index by the open surface kind, dedup-throws in the ctor,
/// resolve-throws on unknown, <c>TryResolve</c> returns false. A new surface becomes resolvable by registering
/// a provider — never a switch edit here.
/// </summary>
[Trait("Category", "Unit")]
public class TaskLaunchSeedProviderRegistryTests
{
    [Fact]
    public void Resolves_a_registered_provider_by_its_surface_kind()
    {
        var chat = new StubProvider(TaskLaunchSurfaceKinds.Chat);
        var pr = new StubProvider(TaskLaunchSurfaceKinds.Pr);

        var registry = new TaskLaunchSeedProviderRegistry(new ITaskLaunchSeedProvider[] { chat, pr });

        registry.Resolve(TaskLaunchSurfaceKinds.Chat).ShouldBeSameAs(chat);
        registry.Resolve(TaskLaunchSurfaceKinds.Pr).ShouldBeSameAs(pr);
        registry.All.Count.ShouldBe(2);
    }

    [Fact]
    public void Ctor_throws_on_duplicate_surface_kinds()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            new TaskLaunchSeedProviderRegistry(new ITaskLaunchSeedProvider[]
            {
                new StubProvider(TaskLaunchSurfaceKinds.Chat),
                new StubProvider(TaskLaunchSurfaceKinds.Chat),
            }));

        ex.Message.ShouldContain(TaskLaunchSurfaceKinds.Chat);
    }

    [Fact]
    public void Resolve_throws_on_an_unknown_surface_kind()
    {
        var registry = new TaskLaunchSeedProviderRegistry(new ITaskLaunchSeedProvider[] { new StubProvider(TaskLaunchSurfaceKinds.Chat) });

        Should.Throw<InvalidOperationException>(() => registry.Resolve("no-such-surface"))
            .Message.ShouldContain("no-such-surface");
    }

    [Fact]
    public void TryResolve_returns_true_for_known_and_false_for_unknown()
    {
        var chat = new StubProvider(TaskLaunchSurfaceKinds.Chat);
        var registry = new TaskLaunchSeedProviderRegistry(new ITaskLaunchSeedProvider[] { chat });

        registry.TryResolve(TaskLaunchSurfaceKinds.Chat, out var found).ShouldBeTrue();
        found.ShouldBeSameAs(chat);

        registry.TryResolve("no-such-surface", out var missing).ShouldBeFalse();
        missing.ShouldBeNull();
    }

    private sealed class StubProvider : ITaskLaunchSeedProvider
    {
        public StubProvider(string surfaceKind) { SurfaceKind = surfaceKind; }

        public string SurfaceKind { get; }

        public Task<TaskLaunchSeed> SeedAsync(TaskLaunchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new TaskLaunchSeed { Goal = "stub", SurfaceKind = SurfaceKind, TeamId = request.TeamId });
    }
}
