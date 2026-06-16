using System.Text;
using Autofac;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Artifacts;
using CodeSpace.Messages.Queries.Artifacts;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The D2 artifact READ surface, end-to-end through the mediator + real Postgres (mirrors
/// <see cref="Agents.AgentScorecardApiFlowTests"/>: the API project has no HTTP test host, and
/// <c>ArtifactsController.Get</c> is a one-line <c>_mediator.Send</c> → the mediator pipeline IS the API-flow).
///
/// <para>Proves the operator contract: the OWNING team fetches an offloaded artifact's bytes; a DIFFERENT team
/// gets null (the controller 404-conflates — existence never leaked); an absent id is null. Team scope is the
/// caller's (<c>ICurrentTeam</c> via BeginScopeAs), never the route.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ArtifactReadApiFlowTests
{
    private readonly PostgresFixture _fixture;

    public ArtifactReadApiFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task The_owning_team_downloads_an_offloaded_artifacts_bytes()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Offload a >8KiB payload so it goes out-of-band (the read must resolve storage_url, not inline bytes).
        var content = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => $"diff line {i}\n")));
        Guid artifactId;
        using (var scope = _fixture.BeginScope())
            artifactId = await scope.Resolve<IArtifactStore>().PutAsync(teamId, content, "text/x-diff", CancellationToken.None);

        var download = await GetAsync(userId, teamId, artifactId);

        download.ShouldNotBeNull();
        download!.Bytes.ShouldBe(content, "the owning team gets the full offloaded bytes");
        download.ContentType.ShouldBe("text/x-diff");
        download.Id.ShouldBe(artifactId);
    }

    [Fact]
    public async Task A_different_team_gets_null_tenancy_404_conflated()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, userB) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var content = Encoding.UTF8.GetBytes("team A's secret diff");
        Guid artifactId;
        using (var scope = _fixture.BeginScope())
            artifactId = await scope.Resolve<IArtifactStore>().PutAsync(teamA, content, "text/x-diff", CancellationToken.None);

        // Team B asks for team A's artifact id → null (the controller turns this into 404 — no existence leak).
        (await GetAsync(userB, teamB, artifactId)).ShouldBeNull("a foreign team never reads another team's artifact");
    }

    [Fact]
    public async Task An_absent_artifact_id_is_null()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        (await GetAsync(userId, teamId, Guid.NewGuid())).ShouldBeNull();
    }

    private async Task<ArtifactDownload?> GetAsync(Guid userId, Guid teamId, Guid artifactId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new GetArtifactQuery { ArtifactId = artifactId });
    }
}
