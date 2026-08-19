using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the reduce's INPUT BOUND — <see cref="MapResultsPrompt"/>, the projection a downstream synthesizer is
/// handed instead of a <c>flow.map</c>'s raw result array, plus <see cref="MapPlan"/>'s normalisation of the budget
/// that switches it on.
///
/// <para>Four invariants, one test each, because each is a way the bound could be dishonest rather than merely
/// imperfect: it must never hand the model more than the budget (at ANY branch count or branch size); it must SAY
/// when what the model holds is an excerpt; a single pathological branch must not be able to spend the whole budget
/// and evict its siblings; and the ordinary within-budget fan-out must reach the model byte-for-byte unchanged —
/// verified through the REAL <see cref="VariableResolver"/>, against the exact template the builder used to emit,
/// so "unchanged" is measured on the wire and not asserted about the helper.</para>
///
/// <para>Pure — no clock, no env, no database.</para>
/// </summary>
[Trait("Category", "Unit")]
public class MapResultsPromptTests
{
    private const int Budget = 8_000;

    [Fact]
    public void Within_budget_the_projection_is_the_unbounded_serialization_character_for_character()
    {
        var results = Results("did the first thing", "did the second thing", "did the third thing");

        var projected = MapResultsPrompt.Project(results, Budget);

        // The ordinary fan-out: the model must see EXACTLY what the raw-array binding produced — the same call
        // VariableResolver's array arm makes on the same element. Not "equivalent JSON": the same characters.
        projected.ShouldBe(JsonSerializer.Serialize(results),
            customMessage: "a fan-out inside the budget must not be reshaped at all — the bound may only bind when it binds");
    }

    [Fact]
    public void A_zero_budget_disables_the_bound_entirely()
    {
        var results = Results(new string('x', 50_000));

        MapResultsPrompt.Project(results, 0).ShouldBe(JsonSerializer.Serialize(results),
            customMessage: "budget <= 0 means no bound — every map that declares none keeps its pre-existing output");
    }

    [Theory]
    [InlineData(3, 40, 2_000)]              // under budget — the projection is the whole array
    [InlineData(1, 1_000_000, 2_000)]       // ONE branch far larger than the entire budget
    [InlineData(10, 200_000, 8_000)]        // ten branches each larger than the budget
    [InlineData(10_000, 100, 2_000)]        // the map's branch ceiling, at the smallest budget the plan allows
    [InlineData(500, 5_000, 20_000)]
    [InlineData(2, 300, 2_000)]
    public void Never_hands_the_model_more_than_the_budget_at_any_branch_count_or_branch_size(int branches, int branchChars, int budget)
    {
        var results = Results(Enumerable.Range(0, branches).Select(i => new string((char)('a' + i % 26), branchChars)).ToArray());

        var projected = MapResultsPrompt.Project(results, budget);

        projected.Length.ShouldBeLessThanOrEqualTo(budget,
            customMessage: $"{branches} branches of {branchChars} chars projected to {projected.Length} chars against a {budget}-char budget — the synthesizer is handed more than its bound");
    }

    [Fact]
    public void Over_budget_the_projection_opens_by_saying_it_is_an_excerpt_and_names_how_many_results_are_present()
    {
        var results = Results(Enumerable.Range(0, 40).Select(_ => new string('y', 4_000)).ToArray());

        var projected = MapResultsPrompt.Project(results, Budget);

        projected.ShouldStartWith("[EXCERPT — NOT the complete per-subtask results.",
            customMessage: "the caveat must lead the prompt — a model that reads the data first has already treated a part as the whole");
        projected.ShouldContain(" of 40 subtask results appear below;",
            customMessage: "the notice must name how many of how many results the model actually holds");
        projected.ShouldContain("are absent entirely",
            customMessage: "silently dropping branches is the defect, not the fix — the count that is missing must be stated");
    }

    /// <summary>
    /// Nine small results and one enormous one. Fair share means the nine keep their FULL text and the giant is cut
    /// back to its own slice. Parameterised by WHERE the giant sits, because position is exactly what separates a
    /// real bound from a first-come one: spending the budget in array order also leaves the nine intact when the
    /// giant is last, and evicts every one of them when it is first.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(9)]
    public void A_single_pathological_branch_cannot_spend_the_budget_its_siblings_need(int pathologicalIndex)
    {
        var summaries = Enumerable.Range(0, 10)
            .Select(i => i == pathologicalIndex ? new string('z', 400_000) : $"small result {i}")
            .ToArray();
        var results = Results(summaries);

        var projected = MapResultsPrompt.Project(results, Budget);

        foreach (var (element, i) in results.EnumerateArray().Select((e, i) => (e, i)).Where(x => x.i != pathologicalIndex))
            projected.ShouldContain(JsonSerializer.Serialize(element),
                customMessage: $"small sibling {i} must survive in FULL — one oversized branch may not evict it, wherever the oversized branch sits");

        CountBranchMarkers(projected).ShouldBe(1,
            customMessage: "the oversized branch is the one that gets cut, and the cut is marked where it happened");
    }

    [Fact]
    public void A_branch_that_had_to_be_shortened_carries_its_own_inline_marker()
    {
        var results = Results(new string('q', 100_000), new string('r', 100_000));

        var projected = MapResultsPrompt.Project(results, Budget);

        CountBranchMarkers(projected).ShouldBe(2,
            customMessage: "a shortened branch must say so WHERE it was shortened — an unmarked cut reads as the branch's whole output");
    }

    [Fact]
    public void A_fan_out_too_wide_to_represent_shows_fewer_branches_rather_than_slivers_of_all_of_them()
    {
        var results = Results(Enumerable.Range(0, 200).Select(i => $"result {i} " + new string('w', 500)).ToArray());

        var projected = MapResultsPrompt.Project(results, Budget);

        // 200 equal slices of an 8K budget would be ~38 useless characters each. The floor shows a readable subset
        // and declares the remainder absent instead.
        var shown = int.Parse(projected["[EXCERPT — NOT the complete per-subtask results. ".Length..].Split(' ')[0]);

        shown.ShouldBeLessThan(200, "a budget that cannot hold 200 readable slices must show fewer branches, not 200 slivers");
        shown.ShouldBeGreaterThan(0, "at least one branch must reach the model");
        projected.ShouldContain($"{200 - shown} are absent entirely");
    }

    [Fact]
    public void The_within_budget_prompt_resolves_identically_to_the_raw_array_binding_it_replaced()
    {
        // The end-to-end byte-identity claim, measured where it matters: through the REAL resolver, comparing the
        // template the builder emitted BEFORE this bound existed against the one it emits now, over the same map
        // outputs. If the projection ever reshapes an ordinary fan-out, these two strings diverge.
        var results = new List<JsonElement>
        {
            Obj("""{"status":"Succeeded","summary":"renamed the module","changedFiles":["a.cs","b.cs"]}"""),
            Obj("""{"status":"Succeeded","summary":"added the tests","changedFiles":["c.cs"]}"""),
        };

        var outputs = WorkflowEngine.BuildMapOutputs("results", results, failed: 0, promptBudgetChars: Budget);
        var scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() };
        scope.Nodes["map"] = outputs;

        var before = Resolve("Goal: G\n\nPer-subtask results:\n{{nodes.map.outputs.results}}", scope);
        var after = Resolve($"Goal: G\n\nPer-subtask results:\n{{{{nodes.map.outputs.{WorkflowOutputKeys.MapResultsPrompt}}}}}", scope);

        after.ShouldBe(before,
            customMessage: "the plan-map reduce prompt must be unchanged for an ordinary fan-out — this is the whole no-meaning-change guarantee");
    }

    // ── MapPlan: the budget that switches the projection on ──

    [Fact]
    public void A_map_that_declares_no_prompt_budget_gets_no_projection()
    {
        MapPlan.From(new MapConfig()).PromptBudgetChars.ShouldBeNull(
            "every map that existed before this bound must keep emitting exactly the keys it always did");
    }

    [Theory]
    [InlineData(0, null)]                                              // non-positive ⇒ off, like an absent key
    [InlineData(-5, null)]
    [InlineData(10, MapPlan.MinPromptBudgetChars)]                     // set but tiny ⇒ raised to the floor
    [InlineData(MapPlan.MinPromptBudgetChars + 1, MapPlan.MinPromptBudgetChars + 1)]
    [InlineData(120_000, 120_000)]
    public void A_set_prompt_budget_is_floored_so_a_projection_always_has_room_to_explain_itself(int configured, int? expected)
    {
        MapPlan.From(new MapConfig { PromptBudgetChars = configured }).PromptBudgetChars.ShouldBe(expected);
    }

    // ── fixtures ──

    /// <summary>
    /// How many branches actually carry a truncation marker. Matched on the DIGITS, so the notice's own description
    /// of the marker shape ("…[N of M chars …]…", with literal N and M) cannot stand in for a real cut — the earlier
    /// substring form of this assertion was satisfied by the notice alone and passed against a silent cut.
    /// </summary>
    private static int CountBranchMarkers(string projected) =>
        Regex.Matches(projected, @"…\[\d+ of \d+ chars of this subtask result omitted\]…").Count;

    /// <summary>An agent.run-shaped result array — the branch terminal output the plan-map map actually reduces.</summary>
    private static JsonElement Results(params string[] summaries) =>
        JsonSerializer.SerializeToElement(summaries.Select(s => new { status = "Succeeded", summary = s }).ToArray());

    private static JsonElement Obj(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Resolve(string template, NodeRunScope scope) =>
        VariableResolver.Resolve(JsonSerializer.SerializeToElement(template), scope).GetString()!;
}
