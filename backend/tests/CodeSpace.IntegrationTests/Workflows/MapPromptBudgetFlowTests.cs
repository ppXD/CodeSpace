using System.Text.Json;
using System.Text.RegularExpressions;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
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
/// 🟢 Integration: the reduce's INPUT BOUND across the real seam — real Postgres, the real engine, the real
/// <c>flow.map</c> reducer, and the real <c>VariableResolver</c> handing a real downstream node its prompt.
///
/// <para>The unit tier pins the projection as a function. What only this tier can show is the WIRE: that a map
/// which declares a <c>promptBudgetChars</c> PERSISTS the bounded projection into its <c>outputs_jsonb</c>
/// alongside the untouched raw array, and that the downstream step's resolved input is the bounded string — the
/// exact position the plan-map synthesizer occupies, which used to receive the whole array and build a request past
/// the model's context window.</para>
///
/// <para><b>Fidelity (Rule 12) — high on everything the bound touches; the model is deliberately absent.</b> The
/// downstream consumer is <see cref="JsonEmitNode"/> rather than <c>llm.complete</c> because the bound lives
/// entirely UPSTREAM of the model call: what is under test is what the engine puts in the prompt input, not what a
/// provider does with it. Swapping in a real model would test the provider, not this. The plan-map builder's own
/// end of the wire — that its synth binds this key and its map declares the budget — is pinned in
/// <c>PlanMapSynthFanoutFlowTests</c> and the builder unit tests.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Surface", "Engine")]
public class MapPromptBudgetFlowTests
{
    private readonly PostgresFixture _fixture;

    /// <summary>The floor — the narrowest budget the plan normalises to, so the assertions bite at the smallest bound the engine can be given.</summary>
    private const int Budget = MapPlan.MinPromptBudgetChars;

    public MapPromptBudgetFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_oversized_fan_out_reaches_the_downstream_step_bounded_and_declared_as_an_excerpt()
    {
        // Six branches, each echoing a 20K-character element: ~120K characters of results against a 2K budget.
        // Before the bound this array went into the prompt whole.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, BudgetedMapDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: BigElements(6, 20_000));

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the run must COMPLETE — the whole point of the bound is that a wide fan-out no longer dies at the reduce");

        var mapOutputs = JsonDocument.Parse((await NodeAsync(db, runId, "map")).OutputsJson).RootElement;

        mapOutputs.GetProperty("count").GetInt32().ShouldBe(6);

        // The raw array is still emitted — the bound is a SECOND view for the prompt, never a replacement of the
        // structured results everything else reads. At this size the ledger copy is an artifact ref (120K of results
        // is far past the 8 KiB inline threshold), which is itself the point: the projection stays INLINE because it
        // is bounded, so the prompt view never needs the fetch the raw array does.
        mapOutputs.GetProperty("results").TryGetProperty(NodeOutputArtifacts.RefKey, out _).ShouldBeTrue(
            customMessage: "the full array is preserved via the artifact store — the bound must not have shrunk the structured results themselves");

        var projection = mapOutputs.GetProperty(WorkflowOutputKeys.MapResultsPrompt).GetString()!;

        projection.Length.ShouldBeLessThanOrEqualTo(Budget,
            customMessage: $"the persisted projection is {projection.Length} chars against a {Budget}-char budget");
        projection.ShouldStartWith("[EXCERPT — NOT the complete per-subtask results.");
        projection.ShouldContain("of 6 subtask results appear below;");

        // The downstream step — the synthesizer's position — received the BOUNDED string as its prompt input.
        var downstream = await NodeAsync(db, runId, "synth");
        var prompt = JsonDocument.Parse(downstream.InputsJson).RootElement.GetProperty("userPrompt").GetString()!;

        prompt.ShouldContain(projection,
            customMessage: "the resolved input must carry the bounded projection the reducer wrote, not a re-expansion of the array");
        prompt.Length.ShouldBeLessThanOrEqualTo(Budget + 64,
            customMessage: "the prompt is the projection plus its short fixed preamble — nothing may reintroduce the unbounded array");
        Regex.Matches(prompt, @"…\[\d+ of \d+ chars of this subtask result omitted\]…").Count
            .ShouldBeGreaterThan(0, "every branch that had to be shortened says so inline, in the text the model reads");
    }

    [Fact]
    public async Task A_fan_out_inside_the_budget_reaches_the_downstream_step_as_the_plain_serialized_array()
    {
        // The common case. The projection must be the array's own serialization — the same characters the raw-array
        // binding produced before the bound existed — with no notice and no markers anywhere.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, BudgetedMapDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: BigElements(3, 40));

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var mapOutputs = JsonDocument.Parse((await NodeAsync(db, runId, "map")).OutputsJson).RootElement;

        var projection = mapOutputs.GetProperty(WorkflowOutputKeys.MapResultsPrompt).GetString()!;

        projection.ShouldBe(JsonSerializer.Serialize(mapOutputs.GetProperty("results")),
            customMessage: "within budget the projection is the array's own serialization, character for character");
        projection.ShouldNotContain("EXCERPT", customMessage: "nothing was dropped, so nothing may claim it was");
        projection.ShouldNotContain("omitted", customMessage: "an unshortened result must carry no truncation marker");
    }

    [Fact]
    public async Task A_map_that_declares_no_budget_persists_no_projection_at_all()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, BudgetedMapDefinition(budget: null));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: BigElements(3, 20_000));

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var mapOutputs = JsonDocument.Parse((await NodeAsync(db, runId, "map")).OutputsJson).RootElement;

        mapOutputs.TryGetProperty(WorkflowOutputKeys.MapResultsPrompt, out _).ShouldBeFalse(
            "a map that declares no prompt budget must persist exactly the keys it always did — no new column of duplicated results for every existing workflow");
        mapOutputs.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ShouldBe(new[] { "count", "failed", "results" });
        mapOutputs.GetProperty("count").GetInt32().ShouldBe(3);
    }

    // ── fixtures ──

    /// <summary>The downstream step's inputs — the plan-map synth's own shape: a short preamble plus the map's bounded projection, composed from the key constant so the test and the reducer cannot drift.</summary>
    private const string SynthPromptInputsJson =
        "{ \"userPrompt\": \"Per-subtask results:\\n{{nodes.map.outputs." + WorkflowOutputKeys.MapResultsPrompt + "}}\" }";

    /// <summary>A manual-trigger payload of <paramref name="count"/> strings of <paramref name="chars"/> characters each.</summary>
    private static string BigElements(int count, int chars) =>
        JsonSerializer.Serialize(new { things = Enumerable.Range(0, count).Select(i => new string((char)('a' + i), chars)).ToArray() });

    /// <summary>
    /// manual → map(items={{trigger.things}}, promptBudgetChars) → synth[userPrompt binds the map's PROJECTION] → terminal.
    /// The branch body echoes its element, so results[i] is <c>{ "value": "&lt;element&gt;" }</c> — the same shape a real
    /// branch terminal produces, at a size the test controls. <paramref name="budget"/> null omits the key entirely.
    /// </summary>
    private static WorkflowDefinition BudgetedMapDefinition(int? budget = Budget) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map",
                    Config = budget is { } b ? WorkflowsTestSeed.Json($$"""{ "promptBudgetChars": {{b}} }""") : WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "leaf", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "value": "{{item}}" }""") },
            new() { Id = "synth", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json(SynthPromptInputsJson) },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "count": "{{nodes.map.outputs.count}}" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "synth" },
            new() { From = "synth", To = "end" },
            new() { From = "ms", To = "leaf" },
        },
    };

    private static async Task<WorkflowRunNode> NodeAsync(CodeSpaceDbContext db, Guid runId, string nodeId) =>
        await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == nodeId && n.IterationKey == "");

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "mapbudget-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }
}
