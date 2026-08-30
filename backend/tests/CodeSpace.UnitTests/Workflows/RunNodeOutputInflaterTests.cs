using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the ON-DEMAND re-inflation of offloaded node outputs (<see cref="IRunNodeOutputInflater"/>) — the seam that
/// let <c>WorkflowService.GetRunAsync</c> stop fetching a whole blob per offloaded cell on a read the Journal walk
/// performs several times a turn while needing at most ONE cell's content (a map's plan) and, beyond it, only
/// top-level scalars that per-property offload never reaches.
///
/// <para>Pins the three properties the move depends on: a caller that asks for content still GETS it; a caller that
/// asks for ONE node pays ONE fetch, not one per cell; and everything not inflated comes back byte-identical. The last
/// test is the reason the fetch existed at all — a map's plan lives in its producer cell's outputs, and a plan large
/// enough to be offloaded reads as no plan at all off the bare detail. A hermetic in-memory store that COUNTS its reads
/// stands in for the real content-addressed <c>IArtifactStore</c>.</para>
/// </summary>
[Trait("Category", "Unit")]
public class RunNodeOutputInflaterTests
{
    [Fact]
    public async Task Inflating_a_cell_replaces_its_ref_with_the_stored_content()
    {
        var store = new CountingArtifactStore();
        var teamId = Guid.NewGuid();
        var plan = """{ "subtasks": [{ "id": "s1", "title": "First" }] }""";
        var run = RunWith(Cell("planner", await OffloadedOutputsAsync(store, teamId, "json", plan)));

        var inflated = await Inflater(store).InflateAsync(run, teamId, CancellationToken.None);

        NodeOutputArtifacts.IsRef(inflated.Nodes[0].Outputs.GetProperty("json")).ShouldBeFalse("the pointer was exchanged for the value");
        inflated.Nodes[0].Outputs.GetProperty("json").GetProperty("subtasks").GetArrayLength().ShouldBe(1, "the caller that needs the content gets the real content");
        store.Reads.ShouldBe(1);
    }

    [Fact]
    public async Task A_scoped_inflation_fetches_only_the_named_nodes_cells()
    {
        // The property that takes the read off its N+1: the map-plan sources read ONE node's outputs, so they pay for
        // one fetch no matter how many other cells of the run were offloaded.
        var store = new CountingArtifactStore();
        var teamId = Guid.NewGuid();
        var run = RunWith(
            Cell("planner", await OffloadedOutputsAsync(store, teamId, "json", """{ "subtasks": [] }""")),
            Cell("agent-a", await OffloadedOutputsAsync(store, teamId, "text", "\"a\"")),
            Cell("agent-b", await OffloadedOutputsAsync(store, teamId, "text", "\"b\"")));
        store.ResetReads();

        var inflated = await Inflater(store).InflateAsync(run, teamId, Ids("planner"), CancellationToken.None);

        store.Reads.ShouldBe(1, "three cells carry a ref; the caller named one, so exactly one blob was fetched");
        NodeOutputArtifacts.IsRef(inflated.Nodes[0].Outputs.GetProperty("json")).ShouldBeFalse("the named cell was inflated");
        NodeOutputArtifacts.IsRef(inflated.Nodes[1].Outputs.GetProperty("text")).ShouldBeTrue("an unnamed cell keeps its pointer — it was never fetched");
    }

    [Fact]
    public async Task A_run_with_no_offloaded_output_is_returned_untouched_and_reads_nothing()
    {
        var store = new CountingArtifactStore();
        var run = RunWith(Cell("emit", Json("""{ "body": "small" }""")), Cell("end", Json("""{ "final": 1 }""")));

        var inflated = await Inflater(store).InflateAsync(run, Guid.NewGuid(), CancellationToken.None);

        store.Reads.ShouldBe(0, "nothing is offloaded, so nothing is fetched");
        inflated.ShouldBeSameAs(run, "an unaffected run is not rewritten at all");
    }

    [Fact]
    public async Task An_inline_cell_beside_an_offloaded_one_comes_back_byte_identical()
    {
        var store = new CountingArtifactStore();
        var teamId = Guid.NewGuid();
        var inline = Cell("emit", Json("""{ "body": "small", "status": 200 }"""), NodeStatus.Failure, error: "boom");
        var run = RunWith(inline, Cell("big", await OffloadedOutputsAsync(store, teamId, "body", "\"xxxxx\"")));

        var inflated = await Inflater(store).InflateAsync(run, teamId, CancellationToken.None);

        inflated.Nodes[0].ShouldBe(inline, "a cell with nothing to inflate is the SAME summary — status, error, timings and outputs all untouched");
        inflated.Nodes.Count.ShouldBe(run.Nodes.Count);
        inflated.Id.ShouldBe(run.Id);
    }

    [Fact]
    public async Task A_missing_artifact_leaves_the_ref_verbatim_rather_than_dropping_the_value()
    {
        var store = new CountingArtifactStore();
        var teamId = Guid.NewGuid();
        var run = RunWith(Cell("planner", await OffloadedOutputsAsync(store, teamId, "json", """{ "subtasks": [] }""")));

        // A different team cannot see the artifact — the structure must survive, not vanish.
        var inflated = await Inflater(store).InflateAsync(run, Guid.NewGuid(), CancellationToken.None);

        NodeOutputArtifacts.IsRef(inflated.Nodes[0].Outputs.GetProperty("json")).ShouldBeTrue("fail-safe: an unreadable ref is kept, never silently emptied");
    }

    [Fact]
    public async Task One_rotted_cell_does_not_cost_the_reader_the_rest_of_the_run()
    {
        // The defect this pins: ONE offloaded output whose bytes no longer verify used to take the whole run-detail
        // read down with it. A reader inspecting a 40-step run lost all 40 steps because one of them rotted.
        var store = new CountingArtifactStore();
        var teamId = Guid.NewGuid();
        var healthy = Cell("planner", await OffloadedOutputsAsync(store, teamId, "json", """{ "subtasks": [] }"""));
        var rotted = Cell("agent", await OffloadedOutputsAsync(store, teamId, "text", "\"transcript\""));
        var run = RunWith(healthy, rotted);
        store.FailRead(ArtifactIdOf(rotted, "text"), ArtifactContentUnavailableKind.IntegrityFailure);

        var inflated = await Inflater(store).InflateAsync(run, teamId, CancellationToken.None);

        inflated.Nodes[0].Outputs.GetProperty("json").GetProperty("subtasks").GetArrayLength().ShouldBe(0, "the healthy cell is inflated as it always was");
        var shed = inflated.Nodes[1].Outputs.GetProperty("text");
        NodeOutputArtifacts.IsRef(shed).ShouldBeTrue("the rotted cell keeps its pointer rather than failing the read");
        shed.GetProperty(NodeOutputArtifacts.RefKey).GetProperty(NodeOutputArtifacts.ReasonKey).GetString()
            .ShouldBe(nameof(ArtifactContentUnavailableKind.IntegrityFailure), "and the pointer says which storage lane failed");
    }

    [Fact]
    public async Task An_offloaded_plan_is_only_readable_as_a_plan_after_the_scoped_inflation()
    {
        // THE reason the run-detail read fetched blobs at all: a flow.map's plan is read off its producer cell's
        // outputs, so an oversize plan sitting behind a ref reads as "no plan" and the plan beat silently disappears.
        var store = new CountingArtifactStore();
        var teamId = Guid.NewGuid();
        var plan = """{ "subtasks": [{ "id": "s1", "title": "First" }, { "id": "s2", "title": "Second" }] }""";
        var run = MapRunWith(await OffloadedOutputsAsync(store, teamId, "json", plan));

        MapPlan.PlannersOf(run).ShouldBeEmpty("precondition: behind a ref, the plan is unreadable — this is what the fetch was protecting");

        var planned = await Inflater(store).InflateAsync(run, teamId, MapPlan.ProducerNodeIds(run), CancellationToken.None);

        MapPlan.PlannersOf(planned).Single().Subtasks.GetArrayLength().ShouldBe(2, "the on-demand fetch restores exactly what the shared read used to hand this caller");
    }

    private static IReadOnlySet<string> Ids(params string[] nodeIds) => nodeIds.ToHashSet(StringComparer.Ordinal);

    private static RunNodeOutputInflater Inflater(IArtifactStore store) => new(store, NullLogger<RunNodeOutputInflater>.Instance);

    private static Guid ArtifactIdOf(WorkflowRunNodeSummary node, string key) =>
        node.Outputs.GetProperty(key).GetProperty(NodeOutputArtifacts.RefKey).GetProperty("id").GetGuid();

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    /// <summary>Outputs holding ONE property whose value was offloaded to <paramref name="store"/> — the exact shape the engine writes to the ledger for an oversize value.</summary>
    private static async Task<JsonElement> OffloadedOutputsAsync(IArtifactStore store, Guid teamId, string key, string rawValue)
    {
        var outputs = new Dictionary<string, JsonElement> { [key] = Json(rawValue) };

        var offloaded = await NodeOutputArtifacts.OffloadLargeAsync(store, teamId, outputs, thresholdBytes: 1, CancellationToken.None);

        return JsonSerializer.SerializeToElement(offloaded);
    }

    private static WorkflowRunNodeSummary Cell(string nodeId, JsonElement outputs, NodeStatus status = NodeStatus.Success, string? error = null) => new()
    {
        NodeId = nodeId,
        IterationKey = "",
        Status = status,
        Inputs = Json("{}"),
        Outputs = outputs,
        Error = error,
        StartedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        RerunnableFromHere = false,
    };

    private static WorkflowRunDetail RunWith(params WorkflowRunNodeSummary[] nodes) => new()
    {
        Id = Guid.NewGuid(),
        RunNumber = 1,
        SourceType = "manual",
        NormalizedPayload = Json("{}"),
        Status = WorkflowRunStatus.Success,
        CreatedDate = DateTimeOffset.UnixEpoch,
        Nodes = nodes,
        Outputs = Json("{}"),
    };

    /// <summary>A run whose <c>flow.map</c> fans out over <c>{{nodes.planner.outputs.json.subtasks}}</c>, with one element branch so the map is recognised as one.</summary>
    private static WorkflowRunDetail MapRunWith(JsonElement plannerOutputs)
    {
        var branch = Cell("leaf", Json("{}")) with { IterationKey = "map#0", ContainerKind = MapFanout.ContainerKind };

        return RunWith(Cell("planner", plannerOutputs), Cell("map", Json("{}")), branch) with
        {
            Definition = new WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<NodeDefinition>
                {
                    new() { Id = "planner", TypeKey = "llm.complete", Config = Json("{}"), Inputs = Json("{}") },
                    new() { Id = "map", TypeKey = MapFanout.ContainerKind, Config = Json("{}"), Inputs = Json("""{ "items": "{{nodes.planner.outputs.json.subtasks}}" }""") },
                },
                Edges = new List<EdgeDefinition> { new() { From = "planner", To = "map" } },
            },
        };
    }

    /// <summary>Hermetic content-addressed store that COUNTS its byte reads — the assertion surface for "this path no longer fetches per cell" — and can be told that ONE artifact's bytes no longer verify, which is what a rotted destination looks like from here.</summary>
    private sealed class CountingArtifactStore : IArtifactStore
    {
        private readonly ConcurrentDictionary<(Guid Team, string Sha), Guid> _idByContent = new();
        private readonly ConcurrentDictionary<(Guid Team, Guid Id), (string Sha, byte[] Bytes, string ContentType)> _byId = new();
        private readonly ConcurrentDictionary<Guid, ArtifactContentUnavailableKind> _rotted = new();
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public void ResetReads() => Volatile.Write(ref _reads, 0);

        public void FailRead(Guid artifactId, ArtifactContentUnavailableKind kind) => _rotted[artifactId] = kind;

        public Task<Guid> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken)
        {
            var sha = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();

            if (_idByContent.TryGetValue((teamId, sha), out var existing)) return Task.FromResult(existing);

            var id = Guid.NewGuid();
            _idByContent[(teamId, sha)] = id;
            _byId[(teamId, id)] = (sha, bytes.ToArray(), contentType);
            return Task.FromResult(id);
        }

        public Task<ArtifactBytes?> GetBytesAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);

            if (_rotted.TryGetValue(artifactId, out var kind))
                return Task.FromException<ArtifactBytes?>(new ArtifactContentUnavailableException(artifactId, kind));

            return Task.FromResult(_byId.TryGetValue((teamId, artifactId), out var row)
                ? new ArtifactBytes { Id = artifactId, Sha256 = row.Sha, ContentType = row.ContentType, Bytes = row.Bytes }
                : null);
        }

        public Task<ArtifactMetadata?> GetMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) =>
            Task.FromResult(_byId.TryGetValue((teamId, artifactId), out var row)
                ? new ArtifactMetadata { Id = artifactId, Sha256 = row.Sha, ContentType = row.ContentType, SizeBytes = row.Bytes.Length, CreatedAt = DateTimeOffset.UnixEpoch }
                : null);
    }
}
