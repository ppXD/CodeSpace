using System.Text.Json;
using Autofac;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Rule-12.5 DRIFT DETECTOR for the real-model planner cassette. The cassette in
/// <see cref="RealModelPhaseAuthorshipFlowTests"/> is keyed on the EXACT planner request the production
/// <c>PlanMapSynthDefinitionBuilder</c> emits (model + system + user prompt + responseSchema). If a human edits
/// that prompt or schema, the <see cref="RecordReplayStructuredLLMClient.CassetteKey"/> moves — and a previously
/// recorded cassette silently stops matching. This test makes that drift LOUD two ways:
///
/// <list type="number">
///   <item>It pins the current key against a committed expected hash (<see cref="ExpectedPlannerKey"/>). A
///   planner prompt/schema change moves the key, FAILS here, and forces a deliberate re-pin + re-record —
///   never a silently-stale cassette. This guard works even before any cassette exists.</item>
///   <item>When a cassette HAS been recorded, it asserts the cassette actually contains an entry for the
///   current key — so an edit that landed without a re-record is caught at the cassette, not just the pin.</item>
/// </list>
///
/// <para>Tagged Integration so it runs in the same CI gate that builds this project (a Unit trait here would run
/// in neither gate — same rationale as <see cref="SubtaskAwareFakeCliDriftTests"/>). The request is derived from
/// the REAL builder via <see cref="PlanMapSynthPlannerRequest"/>, so this is a genuine prod-source pin, not a
/// hand-copied mirror.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PlannerCassetteDriftTests
{
    private readonly PostgresFixture _fixture;

    public PlannerCassetteDriftTests(PostgresFixture fixture) { _fixture = fixture; }

    /// <summary>The run-time planner request for a freshly seeded fixture team — the catalog (part of the prompt, hence the key) renders from the SAME deterministic pool seed every test run uses.</summary>
    private async Task<StructuredLLMCompletionRequest> BuildRequestAsync()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        return await PlanMapSynthPlannerRequest.BuildAsync(scope, teamId, CancellationToken.None);
    }

    /// <summary>
    /// The committed hash of the current plan-map-synth planner request (model + prompts + responseSchema). When
    /// this fails after a planner edit: confirm the edit is intended, update this constant to the new key the
    /// failure prints, then RE-RECORD the cassette via the RealModel live test. The pin is the trip-wire that
    /// makes "I changed the planner prompt but forgot the cassette" a build failure instead of a silent miss.
    /// </summary>
    public const string ExpectedPlannerKey = "184ef80960c78e69cb2e36143716a1894b890603933a108b107967832bfd732c";   // re-pinned: S7 widened the acceptance fragment (non-coding kinds + rubric + schema)

    [Fact]
    public async Task Planner_request_key_is_pinned_so_a_prompt_or_schema_change_forces_a_re_record()
    {
        var key = RecordReplayStructuredLLMClient.CassetteKey(await BuildRequestAsync());

        key.ShouldBe(ExpectedPlannerKey,
            customMessage: $"the plan-map-synth planner prompt/schema changed → cassette key moved to '{key}'. If intended: update ExpectedPlannerKey to that value AND re-record the cassette via the RealModel live test (a stale cassette would now MISS on replay).");
    }

    [Fact]
    public async Task Recorded_cassette_if_present_still_matches_the_current_planner_key()
    {
        if (!RecordReplayStructuredLLMClient.CassetteExists(RealModelCassettePaths.PlannerCassettePath)) return;   // no cassette yet → nothing to drift against

        var key = RecordReplayStructuredLLMClient.CassetteKey(await BuildRequestAsync());
        var entries = JsonSerializer.Deserialize<List<RecordReplayStructuredLLMClient.CassetteEntry>>(File.ReadAllText(RealModelCassettePaths.PlannerCassettePath))!;

        entries.ShouldContain(e => e.KeyHash == key,
            customMessage: "a cassette exists but has NO entry for the current planner key — the planner prompt/schema drifted from what was recorded. Re-record via the RealModel live test.");
    }
}
