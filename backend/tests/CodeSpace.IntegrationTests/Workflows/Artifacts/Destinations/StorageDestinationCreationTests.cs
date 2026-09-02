using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing.Exceptions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Destinations;

/// <summary>
/// Creating a destination is one transaction or it is nothing.
///
/// <para>Why that has to be true rather than merely tidy: none of the rows underneath can be deleted. A credential
/// cannot, a profile cannot, and a route row is the team's only one for its data class forever. So a sequence that
/// got three rows in and then met a destination that would not take a write used to leave an operator with
/// permanent wreckage and a lifecycle vocabulary to learn before they could reason about it. The refusal these tests
/// pin is the whole point of composing the steps server-side.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageDestinationCreationTests : IDisposable
{
    private const string WorkflowArtifacts = WorkflowArtifactDestinationResolver.DataClassTypeKey;
    private const string AgentRunLogs = AgentRunLogStorageResolver.DataClassTypeKey;

    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public StorageDestinationCreationTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task One_command_records_the_destination_and_activates_everything_that_has_to_be_active()
    {
        var world = await SeedTeamAsync();

        var destination = await CreateAsync(world, Root(), [WorkflowArtifacts, AgentRunLogs]);

        destination.State.ShouldBe(StorageProfileStateValue.Active, "a Draft profile is refused for every write, so recording one would record a destination that silently drops artifacts");
        destination.DataClassTypeKeys.ShouldBe([WorkflowArtifacts, AgentRunLogs]);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.StorageProfile.SingleAsync(row => row.TeamId == world.TeamId)).State.ShouldBe(StorageProfileState.Active);
        var routes = await db.StorageRoute.Where(row => row.TeamId == world.TeamId).ToListAsync();
        routes.Select(route => route.DataClassTypeKey).Order().ShouldBe(new[] { AgentRunLogs, WorkflowArtifacts }.Order());
        routes.ShouldAllBe(route => route.State == StorageRouteState.Active);
    }

    /// <summary>
    /// The claim the whole command exists for. Route activation writes and discards one real object, and it is the
    /// LAST step - so a destination that cannot take bytes is discovered after a credential, a profile, a revision
    /// and a route row have all been written. Every one of them has to be gone.
    /// </summary>
    [Fact]
    public async Task A_destination_that_will_not_take_a_write_leaves_nothing_behind_at_all()
    {
        var world = await SeedTeamAsync();

        var refused = await Should.ThrowAsync<StorageRouteInvalidException>(() => CreateAsync(world, $"/dev/null/unwritable-{Guid.NewGuid():N}", [WorkflowArtifacts]));

        refused.Message.ShouldContain("did not accept a write", Case.Sensitive);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.StorageCredential.CountAsync(row => row.TeamId == world.TeamId)).ShouldBe(0, "a credential written here could only ever be revoked, never removed");
        (await db.StorageProfile.CountAsync(row => row.TeamId == world.TeamId)).ShouldBe(0, "a profile written here could never be removed");
        (await db.StorageProfileRevision.CountAsync(row => row.TeamId == world.TeamId)).ShouldBe(0);
        (await db.StorageRoute.CountAsync(row => row.TeamId == world.TeamId)).ShouldBe(0, "and this data class's one route row would be spent for the life of the team");
    }

    /// <summary>
    /// The second destination. A data class carries exactly one route row forever, so claiming a class already
    /// claimed is a repoint - it moves where the NEXT write lands and touches neither the stored bytes nor the
    /// destination they are stored in, which must stay Active for them to remain readable.
    /// </summary>
    [Fact]
    public async Task Claiming_a_data_class_another_destination_holds_repoints_it_and_leaves_the_old_one_readable()
    {
        var world = await SeedTeamAsync();
        var first = await CreateAsync(world, Root(), [WorkflowArtifacts]);

        var second = await CreateAsync(world, Root(), [WorkflowArtifacts]);

        second.ProfileId.ShouldNotBe(first.ProfileId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var route = await db.StorageRoute.SingleAsync(row => row.TeamId == world.TeamId && row.DataClassTypeKey == WorkflowArtifacts);
        route.CurrentRevision.ShouldBe(2, "the route was repointed, not replaced - a data class gets one route row for the life of the team");
        route.State.ShouldBe(StorageRouteState.Active);
        var head = await db.StorageRouteRevision.SingleAsync(row => row.StorageRouteId == route.Id && row.Revision == 2);
        head.StorageProfileId.ShouldBe(second.ProfileId);
        (await db.StorageProfile.SingleAsync(row => row.Id == first.ProfileId)).State.ShouldBe(StorageProfileState.Active, "bytes already stored under the old destination are only readable while it resolves");
    }

    /// <summary>
    /// A provider whose secret schema requires nothing gets no credential at all - the one case with no key to
    /// mint - and that is not a failure. Read off the provider's own schema, so a future provider needs no change here.
    /// </summary>
    [Fact]
    public async Task A_provider_that_needs_no_secret_records_no_credential()
    {
        var world = await SeedTeamAsync();

        var destination = await CreateAsync(world, Root(), [WorkflowArtifacts]);

        destination.CredentialId.ShouldBeNull();
        destination.CredentialRevision.ShouldBeNull();

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().StorageCredential.CountAsync(row => row.TeamId == world.TeamId)).ShouldBe(0);
    }

    /// <summary>Recording a destination without sending anything to it yet is legitimate: nothing is routed, and nothing is refused.</summary>
    [Fact]
    public async Task A_destination_that_claims_no_data_class_is_recorded_and_routes_nothing()
    {
        var world = await SeedTeamAsync();

        var destination = await CreateAsync(world, Root(), []);

        destination.State.ShouldBe(StorageProfileStateValue.Active);
        destination.DataClassTypeKeys.ShouldBeEmpty();

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().StorageRoute.CountAsync(row => row.TeamId == world.TeamId)).ShouldBe(0);
    }

    public void Dispose()
    {
        foreach (var root in _roots.Where(Directory.Exists)) Directory.Delete(root, recursive: true);
    }

    private string Root()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codespace-destination-{Guid.NewGuid():N}");
        _roots.Add(root);
        return root;
    }

    private async Task<StorageDestinationDetail> CreateAsync(World world, string rootPath, string[] dataClassTypeKeys)
    {
        using var scope = _fixture.BeginScopeAs(world.ActorId, world.TeamId, Roles.Admin);

        return await scope.Resolve<IMediator>().Send(new CreateStorageDestinationCommand
        {
            Name = $"dest-{Guid.NewGuid():N}",
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey,
            NonSecretConfig = JsonSerializer.SerializeToElement(new { rootPath }),
            DataClassTypeKeys = dataClassTypeKeys,
        });
    }

    private async Task<World> SeedTeamAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"dest-{actorId:N}@test.local", Name = $"dest-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"dest-{teamId:N}", Name = "Storage Destination Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();

        return new World(actorId, teamId);
    }

    private sealed record World(Guid ActorId, Guid TeamId);
}
