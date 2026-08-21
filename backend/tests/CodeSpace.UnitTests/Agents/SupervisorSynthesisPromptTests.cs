using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

[Trait("Category", "Unit")]
public sealed class SupervisorSynthesisPromptTests
{
    [Fact]
    public void A_within_budget_prompt_is_byte_identical_to_the_legacy_rendering()
    {
        var agent = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var sources = new[]
        {
            new SupervisorSynthesisSource(agent, "Succeeded", "implemented alpha", new[] { new SupervisorSynthesisDiff(null, "@@ -1 +1 @@\n-old\n+new") }),
        };

        var projection = SupervisorSynthesisPrompt.Project("ship it", sources, SupervisorSynthesisBudget.DefaultChars);

        projection.Text.ShouldBe($"Goal: ship it\n\n=== Agent {agent} (Succeeded) ===\nSummary: implemented alpha\nDiff:\n@@ -1 +1 @@\n-old\n+new\n\n");
        projection.Coverage.ShouldBe(new SupervisorSynthesisCoverage(true, 1, 1, 0, 0, false, SupervisorSynthesisBudget.DefaultChars, projection.Text.Length));
    }

    [Fact]
    public void A_within_budget_analysis_only_prompt_is_byte_identical_to_the_legacy_rendering()
    {
        var agent = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var sources = new[] { new SupervisorSynthesisSource(agent, "Succeeded", "analysis without a patch", Array.Empty<SupervisorSynthesisDiff>()) };

        var projection = SupervisorSynthesisPrompt.Project("investigate", sources, SupervisorSynthesisBudget.DefaultChars);

        projection.Text.ShouldBe($"Goal: investigate\n\n=== Agent {agent} (Succeeded) ===\nSummary: analysis without a patch\n");
        projection.Coverage.ShouldBe(new SupervisorSynthesisCoverage(true, 1, 1, 0, 0, false, SupervisorSynthesisBudget.DefaultChars, projection.Text.Length));
    }

    [Fact]
    public void An_authored_synthesis_instruction_reaches_the_bounded_prompt_without_changing_the_legacy_null_path()
    {
        var sources = new[] { Source("11111111-1111-1111-1111-111111111111", "web", 'a') };

        var legacy = SupervisorSynthesisPrompt.Project("ship it", sources, SupervisorSynthesisBudget.DefaultChars);
        var instructed = SupervisorSynthesisPrompt.Project("ship it", "Emphasize compatibility and migration risk.", sources, SupervisorSynthesisBudget.DefaultChars);

        legacy.Text.ShouldStartWith("Goal: ship it\n\n=== Agent", customMessage: "an absent instruction stays byte-compatible with the pre-slice prompt");
        legacy.Text.ShouldNotContain("Synthesis instruction:");
        instructed.Text.ShouldStartWith("Goal: ship it\n\nSynthesis instruction:\nEmphasize compatibility and migration risk.\n\n=== Agent");
    }

    [Fact]
    public void A_large_synthesis_instruction_is_inside_the_same_hard_prompt_budget()
    {
        var sources = new[] { Source("11111111-1111-1111-1111-111111111111", "web", 'a') };

        var projection = SupervisorSynthesisPrompt.Project("ship it", new string('i', 20_000), sources, SupervisorSynthesisBudget.MinChars);

        projection.Text.Length.ShouldBeLessThanOrEqualTo(SupervisorSynthesisBudget.MinChars);
        projection.Coverage.Complete.ShouldBeFalse();
        projection.Coverage.GoalShortened.ShouldBeTrue("the directive shares the bounded goal/directive projection; it never creates an unbounded side channel");
    }

    [Fact]
    public void An_over_budget_prompt_is_bounded_and_fair_across_agents_and_repositories()
    {
        var sources = new[]
        {
            Source("11111111-1111-1111-1111-111111111111", "web", 'a'),
            Source("22222222-2222-2222-2222-222222222222", "api", 'b'),
            Source("33333333-3333-3333-3333-333333333333", "worker", 'c'),
        };

        var projection = SupervisorSynthesisPrompt.Project(new string('g', 3_000), sources, SupervisorSynthesisBudget.MinChars);

        projection.Text.Length.ShouldBeLessThanOrEqualTo(SupervisorSynthesisBudget.MinChars);
        projection.Text.ShouldStartWith("[EXCERPT — NOT the complete supervisor synthesis input.");
        projection.Text.ShouldContain("Agent 11111111-1111-1111-1111-111111111111");
        projection.Text.ShouldContain("Agent 22222222-2222-2222-2222-222222222222");
        projection.Text.ShouldContain("Agent 33333333-3333-3333-3333-333333333333");
        projection.Text.ShouldContain("Diff [web]");
        projection.Text.ShouldContain("Diff [api]");
        projection.Text.ShouldContain("Diff [worker]");
        projection.Coverage.Complete.ShouldBeFalse();
        projection.Coverage.TotalSources.ShouldBe(3);
        projection.Coverage.IncludedSources.ShouldBe(3);
        projection.Coverage.ShortenedSources.ShouldBe(3);
        projection.Coverage.OmittedSources.ShouldBe(0);
        projection.Coverage.GoalShortened.ShouldBeTrue();
        projection.Coverage.EmittedChars.ShouldBe(projection.Text.Length);
    }

    [Fact]
    public void A_narrow_budget_names_omitted_sources_instead_of_silently_claiming_completeness()
    {
        var sources = Enumerable.Range(0, 11)
            .Select(i => Source(Guid.Parse($"{i + 1:D8}-1111-1111-1111-111111111111").ToString(), $"repo-{i}", (char)('a' + i)))
            .Append(new SupervisorSynthesisSource(Guid.Parse("00000012-1111-1111-1111-111111111111"), "Succeeded", "analysis-only omitted evidence", Array.Empty<SupervisorSynthesisDiff>()))
            .ToArray();

        var projection = SupervisorSynthesisPrompt.Project("goal", sources, SupervisorSynthesisBudget.MinChars);

        projection.Text.Length.ShouldBeLessThanOrEqualTo(SupervisorSynthesisBudget.MinChars);
        projection.Coverage.Complete.ShouldBeFalse();
        projection.Coverage.TotalSources.ShouldBe(12);
        projection.Coverage.IncludedSources.ShouldBeLessThan(12);
        projection.Coverage.OmittedSources.ShouldBe(12 - projection.Coverage.IncludedSources);
        projection.Text.ShouldContain($"{projection.Coverage.IncludedSources} of 12 diff sources appear below");
        projection.Text.ShouldNotContain("analysis-only omitted evidence", customMessage: "the last summary-only source is omitted by this narrow budget, so its source count is the durable disclosure");
    }

    [Fact]
    public void Source_selection_round_robins_repository_ordinals_across_agents()
    {
        var agentA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var agentB = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var sources = new[]
        {
            new SupervisorSynthesisSource(agentA, "Succeeded", null, new[]
            {
                new SupervisorSynthesisDiff("a-1", new string('a', 2_000)),
                new SupervisorSynthesisDiff("a-2", new string('b', 2_000)),
                new SupervisorSynthesisDiff("a-3", new string('c', 2_000)),
            }),
            new SupervisorSynthesisSource(agentB, "Succeeded", null, new[] { new SupervisorSynthesisDiff("b-1", new string('d', 2_000)) }),
        };

        var projection = SupervisorSynthesisPrompt.Project("goal", sources, SupervisorSynthesisBudget.MinChars);

        projection.Text.ShouldContain("Diff [a-1]");
        projection.Text.ShouldContain("Diff [b-1]", customMessage: "one agent's extra repositories must not starve the next agent's first repository");
    }

    [Fact]
    public void A_bounded_prompt_keeps_an_analysis_only_contributor_without_a_diff()
    {
        var analysisAgent = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var implementationAgent = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var sources = new[]
        {
            new SupervisorSynthesisSource(analysisAgent, "Succeeded", "analysis-only evidence that synthesis must retain", Array.Empty<SupervisorSynthesisDiff>()),
            new SupervisorSynthesisSource(implementationAgent, "Succeeded", "implemented web", new[] { new SupervisorSynthesisDiff("web", new string('w', 8_000)) }),
        };

        var projection = SupervisorSynthesisPrompt.Project(new string('g', 3_000), sources, SupervisorSynthesisBudget.MinChars);

        projection.Coverage.Complete.ShouldBeFalse("the regression exists only after the bounded excerpt path is selected");
        projection.Text.ShouldContain($"Agent {analysisAgent}");
        projection.Text.ShouldContain("analysis-only evidence that synthesis must retain");
        projection.Text.ShouldContain($"Agent {implementationAgent}");
        projection.Coverage.TotalSources.ShouldBe(2, "a contributor summary is evidence even when that contributor produced no patch");
        projection.Coverage.IncludedSources.ShouldBe(2);
        projection.Coverage.OmittedSources.ShouldBe(0);
    }

    [Fact]
    public void An_over_budget_all_analysis_prompt_accounts_for_its_summary_source()
    {
        var agent = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var sources = new[] { new SupervisorSynthesisSource(agent, "Succeeded", "analysis-start-" + new string('a', 8_000), Array.Empty<SupervisorSynthesisDiff>()) };

        var projection = SupervisorSynthesisPrompt.Project("investigate", sources, SupervisorSynthesisBudget.MinChars);

        projection.Coverage.Complete.ShouldBeFalse();
        projection.Text.ShouldContain($"Agent {agent}");
        projection.Text.ShouldContain("analysis-start-");
        projection.Coverage.TotalSources.ShouldBe(1);
        projection.Coverage.IncludedSources.ShouldBe(1);
        projection.Coverage.ShortenedSources.ShouldBe(1);
        projection.Coverage.OmittedSources.ShouldBe(0);
        projection.Text.Length.ShouldBeLessThanOrEqualTo(SupervisorSynthesisBudget.MinChars);
    }

    [Fact]
    public void Slicing_never_leaves_an_unpaired_utf16_surrogate()
    {
        var sources = new[] { Source("11111111-1111-1111-1111-111111111111", "emoji", 'x', "🚀") };

        var projection = SupervisorSynthesisPrompt.Project(string.Concat(Enumerable.Repeat("🚀", 2_000)), sources, SupervisorSynthesisBudget.MinChars);

        HasUnpairedSurrogate(projection.Text).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, SupervisorSynthesisBudget.DefaultChars)]
    [InlineData(0, SupervisorSynthesisBudget.DefaultChars)]
    [InlineData(1, SupervisorSynthesisBudget.MinChars)]
    [InlineData(2500, 2500)]
    [InlineData(2000000, SupervisorSynthesisBudget.MaxChars)]
    public void Budget_normalization_is_closed_and_bounded(int? authored, int expected) =>
        SupervisorSynthesisBudget.Normalize(authored).ShouldBe(expected);

    private static SupervisorSynthesisSource Source(string agentId, string alias, char fill, string suffix = "") =>
        new(Guid.Parse(agentId), "Succeeded", $"summary-{alias}", new[] { new SupervisorSynthesisDiff(alias, new string(fill, 4_000) + suffix) });

    private static bool HasUnpairedSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return true;
            }
            else if (char.IsLowSurrogate(value[i])) return true;
        }

        return false;
    }
}
