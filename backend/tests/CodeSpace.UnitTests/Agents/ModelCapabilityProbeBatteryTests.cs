using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the capability-probe BATTERY: the in-code oracles actually discriminate a correct responder from a garbage one,
/// and the score→tier mapping caps at Strong (never Frontier) and floors below the Easy majority. Order-coupled on
/// purpose — it pins the exact battery, so a careless edit that breaks the Easy/Hard split is caught.
/// </summary>
public sealed class ModelCapabilityProbeBatteryTests
{
    // The canonical correct answer for each task, in Tasks order. Pinning these proves the graders accept a real answer.
    private static readonly string[] Correct = ["12", "21", "maerts", "{\"sum\": 13, \"product\": 42}", "40", "2,3,5"];

    [Fact]
    public void Battery_has_a_balanced_easy_and_hard_split()
    {
        ModelCapabilityProbeBattery.Tasks.Count.ShouldBe(Correct.Length, "the Correct[] fixture must stay in lockstep with the battery");
        ModelCapabilityProbeBattery.CountBand(ProbeBand.Easy).ShouldBeGreaterThanOrEqualTo(3);
        ModelCapabilityProbeBattery.CountBand(ProbeBand.Hard).ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void A_perfect_responder_maps_to_Strong()
    {
        var (easy, hard) = Score(Correct);
        ModelCapabilityProbeBattery.MapToTier(easy, hard).ShouldBe(ModelCapabilityTier.Strong, "answering every task correctly demonstrates Strong");
    }

    [Fact]
    public void A_garbage_responder_gets_no_verdict_and_stays_Unknown()
    {
        var garbage = ModelCapabilityProbeBattery.Tasks.Select(_ => "I cannot help with that.").ToArray();
        var (easy, hard) = Score(garbage);
        ModelCapabilityProbeBattery.MapToTier(easy, hard).ShouldBeNull("a model that fails every task gets no verdict — it stays Unknown, never gets promoted");
    }

    [Fact]
    public void An_easy_only_responder_maps_to_Basic_not_Strong()
    {
        // Correct on the EASY band, garbage on the HARD band — the structured/multi-step tasks are the Strong discriminator.
        var responses = ModelCapabilityProbeBattery.Tasks
            .Select((t, i) => t.Band == ProbeBand.Easy ? Correct[i] : "nope").ToArray();
        var (easy, hard) = Score(responses);

        easy.ShouldBeGreaterThanOrEqualTo(2, "the easy band is cleared");
        ModelCapabilityProbeBattery.MapToTier(easy, hard).ShouldBe(ModelCapabilityTier.Basic, "passing only the easy band is Basic, not Strong");
    }

    [Theory]
    [InlineData(3, 3, ModelCapabilityTier.Strong)]
    [InlineData(2, 2, ModelCapabilityTier.Strong)]   // bare majority of both bands
    [InlineData(3, 1, ModelCapabilityTier.Basic)]    // easy majority, hard minority
    [InlineData(2, 0, ModelCapabilityTier.Basic)]
    public void MapToTier_maps_majorities_and_caps_at_Strong(int easy, int hard, ModelCapabilityTier expected)
        => ModelCapabilityProbeBattery.MapToTier(easy, hard).ShouldBe(expected);

    [Theory]
    [InlineData(1, 3)]   // below the easy floor — no Basic even if the hard band passes
    [InlineData(0, 0)]
    public void MapToTier_returns_null_below_the_easy_majority(int easy, int hard)
        => ModelCapabilityProbeBattery.MapToTier(easy, hard).ShouldBeNull();

    [Fact]
    public void MapToTier_never_returns_Frontier_even_on_a_perfect_score()
        => ModelCapabilityProbeBattery.MapToTier(99, 99).ShouldBe(ModelCapabilityTier.Strong);

    [Fact]
    public void Integer_oracles_tolerate_a_trailing_period_and_prose_but_reject_a_digit_run()
    {
        // The Easy integer tasks (gcd→12, apples→21) and the Hard sort task (→40) must accept a terse answer that ends in
        // a sentence period — a common formatting habit that must NOT collapse the band — while still rejecting a digit run.
        var gcd = ModelCapabilityProbeBattery.Tasks.First(t => t.Prompt.Contains("greatest common divisor"));
        gcd.Passes("12").ShouldBeTrue();
        gcd.Passes("12.").ShouldBeTrue("a trailing period must not fail the answer");
        gcd.Passes("The GCD is 12.").ShouldBeTrue("prose ending in the answer with a period passes");
        gcd.Passes("1200").ShouldBeFalse("a longer digit run is not the answer");

        var sort = ModelCapabilityProbeBattery.Tasks.First(t => t.Prompt.Contains("Sort the list"));
        sort.Passes("40.").ShouldBeTrue();
        sort.Passes("400").ShouldBeFalse();
    }

    [Fact]
    public void The_primes_oracle_rejects_too_many_and_off_by_one()
    {
        var primes = ModelCapabilityProbeBattery.Tasks.First(t => t.Prompt.Contains("prime numbers"));
        primes.Passes("2,3,5").ShouldBeTrue();
        primes.Passes("2, 3, and 5").ShouldBeTrue("natural phrasing with the right integers passes");
        primes.Passes("2,3,5,7").ShouldBeFalse("listing four primes when asked for three fails");
        primes.Passes("1,2,3,5").ShouldBeFalse("an off-by-one start fails");
        primes.Passes("12,3,5").ShouldBeFalse("a leading-digit variant fails");
    }

    [Fact]
    public void The_json_oracle_rejects_a_wrong_value_and_missing_object()
    {
        var json = ModelCapabilityProbeBattery.Tasks.Single(t => t.Prompt.Contains("JSON"));
        json.Passes("{\"sum\": 13, \"product\": 42}").ShouldBeTrue();
        json.Passes("```json\n{\"sum\": 13, \"product\": 42}\n```").ShouldBeTrue("a markdown-fenced object is tolerated");
        json.Passes("Example: {\"a\":1}. Answer: {\"sum\": 13, \"product\": 42}").ShouldBeTrue("an incidental earlier brace group doesn't block the real answer");
        json.Passes("{\"sum\": 13, \"product\": 99}").ShouldBeFalse("a wrong product fails");
        json.Passes("the sum is 13 and the product is 42").ShouldBeFalse("prose with no JSON object fails");
    }

    private static (int easy, int hard) Score(string[] responses)
    {
        var tasks = ModelCapabilityProbeBattery.Tasks;
        int easy = 0, hard = 0;

        for (var i = 0; i < tasks.Count; i++)
        {
            if (!tasks[i].Passes(responses[i])) continue;

            if (tasks[i].Band == ProbeBand.Easy) easy++;
            else hard++;
        }

        return (easy, hard);
    }
}
