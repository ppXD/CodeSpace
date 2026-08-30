using System.Text.Json;
using System.Text.Json.Nodes;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The run-detail READ path — <c>WorkflowService.GetRunAsync</c>, the shared read behind the Journal, the Room, the
/// phase board and the run-detail API. It projects the columns it maps instead of materialising entity graphs, and it
/// no longer fetches an offloaded output's blob for every cell: the callers that read an output's CONTENT ask
/// <see cref="IRunNodeOutputInflater"/> for the cells they actually read.
///
/// <para>Integration tier (real Postgres + real <c>ArtifactStore</c> + <c>LocalFileArtifactBlobBackend</c>) over ONE
/// run that exercises every shape the projection has to get right at once: offloaded outputs, inline outputs, a
/// <c>flow.map</c> fan-out with per-branch cells, and a failed node. The expectations are computed from the LEDGER row
/// and the artifact row directly, never from the read under test, so they say what the caller used to get rather than
/// what the new code happens to produce.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RunDetailReadPathFlowTests
{
    private readonly PostgresFixture _fixture;

    public RunDetailReadPathFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    // 32 KiB per subtask title > the 8 KiB inline threshold ⇒ the planner's plan, each branch's echoed element and the
    // map's aggregate all offload, so the run carries FOUR ref-carrying cells for the read to not fetch.
    private const int SubtaskCount = 2;

    /// <summary>
    /// The oversize value each seeded run offloads, made UNIQUE per seed by <paramref name="nonce"/>. The blob backend
    /// is content-addressed by sha256 with no team in the path, so identical bytes from two teams share ONE file on
    /// disk — without the nonce the tamper test below would corrupt the very blob its sibling tests read.
    /// </summary>
    private static string BigTitle(int index, string nonce) => nonce + new string((char)('a' + index), 32 * 1024);

    [Fact]
    public async Task The_projected_detail_matches_the_ledger_for_offloaded_inline_map_and_failed_cells()
    {
        var (teamId, runId, _) = await SeedCompletedRunAsync();

        using var scope = _fixture.BeginScope();
        var detail = await scope.Resolve<IWorkflowService>().GetRunAsync(runId, teamId, CancellationToken.None);

        detail.ShouldNotBeNull();
        detail!.Status.ShouldBe(WorkflowRunStatus.Failure, "the run ends on the failing node — the failed-cell shape is really exercised");

        var ledger = await ReadLedgerCellsAsync(runId);

        detail.Nodes.Count.ShouldBe(ledger.Count, "every cell the ledger view holds is projected — none dropped by the narrower select");

        foreach (var node in detail.Nodes)
        {
            var row = ledger[(node.NodeId, node.IterationKey)];

            node.Status.ShouldBe(row.Status, $"cell {node.NodeId}#{node.IterationKey} status");
            node.Error.ShouldBe(row.Error, $"cell {node.NodeId}#{node.IterationKey} error");
            node.StartedAt.ShouldBe(row.StartedAt);
            node.CompletedAt.ShouldBe(row.CompletedAt);
            JsonMatches(node.Inputs, row.InputsJson).ShouldBeTrue($"cell {node.NodeId}#{node.IterationKey} inputs are the ledger's");
            JsonMatches(node.Outputs, row.OutputsJson).ShouldBeTrue($"cell {node.NodeId}#{node.IterationKey} outputs are the ledger's");
        }

        detail.Nodes.Count(n => n.NodeId == "leaf" && n.ContainerKind == MapFanout.ContainerKind).ShouldBe(SubtaskCount, "one body cell per map element branch, each badged with the container kind the pinned definition gives it");
        detail.Nodes.Single(n => n.NodeId == "boom").Status.ShouldBe(NodeStatus.Failure);
    }

    [Fact]
    public async Task The_on_demand_content_path_returns_the_full_stored_value_for_every_offloaded_cell()
    {
        var (teamId, runId, nonce) = await SeedCompletedRunAsync();

        using var scope = _fixture.BeginScope();
        var detail = await scope.Resolve<IWorkflowService>().GetRunAsync(runId, teamId, CancellationToken.None);
        var inflated = await scope.Resolve<IRunNodeOutputInflater>().InflateAsync(detail!, teamId, CancellationToken.None);

        var expected = await ReadExpectedInflatedOutputsAsync(runId, teamId);

        expected.Values.Count(HasRefSomewhere).ShouldBe(0, "precondition: the expectation resolved every ref from the artifact rows itself");

        foreach (var node in inflated.Nodes)
            JsonMatches(node.Outputs, expected[(node.NodeId, node.IterationKey)].ToJsonString())
                .ShouldBeTrue($"cell {node.NodeId}#{node.IterationKey} inflates to the bytes the artifact row holds");

        var planner = inflated.Nodes.Single(n => n.NodeId == "planner" && n.IterationKey == "");
        planner.Outputs.GetProperty("json").GetProperty("subtasks")[0].GetProperty("title").GetString()
            .ShouldBe(BigTitle(0, nonce), "the caller that reads content gets the whole 32 KiB value back, not a pointer");
    }

    [Fact]
    public async Task The_on_demand_content_path_refuses_bytes_that_no_longer_match_the_stores_identity_claim()
    {
        // The verification stays where it belongs — inside ArtifactStore.GetBytesAsync, on every read. Moving the fetch
        // off the shared read changed WHEN bytes are fetched, never WHETHER they are proven before a caller sees them.
        //
        // The refusal is DISPLAYED rather than thrown: the reader still never receives unverified bytes, but the one
        // cell that rotted no longer costs them the other cells of the run.
        var (teamId, runId, _) = await SeedCompletedRunAsync();

        using var scope = _fixture.BeginScope();
        var detail = await scope.Resolve<IWorkflowService>().GetRunAsync(runId, teamId, CancellationToken.None);

        var tampered = await FlipOneStoredByteAsync(teamId, RefIdsOf(detail!));

        var inflated = await scope.Resolve<IRunNodeOutputInflater>().InflateAsync(detail!, teamId, CancellationToken.None);

        var refused = inflated.Nodes.SelectMany(node => node.Outputs.EnumerateObject())
            .Where(property => ReadRefId(property.Value) == tampered)
            .ToList();

        refused.ShouldNotBeEmpty("the cell whose bytes stopped matching keeps its pointer rather than showing content that is not the artifact");
        refused.ShouldAllBe(property => property.Value.GetProperty(NodeOutputArtifacts.RefKey).GetProperty(NodeOutputArtifacts.ReasonKey).GetString() == "IntegrityFailure",
            "and it names the lane, so a reader knows the copy was refused rather than never recorded");

        inflated.Nodes.SelectMany(node => node.Outputs.EnumerateObject())
            .Any(property => ReadRefId(property.Value) is { } id && id != tampered)
            .ShouldBeFalse("every OTHER offloaded cell still inflated — one rotted blob is not a failed run-detail read");
    }

    [Fact]
    public async Task The_shared_read_fetches_no_artifact_however_many_cells_were_offloaded()
    {
        var (teamId, runId, _) = await SeedCompletedRunAsync();

        var counter = new ArtifactReadCounter();
        using var scope = _fixture.BeginScope(b => b.RegisterDecorator<IArtifactStore>((_, _, inner) => new CountingArtifactStore(inner, counter)));

        var detail = await scope.Resolve<IWorkflowService>().GetRunAsync(runId, teamId, CancellationToken.None);

        var offloadedCells = detail!.Nodes.Count(n => HasRef(n.Outputs));
        offloadedCells.ShouldBeGreaterThan(1, "precondition: several cells carry a ref, so a per-cell fetch would have shown up as several reads");
        counter.Reads.ShouldBe(0, "the shared read fetches no blob at all — its cost no longer scales with the number of offloaded cells");

        // …and the on-demand path costs exactly what the caller asked for: one named node, one fetch.
        await scope.Resolve<IRunNodeOutputInflater>().InflateAsync(detail, teamId, new HashSet<string>(new[] { "planner" }, StringComparer.Ordinal), CancellationToken.None);

        counter.Reads.ShouldBe(1, "the map-plan caller names one node and pays one fetch, not one per cell");
    }

    /// <summary>Runs the offload/map/failure workflow to completion and returns its team + run id, plus the nonce its oversize values carry.</summary>
    private async Task<(Guid TeamId, Guid RunId, string Nonce)> SeedCompletedRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var nonce = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new { plan = new { subtasks = Enumerable.Range(0, SubtaskCount).Select(i => new { id = $"s{i}", title = BigTitle(i, nonce) }).ToArray() } });
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: payload);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);

        return (teamId, runId, nonce);
    }

    /// <summary>
    /// Rewrites ONE byte of ONE offloaded blob the detail's cells point at — same length, same
    /// <c>workflow_artifact</c> row — and returns that artifact's id.
    ///
    /// <para>The tamper has to land on the BLOB rather than the row: <c>workflow_artifact</c> carries a BEFORE UPDATE
    /// trigger (<c>0016_workflow_artifact.sql</c>) that raises on every UPDATE, so a row whose sha/size disagrees with
    /// its bytes is a state Postgres will not hold. Corrupting the file under the content-addressed path the store
    /// itself recorded in <c>storage_url</c> is also the REAL failure this check exists for — a foreign write, a
    /// truncation, a half-restored mount. Same length is deliberate: <c>GetBytesAsync</c>'s
    /// <c>bytes.Length != row.SizeBytes</c> pre-check cannot see it, so ONLY the sha256 comparison can, and deleting
    /// that comparison turns this test red.</para>
    /// </summary>
    private async Task<Guid> FlipOneStoredByteAsync(Guid teamId, IReadOnlyCollection<Guid> refIds)
    {
        using var scope = _fixture.BeginScope();

        var offloaded = await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking()
            .Where(a => a.TeamId == teamId && refIds.Contains(a.Id) && a.StorageUrl != null)
            .Select(a => new { a.Id, a.StorageUrl })
            .FirstOrDefaultAsync();

        offloaded.ShouldNotBeNull("precondition: a projected cell's ref points at an OFFLOADED artifact, so there is a blob to corrupt");

        var path = new Uri(offloaded.StorageUrl!).LocalPath;
        var bytes = await File.ReadAllBytesAsync(path);

        bytes.Length.ShouldBeGreaterThan(0, "precondition: the blob the store recorded is really on disk");
        bytes[^1] ^= 0xFF;

        await File.WriteAllBytesAsync(path, bytes);

        return offloaded.Id;
    }

    /// <summary>Every artifact id the projected cells' outputs point at — exactly the set a whole-run inflation fetches.</summary>
    private static IReadOnlyCollection<Guid> RefIdsOf(WorkflowRunDetail detail) =>
        detail.Nodes
            .Where(node => node.Outputs.ValueKind == JsonValueKind.Object)
            .SelectMany(node => node.Outputs.EnumerateObject())
            .Select(property => ReadRefId(property.Value))
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

    /// <summary>The ledger view's own rows — the source of truth the projection must not diverge from.</summary>
    private async Task<Dictionary<(string NodeId, string IterationKey), LedgerCell>> ReadLedgerCellsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunNode
            .Where(n => n.RunId == runId)
            .Select(n => new LedgerCell(n.NodeId, n.IterationKey, n.Status, n.InputsJson, n.OutputsJson, n.Error, n.StartedAt, n.CompletedAt))
            .ToDictionaryAsync(n => (n.NodeId, n.IterationKey));
    }

    /// <summary>
    /// What each cell's outputs looked like BEFORE the offload — rebuilt from the LEDGER row by replacing every ref
    /// with the bytes the store holds for it. Built from the store's own public read rather than from the run-detail
    /// path under test, so this is a real differential and not the code agreeing with itself.
    /// </summary>
    private async Task<Dictionary<(string NodeId, string IterationKey), JsonNode>> ReadExpectedInflatedOutputsAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<IArtifactStore>();

        var expected = new Dictionary<(string, string), JsonNode>();

        foreach (var cell in (await ReadLedgerCellsAsync(runId)).Values)
        {
            var outputs = JsonNode.Parse(cell.OutputsJson)!.AsObject();

            foreach (var property in outputs.ToList())
            {
                if (ReadRefId(property.Value) is not { } artifactId) continue;

                var artifact = await store.GetBytesAsync(teamId, artifactId, CancellationToken.None);

                outputs[property.Key] = JsonNode.Parse(artifact!.Bytes);
            }

            expected[(cell.NodeId, cell.IterationKey)] = outputs;
        }

        return expected;
    }

    private static Guid? ReadRefId(JsonElement value) =>
        NodeOutputArtifacts.IsRef(value) && value.GetProperty(NodeOutputArtifacts.RefKey).TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), out var parsed)
            ? parsed
            : null;

    private static Guid? ReadRefId(JsonNode? value) =>
        value is JsonObject obj && obj.TryGetPropertyValue(NodeOutputArtifacts.RefKey, out var refObj)
        && refObj is JsonObject reference && reference.TryGetPropertyValue("id", out var id) && Guid.TryParse(id?.GetValue<string>(), out var parsed)
            ? parsed
            : null;

    private static bool HasRef(JsonElement outputs) =>
        outputs.ValueKind == JsonValueKind.Object && outputs.EnumerateObject().Any(p => NodeOutputArtifacts.IsRef(p.Value));

    private static bool HasRefSomewhere(JsonNode node) => node.ToJsonString().Contains(NodeOutputArtifacts.RefKey, StringComparison.Ordinal);

    private static bool JsonMatches(JsonElement actual, string? expectedRaw) =>
        JsonNode.DeepEquals(JsonNode.Parse(actual.GetRawText()), JsonNode.Parse(string.IsNullOrWhiteSpace(expectedRaw) ? "{}" : expectedRaw));

    private sealed record LedgerCell(string NodeId, string IterationKey, NodeStatus Status, string InputsJson, string OutputsJson, string? Error, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);

    // start → planner(echoes the oversize plan) → map(fans out the plan's subtasks; body echoes each element)
    //       → boom(always fails) → terminal. Yields offloaded + inline outputs, map branches and a failed cell in ONE run.
    private static WorkflowDefinition ReadPathDefinition(string flakyKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "planner", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "json": "{{trigger.plan}}" }""") },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{nodes.planner.outputs.json.subtasks}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "leaf", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "value": "{{item}}" }""") },
            new() { Id = "boom", TypeKey = FlakyTestNode.Key, Config = WorkflowsTestSeed.Json($$"""{ "key": "{{flakyKey}}", "failTimes": 99 }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "planner" },
            new() { From = "planner", To = "map" },
            new() { From = "map", To = "boom" },
            new() { From = "boom", To = "end" },
            new() { From = "ms", To = "leaf" },
        },
    };

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);

        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "read-path-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = ReadPathDefinition("read-path-" + Guid.NewGuid().ToString("N")[..8]),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private sealed class ArtifactReadCounter
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public void Increment() => Interlocked.Increment(ref _reads);
    }

    /// <summary>Counts byte reads while delegating to the REAL store, so the assertion is about the read path's behaviour, not a stub's.</summary>
    private sealed class CountingArtifactStore : IArtifactStore
    {
        private readonly IArtifactStore _inner;
        private readonly ArtifactReadCounter _counter;

        public CountingArtifactStore(IArtifactStore inner, ArtifactReadCounter counter)
        {
            _inner = inner;
            _counter = counter;
        }

        public Task<Guid> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken) =>
            _inner.PutAsync(teamId, bytes, contentType, cancellationToken);

        public Task<ArtifactBytes?> GetBytesAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken)
        {
            _counter.Increment();
            return _inner.GetBytesAsync(teamId, artifactId, cancellationToken);
        }

        public Task<ArtifactMetadata?> GetMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) =>
            _inner.GetMetadataAsync(teamId, artifactId, cancellationToken);
    }
}
