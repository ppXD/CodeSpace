using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing.Exceptions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Routing;

/// <summary>
/// The gate on the last unguarded one-way door. Activation refuses every transition back to Draft, Retired is terminal
/// and a route cannot be deleted — so a route bound to a destination that is not taking bytes is bound for good.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageRouteActivationProbeTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public StorageRouteActivationProbeTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_route_whose_destination_accepts_a_write_activates()
    {
        var world = await SeedAsync(NewRoot());

        var activated = await ActivateAsync(world);

        activated.ShouldNotBeNull().State.ShouldBe(StorageRouteStateValue.Active);
    }

    [Fact]
    public async Task A_route_whose_destination_refuses_a_write_is_not_activated_and_says_which_end_to_fix()
    {
        // Before this gate the entire check was a database read asserting the profile row says Active — no driver
        // opened, no credential resolved, nothing written. A route pointing at a nonexistent bucket reached Active
        // and started binding writes.
        var world = await SeedAsync("/dev/null/codespace-cannot-write-here");

        var refused = await Should.ThrowAsync<StorageRouteInvalidException>(() => ActivateAsync(world));

        refused.Message.ShouldContain("did not accept a write");
        refused.Message.ShouldContain("cannot be undone", Case.Insensitive, "an operator refused a one-way door deserves to know it was one");

        await StateShouldBeAsync(world, StorageRouteState.Draft);
    }

    [Fact]
    public async Task Activating_a_route_that_is_already_active_stays_a_no_op_even_when_the_destination_is_down()
    {
        // A retry must not be the thing that breaks. Once the route is bound, refusing an idempotent call during a
        // transient outage would turn a harmless repeat into an error the caller cannot act on.
        var root = NewRoot();
        var world = await SeedAsync(root);
        await ActivateAsync(world);

        Directory.Delete(root, recursive: true);
        File.WriteAllText(root, "not a directory");

        var again = await ActivateAsync(world);

        again.ShouldNotBeNull().State.ShouldBe(StorageRouteStateValue.Active);
    }

    [Fact]
    public async Task The_probe_that_gates_activation_leaves_its_answer_behind()
    {
        // The gate and the observability plane are the same probe: an operator who was refused can see WHY on the
        // profile afterwards, without re-running anything.
        var world = await SeedAsync("/dev/null/codespace-cannot-write-here");

        await Should.ThrowAsync<StorageRouteInvalidException>(() => ActivateAsync(world));

        using var scope = _fixture.BeginScope();
        var health = await scope.Resolve<CodeSpaceDbContext>().StorageProfileHealth.AsNoTracking()
            .SingleOrDefaultAsync(row => row.StorageProfileId == world.ProfileId);

        health.ShouldNotBeNull().Status.ShouldNotBe(StorageProfileProbeStatusValue.Available);
    }

    // ─── World + helpers ─────────────────────────────────────────────────────

    private async Task<StorageRouteDetail?> ActivateAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var routes = scope.Resolve<IStorageRouteService>();
        var route = (await routes.GetAsync(world.TeamId, world.RouteId, null, 10, CancellationToken.None)).ShouldNotBeNull();

        return await routes.SetStateAsync(world.TeamId, world.ActorId, new SetStorageRouteStateCommand
        {
            RouteId = world.RouteId, ExpectedXmin = route.Xmin, ExpectedCurrentRevision = route.CurrentRevision,
            State = StorageRouteStateValue.Active,
        }, CancellationToken.None);
    }

    private async Task StateShouldBeAsync(World world, StorageRouteState expected)
    {
        using var scope = _fixture.BeginScope();

        (await scope.Resolve<CodeSpaceDbContext>().StorageRoute.AsNoTracking().SingleAsync(row => row.Id == world.RouteId))
            .State.ShouldBe(expected, "a refused activation must leave the route exactly where it was");
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codespace-activation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private async Task<World> SeedAsync(string rootPath)
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"activate-{actorId:N}@test.local", Name = "Activate" });
        db.Team.Add(new Team { Id = teamId, Slug = $"activate-{teamId:N}", Name = "Activate", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();

        // Built through the real services, so the ordering constraints this gate sits inside are the production ones.
        var profiles = scope.Resolve<IStorageProfileService>();
        var created = await profiles.CreateAsync(teamId, actorId, new CreateStorageProfileCommand
        {
            StableName = "activation-target", ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey,
            NonSecretConfig = JsonDocument.Parse(JsonSerializer.Serialize(new { rootPath })).RootElement,
        }, CancellationToken.None);

        var active = (await profiles.SetStateAsync(teamId, actorId, new SetStorageProfileStateCommand
        {
            ProfileId = created.Id, ExpectedXmin = created.Xmin, ExpectedCurrentRevision = created.CurrentRevision,
            State = StorageProfileStateValue.Active,
        }, CancellationToken.None)).ShouldNotBeNull();

        var route = await scope.Resolve<IStorageRouteService>().CreateAsync(teamId, actorId, new CreateStorageRouteCommand
        {
            DataClassTypeKey = "agent-run-log/v1", StorageProfileId = active.Id,
        }, CancellationToken.None);

        return new World(teamId, actorId, created.Id, route.Id);
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                else if (File.Exists(root)) File.Delete(root);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid ProfileId, Guid RouteId);
}
