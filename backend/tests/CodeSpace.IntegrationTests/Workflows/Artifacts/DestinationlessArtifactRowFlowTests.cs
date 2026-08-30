using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// The read's last untyped exit: a row that names NO destination — no inline bytes, no <c>storage_url</c>, no routed
/// object. Everything else the whole-object read can meet now sheds onto the pointer; this one threw a bare
/// <c>InvalidOperationException</c> past every catch on the way out, so one such row answered an operator's whole
/// run-detail read with a failure instead of costing them the single cell it belongs to.
///
/// <para>ONE row, read through BOTH of the store's readers, because each reaches the missing destination through its
/// own guard. Typing only the whole-object one left the bounded read still calling the identical row state an
/// integrity failure — two verdicts for one physical fact, and nothing that would have reported the disagreement. The
/// cross-assert at the end of the test is that missing detector.</para>
///
/// <para>Staged the only way it can be: the three-way <c>workflow_artifact_storage_xor</c> CHECK forbids the state on
/// every INSERT, so the constraint is lifted for exactly one row and restored from the catalog's own definition — the
/// same NOT VALID form migration 0136 adds, which is why the row survives the restore. That NOT VALID is precisely why
/// the branch is worth typing rather than deleting: the constraint has never been validated against the table, so it
/// is the app, not the database, that has to stay honest if a backfill ever writes one.</para>
///
/// <para>Tier: 🟢 High-fidelity — real Postgres with the real schema, the real content-addressed store, and the real
/// display-read inflater. Only the CHECK is out of the way, and only long enough to write the row.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DestinationlessArtifactRowFlowTests
{
    private const string StorageXorConstraint = "workflow_artifact_storage_xor";

    private readonly PostgresFixture _fixture;

    public DestinationlessArtifactRowFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_row_naming_no_destination_sheds_like_every_other_unreadable_artifact()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var healthy = await OffloadAsync(teamId, new string('h', ArtifactStoreConfig.InlineThresholdBytes * 2));
        var destinationless = await InsertRowNamingNoDestinationAsync(teamId);

        var run = RunWith(Cell("planner", healthy.Outputs), Cell("agent", OutputsPointingAt(destinationless)));

        using var scope = _fixture.BeginScope();
        var inflated = await scope.Resolve<IRunNodeOutputInflater>().InflateAsync(run, teamId, CancellationToken.None);

        inflated.Nodes[0].Outputs.GetProperty("body").GetString().ShouldBe(healthy.Value,
            "a neighbour's row having lost its destination is not this cell's fact — the reader keeps the whole rest of the run");

        var shed = inflated.Nodes[1].Outputs.GetProperty("body");
        NodeOutputArtifacts.IsRef(shed).ShouldBeTrue("the cell keeps its pointer rather than costing the reader the run detail");

        var wholeObjectLane = shed.GetProperty(NodeOutputArtifacts.RefKey).GetProperty(NodeOutputArtifacts.ReasonKey).GetString();
        wholeObjectLane.ShouldBe(nameof(ArtifactContentUnavailableKind.MetadataMissing),
            "a row that names nowhere is missing the metadata that says where its bytes went — the same lane a row that is gone entirely reports");

        var bounded = await scope.Resolve<IArtifactRangeReader>().ReadRangeAsync(teamId, destinationless, 0, 4096, CancellationToken.None);

        bounded.State.ToString().ShouldBe(wholeObjectLane,
            "ONE physical row, TWO readers: each reaches the missing destination through its own throw-site, so typing only one of them leaves the two disagreeing about the same fact — worse than both being wrong the same way, because nothing says they drifted");
    }

    /// <summary>One oversize output property put through the REAL store, so the surviving neighbour is a pointer that has to come back out of storage.</summary>
    private async Task<OffloadedCell> OffloadAsync(Guid teamId, string value)
    {
        using var scope = _fixture.BeginScope();

        var outputs = new Dictionary<string, JsonElement> { ["body"] = JsonSerializer.SerializeToElement(value) };
        var offloaded = await NodeOutputArtifacts.OffloadLargeAsync(scope.Resolve<IArtifactStore>(), teamId, outputs, ArtifactStoreConfig.InlineThresholdBytes, CancellationToken.None);

        NodeOutputArtifacts.IsRef(offloaded["body"]).ShouldBeTrue("precondition: the neighbour is a pointer too, so a shed granularity coarser than one property would take it with the other cell");

        return new OffloadedCell(JsonSerializer.SerializeToElement(offloaded), value);
    }

    /// <summary>
    /// Writes the row the CHECK forbids, with the constraint restored verbatim from <c>pg_get_constraintdef</c> rather
    /// than re-spelled here — a copy of the predicate in a test is a copy that drifts from the migration silently.
    /// </summary>
    private async Task<Guid> InsertRowNamingNoDestinationAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();

        var db = scope.Resolve<CodeSpaceDbContext>();
        var artifactId = Guid.NewGuid();
        var definition = await db.Database.SqlQueryRaw<string>(
            $"""SELECT pg_get_constraintdef(oid) AS "Value" FROM pg_constraint WHERE conname = '{StorageXorConstraint}'""").SingleAsync();

        await db.Database.ExecuteSqlRawAsync($"ALTER TABLE workflow_artifact DROP CONSTRAINT {StorageXorConstraint}");
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO workflow_artifact (id, team_id, sha256, content_type, size_bytes) VALUES ({0}, {1}, {2}, 'application/json', 4096)",
                artifactId, teamId, new string('a', 64));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE workflow_artifact ADD CONSTRAINT {StorageXorConstraint} {definition}");
        }

        return artifactId;
    }

    /// <summary>The ledger shape for one offloaded property — the pointer alone, exactly as it survives in <c>outputs_jsonb</c> after the value moved out.</summary>
    private static JsonElement OutputsPointingAt(Guid artifactId) => JsonSerializer.SerializeToElement(new Dictionary<string, object>
    {
        ["body"] = new Dictionary<string, object> { [NodeOutputArtifacts.RefKey] = new { id = artifactId, size_bytes = 4096, content_type = "application/json" } },
    });

    private sealed record OffloadedCell(JsonElement Outputs, string Value);

    private static WorkflowRunNodeSummary Cell(string nodeId, JsonElement outputs) => new()
    {
        NodeId = nodeId, IterationKey = "", Status = NodeStatus.Success, Inputs = Empty, Outputs = outputs,
        StartedAt = DateTimeOffset.UnixEpoch, CompletedAt = DateTimeOffset.UnixEpoch, RerunnableFromHere = false,
    };

    private static WorkflowRunDetail RunWith(params WorkflowRunNodeSummary[] nodes) => new()
    {
        Id = Guid.NewGuid(), RunNumber = 1, SourceType = "manual", NormalizedPayload = Empty,
        Status = WorkflowRunStatus.Success, CreatedDate = DateTimeOffset.UnixEpoch, Nodes = nodes, Outputs = Empty,
    };

    private static JsonElement Empty => JsonDocument.Parse("{}").RootElement.Clone();
}
