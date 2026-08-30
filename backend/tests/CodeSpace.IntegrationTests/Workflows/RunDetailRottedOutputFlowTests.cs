using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The operator contract this slice exists for: a run holding ONE offloaded output whose stored bytes no longer verify
/// is still fully readable. Before, the whole-object read raised a bare <c>InvalidOperationException</c>, the failure
/// classifier read that as the caller's fault, and <c>GET api/workflows/runs/{id}</c> answered 400 — telling an
/// operator their request was malformed, with the sha in the message, for a fault that was entirely ours.
///
/// <para>Through the real engine, the real <c>workflow_run_node</c> view, the real content-addressed store and the real
/// mediator pipeline — everything BELOW the controller, where the outcome is a shed cell rather than a status code. The
/// 400 itself is only visible to a request through the real host, so it is pinned by
/// <c>RunDetailRottedOutputEndpointE2ETests</c> over HTTP; this suite owns the engine-produced run the shed happens to.
/// Rot is staged the way it happens in production: the content-addressed object is overwritten with same-length foreign
/// bytes, which is exactly what the read's own identity check is there to catch.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RunDetailRottedOutputFlowTests
{
    private readonly PostgresFixture _fixture;

    public RunDetailRottedOutputFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_run_with_one_rotted_offloaded_output_still_reads_and_names_the_lane_that_failed()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);

        var artifactId = await RecordOffloadedCellAsync(runId, teamId);
        await RotAsync(artifactId);

        var detail = await ReadRunAsync(userId, teamId, runId);

        detail.ShouldNotBeNull("the run-detail read survives an output whose bytes are no longer the artifact");

        var healthy = detail!.Nodes.Where(node => node.NodeId != RottedNodeId).ToList();
        healthy.Select(node => node.NodeId).OrderBy(id => id, StringComparer.Ordinal).ShouldBe(new[] { "end", "start" });
        healthy.ShouldAllBe(node => node.Outputs.ValueKind == JsonValueKind.Object, "every healthy cell comes back intact");

        var shed = detail.Nodes.Single(node => node.NodeId == RottedNodeId).Outputs.GetProperty("body");
        NodeOutputArtifacts.IsRef(shed).ShouldBeTrue("the rotted cell keeps its pointer rather than costing the reader the run");
        shed.GetProperty(NodeOutputArtifacts.RefKey).GetProperty(NodeOutputArtifacts.ReasonKey).GetString().ShouldBe(
            "IntegrityFailure",
            "the cell says WHICH storage lane failed — 'the stored copy did not match what was recorded' is a different action than 'the destination is down'");
    }

    private const string RottedNodeId = "rotted";

    /// <summary>A real two-cell run, so the assertion that the healthy cells survive is made against cells the engine actually produced.</summary>
    private async Task<Guid> SeedRunAsync(Guid teamId, Guid userId)
    {
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "rotted-output-" + Guid.NewGuid().ToString("N")[..6],
                Description = null,
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);

        return runId;
    }

    /// <summary>Appends one more terminal cell whose oversize output the store offloads, exactly as the engine's own ledger write does.</summary>
    private async Task<Guid> RecordOffloadedCellAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();

        var value = JsonSerializer.SerializeToElement(new string('b', ArtifactStoreConfig.InlineThresholdBytes * 2));
        var outputs = new Dictionary<string, JsonElement> { ["body"] = value };

        var offloaded = await NodeOutputArtifacts.OffloadLargeAsync(scope.Resolve<IArtifactStore>(), teamId, outputs, ArtifactStoreConfig.InlineThresholdBytes, CancellationToken.None);

        NodeOutputArtifacts.IsRef(offloaded["body"]).ShouldBeTrue("precondition: the value is big enough that the ledger holds only a pointer to it");

        await scope.Resolve<IRunRecordLogger>().NodeCompletedAsync(runId, RottedNodeId, iterationKey: "", offloaded, routingHints: null, TimeSpan.FromMilliseconds(1), CancellationToken.None);

        return offloaded["body"].GetProperty(NodeOutputArtifacts.RefKey).GetProperty("id").GetGuid();
    }

    /// <summary>Overwrites the content-addressed object with same-length foreign bytes — the size still matches, so only the digest can catch it.</summary>
    private async Task RotAsync(Guid artifactId)
    {
        using var scope = _fixture.BeginScope();

        var row = await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().SingleAsync(a => a.Id == artifactId);
        var path = new Uri(row.StorageUrl!).LocalPath;

        await File.WriteAllBytesAsync(path, new byte[new FileInfo(path).Length]);
    }

    private async Task<WorkflowRunDetail?> ReadRunAsync(Guid userId, Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);

        return await scope.Resolve<IMediator>().Send(new GetWorkflowRunQuery { RunId = runId });
    }
}
