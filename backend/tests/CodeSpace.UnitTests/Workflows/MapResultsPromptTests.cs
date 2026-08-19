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
/// <para>The "it must SAY so" invariant is asserted TWICE over, on two different carriers, and the second one is the
/// load-bearing half: the notice inside the text is a request to a model, so the <see cref="MapResultsCoverage"/>
/// section below pins the same partiality as DATA — the numbers, the shortened indices, and the single
/// <c>Complete</c> flag — because that is the copy a downstream node, a projection, and this test can read without
/// trusting a model to have obeyed a sentence.</para>
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

        var projected = MapResultsPrompt.Project(results, Budget).Text;

        // The ordinary fan-out: the model must see EXACTLY what the raw-array binding produced — the same call
        // VariableResolver's array arm makes on the same element. Not "equivalent JSON": the same characters.
        projected.ShouldBe(JsonSerializer.Serialize(results),
            customMessage: "a fan-out inside the budget must not be reshaped at all — the bound may only bind when it binds");
    }

    [Fact]
    public void A_zero_budget_disables_the_bound_entirely()
    {
        var results = Results(new string('x', 50_000));

        MapResultsPrompt.Project(results, 0).Text.ShouldBe(JsonSerializer.Serialize(results),
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

        var projected = MapResultsPrompt.Project(results, budget).Text;

        projected.Length.ShouldBeLessThanOrEqualTo(budget,
            customMessage: $"{branches} branches of {branchChars} chars projected to {projected.Length} chars against a {budget}-char budget — the synthesizer is handed more than its bound");
    }

    [Fact]
    public void Over_budget_the_projection_opens_by_saying_it_is_an_excerpt_and_names_how_many_results_are_present()
    {
        var results = Results(Enumerable.Range(0, 40).Select(_ => new string('y', 4_000)).ToArray());

        var projected = MapResultsPrompt.Project(results, Budget).Text;

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

        var projected = MapResultsPrompt.Project(results, Budget).Text;

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

        var projected = MapResultsPrompt.Project(results, Budget).Text;

        CountBranchMarkers(projected).ShouldBe(2,
            customMessage: "a shortened branch must say so WHERE it was shortened — an unmarked cut reads as the branch's whole output");
    }

    [Fact]
    public void A_fan_out_too_wide_to_represent_shows_fewer_branches_rather_than_slivers_of_all_of_them()
    {
        var results = Results(Enumerable.Range(0, 200).Select(i => $"result {i} " + new string('w', 500)).ToArray());

        var projected = MapResultsPrompt.Project(results, Budget).Text;

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

    // ── MapResultsCoverage: the partiality as DATA, not as a sentence to the model ──

    /// <summary>
    /// The blocker this section exists for. Excerpting used to leave "this is partial" ONLY inside the prompt text,
    /// so a reduce that saw 3 of 20 branches produced the same shaped data as one that saw 20 of 20 and the
    /// difference was unverifiable. The coverage must state the real numbers — and it must be the code that DID the
    /// cutting that states them, which is what asserting them against the text's own notice proves.
    /// </summary>
    [Fact]
    public void Over_budget_the_coverage_names_how_many_branches_were_included_out_of_how_many_exist()
    {
        var results = Results(Enumerable.Range(0, 20).Select(_ => new string('y', 4_000)).ToArray());

        var projection = MapResultsPrompt.Project(results, Budget);

        projection.Coverage.Complete.ShouldBeFalse(
            customMessage: "branches were dropped — the flag a downstream consumer gates on must say the input was not whole");
        projection.Coverage.TotalBranches.ShouldBe(20);
        projection.Coverage.IncludedBranches.ShouldBeInRange(1, 19,
            customMessage: "the 8K budget holds some but not all of 20 four-thousand-char branches; that count is the fact the run must record");

        // Same fact, both carriers: the number the model is TOLD and the number the data RECORDS cannot disagree,
        // or a reader auditing one against the other would be misled about which to trust.
        projection.Text.ShouldStartWith($"[EXCERPT — NOT the complete per-subtask results. {projection.Coverage.IncludedBranches} of 20 subtask results appear below;",
            customMessage: "the recorded IncludedBranches must be the same number the notice states");
    }

    /// <summary>
    /// The case that separates a REAL flag from one derived by comparing counts: two branches, both included, both
    /// cut. IncludedBranches == TotalBranches here, so a <c>Complete</c> computed as "included == total" would read
    /// TRUE over a projection that dropped most of both branches' text — the exact untruth this record exists to
    /// prevent. The cut branches must also be NAMED, so a reader can tell which subtasks are only partly represented.
    /// </summary>
    [Fact]
    public void Every_branch_included_but_shortened_still_reads_incomplete_and_names_which_ones_were_cut()
    {
        var results = Results(new string('q', 100_000), new string('r', 100_000));

        var coverage = MapResultsPrompt.Project(results, Budget).Coverage;

        coverage.IncludedBranches.ShouldBe(2, customMessage: "both branches are present in the excerpt");
        coverage.TotalBranches.ShouldBe(2);
        coverage.Complete.ShouldBeFalse(
            customMessage: "every branch appearing is NOT the same as every branch appearing in full — a flag that cannot see truncation is the defect");
        coverage.ShortenedBranches.ShouldBe(new[] { 0, 1 },
            customMessage: "both branches were cut, so both indices must be named — a reader has to know WHICH subtasks are partial");
    }

    [Fact]
    public void Within_budget_the_coverage_says_the_reduce_read_every_branch_in_full()
    {
        var results = Results("did the first thing", "did the second thing", "did the third thing");

        var coverage = MapResultsPrompt.Project(results, Budget).Coverage;

        coverage.Complete.ShouldBeTrue(customMessage: "nothing was dropped, so nothing may be recorded as dropped either");
        coverage.IncludedBranches.ShouldBe(3);
        coverage.TotalBranches.ShouldBe(3);
        coverage.ShortenedBranches.ShouldBeEmpty();
    }

    /// <summary>
    /// The invariant that ties the two carriers together at EVERY shape the projection can take, including the
    /// degenerate ones (a budget too narrow for a single readable slice, a non-array binding): the recorded
    /// <c>Complete</c> is true exactly when the text IS the whole serialization. Nothing can be dropped without the
    /// flag falling, and the flag cannot fall while everything is present.
    /// </summary>
    [Theory]
    [InlineData(3, 40, 2_000)]              // under budget
    [InlineData(1, 1_000_000, 2_000)]       // one branch far larger than the budget
    [InlineData(10, 200_000, 8_000)]
    [InlineData(10_000, 100, 2_000)]        // the branch ceiling at the plan's smallest budget
    [InlineData(2, 300, 2_000)]
    [InlineData(4, 5_000, 40)]              // narrower than the notice itself — the degenerate cut
    [InlineData(3, 100, 0)]                 // bound disabled entirely
    public void Complete_is_recorded_true_exactly_when_nothing_was_dropped(int branches, int branchChars, int budget)
    {
        var results = Results(Enumerable.Range(0, branches).Select(i => new string((char)('a' + i % 26), branchChars)).ToArray());

        var projection = MapResultsPrompt.Project(results, budget);

        projection.Coverage.Complete.ShouldBe(projection.Text == JsonSerializer.Serialize(results),
            customMessage: $"{branches} branches of {branchChars} chars at a {budget}-char budget recorded Complete={projection.Coverage.Complete} over a text that is {(projection.Text == JsonSerializer.Serialize(results) ? "" : "NOT ")}the whole serialization");
    }

    /// <summary>
    /// The reducer's end of the same fact: a map that declares a budget PERSISTS the coverage beside the text, and an
    /// excerpted map's output bag is therefore NOT the shape a complete one produces. Asserted on the bag rather than
    /// on the prompt string, because the bag is what the ledger stores and what every downstream reader binds.
    /// </summary>
    [Fact]
    public void The_reducer_persists_the_coverage_so_an_excerpted_map_is_distinguishable_from_a_complete_one()
    {
        var big = Enumerable.Range(0, 30).Select(_ => Obj($$"""{"summary":"{{new string('z', 4_000)}}"}""")).ToList();
        var small = new List<JsonElement> { Obj("""{"summary":"did it"}""") };

        var excerpted = Coverage(WorkflowEngine.BuildMapOutputs("results", big, failed: 0, promptBudgetChars: Budget));
        var whole = Coverage(WorkflowEngine.BuildMapOutputs("results", small, failed: 0, promptBudgetChars: Budget));

        excerpted.GetProperty("complete").GetBoolean().ShouldBeFalse();
        excerpted.GetProperty("totalBranches").GetInt32().ShouldBe(30);
        excerpted.GetProperty("includedBranches").GetInt32().ShouldBeLessThan(30,
            customMessage: "the persisted bag must carry the shortfall, not merely a prompt that mentions it");

        whole.GetProperty("complete").GetBoolean().ShouldBeTrue();
        whole.GetProperty("includedBranches").GetInt32().ShouldBe(1);

        excerpted.GetRawText().ShouldNotBe(whole.GetRawText(),
            customMessage: "THE blocker: a run over a partial view must not be byte-indistinguishable from one over the whole view");
    }

    [Fact]
    public void A_map_that_declares_no_budget_persists_no_coverage_either()
    {
        var outputs = WorkflowEngine.BuildMapOutputs("results", new List<JsonElement>(), failed: 0);

        outputs.ContainsKey(WorkflowOutputKeys.MapResultsCoverage).ShouldBeFalse(
            "no projection means nothing was bounded, so there is no coverage claim to make — every pre-existing map keeps its exact output bag");
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

    /// <summary>The coverage the real reducer persisted into a map's output bag.</summary>
    private static JsonElement Coverage(IReadOnlyDictionary<string, JsonElement> outputs) => outputs[WorkflowOutputKeys.MapResultsCoverage];

    private static string Resolve(string template, NodeRunScope scope) =>
        VariableResolver.Resolve(JsonSerializer.SerializeToElement(template), scope).GetString()!;
}
