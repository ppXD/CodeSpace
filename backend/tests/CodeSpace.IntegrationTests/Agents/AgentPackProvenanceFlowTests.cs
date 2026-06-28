using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// Pack provenance on the bench — an imported persona's summary carries its source pack's name (owner/repo) so
/// the card can show where it came from; an authored persona carries none. Read through the mediator (the real
/// list/detail path), proving the batched pack-name join is team-scoped.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentPackProvenanceFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentPackProvenanceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_imported_persona_carries_its_pack_name_and_an_authored_one_does_not()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var importedId = await SeedImportedAgentAsync(teamId, userId, "obra/superpowers", "imported-reviewer");

        Guid authoredId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            authoredId = await scope.Resolve<IMediator>().Send(new CreateAgentDefinitionCommand { Name = "Local Helper" });

        using var verify = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = verify.Resolve<IMediator>();

        var imported = await mediator.Send(new GetAgentDefinitionQuery { AgentDefinitionId = importedId });
        imported!.PackName.ShouldBe("obra/superpowers", customMessage: "an imported persona must surface its source pack's owner/repo so the bench can badge where it came from");

        var authored = await mediator.Send(new GetAgentDefinitionQuery { AgentDefinitionId = authoredId });
        authored!.PackName.ShouldBeNull("an authored persona has no source pack");

        // The list path carries it too.
        var list = await mediator.Send(new ListAgentDefinitionsQuery());
        list.Single(a => a.Id == importedId).PackName.ShouldBe("obra/superpowers");
        list.Single(a => a.Id == authoredId).PackName.ShouldBeNull();
    }

    private async Task<Guid> SeedImportedAgentAsync(Guid teamId, Guid userId, string packName, string slug)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        var packId = Guid.NewGuid();
        db.Pack.Add(new Pack { Id = packId, TeamId = teamId, Kind = PackKind.Github, Name = packName, Url = $"https://github.com/{packName}", CreatedBy = userId, LastModifiedBy = userId, CreatedDate = now, LastModifiedDate = now });

        var agentId = Guid.NewGuid();
        db.AgentDefinition.Add(new AgentDefinition
        {
            Id = agentId, TeamId = teamId, Slug = slug, Name = "Imported Reviewer", SystemPrompt = "p",
            Origin = AgentDefinitionOrigin.Imported, PackId = packId, SourcePath = $"agents/{slug}.md",
            CreatedBy = userId, LastModifiedBy = userId, CreatedDate = now, LastModifiedDate = now,
        });

        await db.SaveChangesAsync();
        return agentId;
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, Email = $"prov-{userId:N}@test.local", Name = $"prov-user-{userId:N}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"prov-team-{teamId:N}", Name = "Provenance Test Team", Kind = TeamKind.Workspace, OwnerUserId = userId, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}
