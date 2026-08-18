using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Runtime;

/// <summary>
/// The routed artifact plane's SHAPE, which the container's convention registration turns into behaviour. None of it
/// is visible to the integration suite until a route exists, so it is pinned structurally here.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RoutedArtifactPlaneWiringTests
{
    [Fact]
    public void One_coordinator_serves_both_routed_ports()
    {
        // AsImplementedInterfaces + InstancePerLifetimeScope means both resolve to the SAME scoped coordinator, so a
        // transfer and the window read that follows it happen against one connection story rather than two.
        var coordinator = typeof(ArtifactCasRuntimeCoordinator);

        typeof(IArtifactCasRuntimeCoordinator).IsAssignableFrom(coordinator).ShouldBeTrue();
        typeof(IArtifactCasRangeReader).IsAssignableFrom(coordinator).ShouldBeTrue();
    }

    [Fact]
    public void Window_reads_stay_a_sibling_rather_than_widening_the_transfer_contract()
    {
        // Rule 7. A window read carries no digest guarantee where the whole-object read does; folding it onto the
        // transfer contract would hand every consumer one capability with two different guarantees.
        var transfers = typeof(IArtifactCasRuntimeCoordinator).GetMethods().Select(method => method.Name).Order().ToArray();

        transfers.ShouldBe([nameof(IArtifactCasRuntimeCoordinator.OpenReadAsync), nameof(IArtifactCasRuntimeCoordinator.PutAsync)]);
        typeof(IArtifactCasRangeReader).IsAssignableFrom(typeof(IArtifactCasRuntimeCoordinator)).ShouldBeFalse();
    }

    [Fact]
    public void No_port_offers_to_move_a_transfer_intent_backwards()
    {
        // artifact_cas_transfer_guard refuses every route out of Failed — a fence claim raises 'terminal rows cannot
        // be claimed', and a plain transition first demands an unexpired worker lease that a terminal row is
        // forbidden to hold. A port that promised a reopen could only ever raise PostgresException out of PutAsync,
        // so recovery is a FRESH intent (ArtifactStore.IdempotencyKeyFor) rather than a backwards move.
        var members = typeof(IArtifactCasRuntimeCoordinator).GetMethods()
            .Concat(typeof(IArtifactCasRangeReader).GetMethods())
            .Select(method => method.Name).ToArray();

        members.ShouldNotContain(name => name.Contains("Reopen", StringComparison.Ordinal) || name.Contains("Retry", StringComparison.Ordinal));
    }

    [Fact]
    public void The_routed_plane_is_a_scoped_dependency_carrying_exactly_the_two_ports()
    {
        // The convention scanner registers any IDependency class and resolves its constructor, so this record is only
        // satisfiable while every parameter is itself a registered port.
        typeof(IScopedDependency).IsAssignableFrom(typeof(ArtifactRoutedPlane)).ShouldBeTrue();

        var ports = typeof(ArtifactRoutedPlane).GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        ports.ShouldBe([typeof(IArtifactCasRuntimeCoordinator), typeof(IArtifactCasRangeReader)]);
    }

    [Fact]
    public void The_store_takes_the_whole_routed_plane_so_it_cannot_quietly_lose_a_port()
    {
        // Dropping the range port would restore an O(offset) read, which would still compile if the store injected
        // only the transfer coordinator.
        var dependencies = typeof(ArtifactStore).GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        dependencies.ShouldContain(typeof(ArtifactRoutedPlane));
        dependencies.ShouldNotContain(typeof(IArtifactCasRuntimeCoordinator));
        dependencies.ShouldContain(typeof(TimeProvider), "the bounded wait for a concurrent writer is clock-driven, not wall-clock-driven");
    }
}
