using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// The STATUS CODE half of the defect, which nothing else pins. One offloaded node output whose stored bytes no longer
/// verify used to leave the whole-object read as a bare <c>InvalidOperationException</c>; the failure classifier reads
/// that as the caller's fault, so <c>GET /api/workflows/runs/{id}</c> answered <b>400</b> — an operator told their
/// request was malformed, with the sha in the message, for a fault that was entirely ours. Only a request through the
/// real host sees that number: below the controller the shape is an exception either way.
///
/// <para>Tier: 🟢 High-fidelity — the real ASP.NET pipeline (JWT auth, the X-Team-Id scope, the controller, the
/// GlobalExceptionFilter that produced the 400), real Postgres, the real durable ledger and the real content-addressed
/// store. Rot is staged the way production produces it: the stored object is overwritten with same-length foreign
/// bytes, so only the digest can catch it.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class RunDetailRottedOutputEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private const string RottedNodeId = "rotted";
    private const string HealthyNodeId = "healthy";

    private readonly TaskLaunchApiFactory _factory;

    public RunDetailRottedOutputEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task A_run_holding_one_rotted_output_answers_200_with_the_lane_that_failed()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();
        var runId = await SeedRunAsync(teamId);
        await RotAsync(await RecordOffloadedCellAsync(runId, teamId));
        await RecordHealthyCellAsync(runId);

        var response = await SendAsync($"/api/workflows/runs/{runId}", userId, teamId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            customMessage: $"a rotted stored copy is OUR fault, never a malformed request; body: {await response.Content.ReadAsStringAsync()}");

        var nodes = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("nodes").EnumerateArray().ToList();
        nodes.Select(node => node.GetProperty("nodeId").GetString()).ShouldBe(new[] { HealthyNodeId, RottedNodeId }, ignoreOrder: true,
            customMessage: "every cell of the run still reaches the operator");

        var shed = Outputs(nodes, RottedNodeId).GetProperty("body").GetProperty(NodeOutputArtifacts.RefKey);
        shed.GetProperty(NodeOutputArtifacts.ReasonKey).GetString().ShouldBe("IntegrityFailure",
            "the cell that could not be read says WHICH storage lane failed, over the wire, rather than arriving as a bare pointer");
        Outputs(nodes, HealthyNodeId).GetProperty("status").GetInt32().ShouldBe(200, "the healthy cell is untouched by its neighbour's rot");
    }

    private static JsonElement Outputs(IEnumerable<JsonElement> nodes, string nodeId) =>
        nodes.Single(node => node.GetProperty("nodeId").GetString() == nodeId).GetProperty("outputs");

    /// <summary>Appends one cell whose oversize output the store offloads, exactly as the engine's own ledger write does, and returns the artifact behind it.</summary>
    private async Task<Guid> RecordOffloadedCellAsync(Guid runId, Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();

        var value = JsonSerializer.SerializeToElement(new string('b', ArtifactStoreConfig.InlineThresholdBytes * 2));
        var outputs = new Dictionary<string, JsonElement> { ["body"] = value };
        var offloaded = await NodeOutputArtifacts.OffloadLargeAsync(scope.ServiceProvider.GetRequiredService<IArtifactStore>(), teamId, outputs, ArtifactStoreConfig.InlineThresholdBytes, CancellationToken.None);

        NodeOutputArtifacts.IsRef(offloaded["body"]).ShouldBeTrue("precondition: the value is big enough that the ledger holds only a pointer to it");

        await scope.ServiceProvider.GetRequiredService<IRunRecordLogger>()
            .NodeCompletedAsync(runId, RottedNodeId, iterationKey: "", offloaded, routingHints: null, TimeSpan.FromMilliseconds(1), CancellationToken.None);

        return offloaded["body"].GetProperty(NodeOutputArtifacts.RefKey).GetProperty("id").GetGuid();
    }

    /// <summary>A second cell whose small output never left the ledger — the neighbour whose survival is what "the read sheds ONE cell" means.</summary>
    private async Task RecordHealthyCellAsync(Guid runId)
    {
        using var scope = _factory.Services.CreateScope();

        var outputs = new Dictionary<string, JsonElement> { ["status"] = JsonSerializer.SerializeToElement(200) };

        await scope.ServiceProvider.GetRequiredService<IRunRecordLogger>()
            .NodeCompletedAsync(runId, HealthyNodeId, iterationKey: "", outputs, routingHints: null, TimeSpan.FromMilliseconds(1), CancellationToken.None);
    }

    /// <summary>Overwrites the stored object with same-length foreign bytes — the size still matches, so only the digest can catch it.</summary>
    private async Task RotAsync(Guid artifactId)
    {
        using var scope = _factory.Services.CreateScope();

        var row = await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().SingleAsync(a => a.Id == artifactId);
        var path = new Uri(row.StorageUrl!).LocalPath;

        await File.WriteAllBytesAsync(path, new byte[new FileInfo(path).Length]);
    }

    private async Task<Guid> SeedRunAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, WorkflowId = null, SourceType = WorkflowRunSourceTypes.Snapshot,
            ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, WorkflowId = null, WorkflowVersion = null, TeamId = teamId, RunRequestId = requestId,
            SourceType = WorkflowRunSourceTypes.Snapshot, Status = WorkflowRunStatus.Success,
            ScopeRepositoryIds = [], ScopeProjectIds = [], CreatedDate = now,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    private async Task<(Guid UserId, Guid TeamId)> SeedTeamMembershipAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"rotted-{suffix}@test.local", Name = "Rotted", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"rotted-{suffix}", Name = "Rotted", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return (userId, teamId);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, Guid userId, Guid teamId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", teamId.ToString());

        return await _factory.CreateClient().SendAsync(request);
    }
}
