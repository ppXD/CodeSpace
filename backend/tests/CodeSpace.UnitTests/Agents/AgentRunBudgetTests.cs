using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

[Trait("Category", "Unit")]
public sealed class AgentRunBudgetTests
{
    [Fact]
    public void Apply_is_reference_identical_when_the_task_has_no_cap()
    {
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Model = "unknown" };

        AgentRunBudget.Apply(new AgentTask { Goal = "g", Harness = "h" }, result, ModelPriceResolver.Empty).ShouldBeSameAs(result);
    }

    [Fact]
    public void Apply_prices_observed_usage_and_accumulates_prior_attempts()
    {
        var task = new AgentTask { Goal = "g", Harness = "h", MaxCostUsd = 1m, BudgetSpentUsd = 0.25m };
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Model = "priced", TokenUsage = new AgentTokenUsage { InputTokens = 100_000, OutputTokens = 10_000 } };
        var prices = new Dictionary<string, ModelPrice> { ["priced"] = new() { InputPerMillionUsd = 2m, OutputPerMillionUsd = 10m } };

        var accounted = AgentRunBudget.Apply(task, result, prices);

        accounted.CostUsd.ShouldBe(0.3m);
        accounted.CumulativeCostUsd.ShouldBe(0.55m);
        accounted.CostIndeterminate.ShouldBeFalse();
    }

    [Fact]
    public void Apply_marks_capped_usage_indeterminate_when_the_observed_model_has_no_price()
    {
        var task = new AgentTask { Goal = "g", Harness = "h", MaxCostUsd = 1m };
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Model = "unknown", TokenUsage = new AgentTokenUsage { InputTokens = 1, OutputTokens = 1 } };

        var accounted = AgentRunBudget.Apply(task, result, ModelPriceResolver.Empty);

        accounted.CostUsd.ShouldBeNull();
        accounted.CumulativeCostUsd.ShouldBeNull();
        accounted.CostIndeterminate.ShouldBeTrue();
    }

    [Fact]
    public void Apply_uses_the_observed_model_instead_of_the_authored_fallback()
    {
        var task = new AgentTask { Goal = "g", Harness = "h", Model = "authored", MaxCostUsd = 10m };
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Model = "observed", TokenUsage = new AgentTokenUsage { InputTokens = 1_000_000, OutputTokens = 0 } };
        var prices = new Dictionary<string, ModelPrice>
        {
            ["authored"] = new() { InputPerMillionUsd = 1m, OutputPerMillionUsd = 1m },
            ["observed"] = new() { InputPerMillionUsd = 4m, OutputPerMillionUsd = 4m },
        };

        AgentRunBudget.Apply(task, result, prices).CostUsd.ShouldBe(4m);
    }

    [Fact]
    public void Apply_marks_missing_usage_indeterminate_instead_of_free()
    {
        var task = new AgentTask { Goal = "g", Harness = "h", Model = "priced", MaxCostUsd = 1m };
        var result = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit" };

        AgentRunBudget.Apply(task, result, new Dictionary<string, ModelPrice> { ["priced"] = new() { InputPerMillionUsd = 1m, OutputPerMillionUsd = 1m } }).CostIndeterminate.ShouldBeTrue();
    }

    [Fact]
    public void WithoutInvocation_preserves_prior_spend_and_records_a_known_zero_attempt()
    {
        var task = new AgentTask { Goal = "g", Harness = "h", MaxCostUsd = 1m, BudgetSpentUsd = 0.25m };
        var result = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "acceptance-unavailable" };

        var accounted = AgentRunBudget.WithoutInvocation(task, result);

        accounted.CostUsd.ShouldBe(0m);
        accounted.CumulativeCostUsd.ShouldBe(0.25m);
        accounted.CostIndeterminate.ShouldBeFalse();
    }

    [Fact]
    public void Apply_stamps_the_snapshot_it_priced_with()
    {
        // MUTATION THIS CATCHES: dropping `PriceSnapshot = snapshot` from Apply's priced return. CostUsd would still
        // be 0.3 and every existing assertion above would stay green, while the number became unauditable — the rate
        // table has no effective-from column, so an operator editing "priced" to 4/20 tomorrow silently re-values
        // this run's $0.30 and nothing recorded what produced it.
        var task = new AgentTask { Goal = "g", Harness = "h", MaxCostUsd = 1m };
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Model = "priced", TokenUsage = new AgentTokenUsage { InputTokens = 100_000, OutputTokens = 10_000 } };
        var prices = new Dictionary<string, ModelPrice> { ["priced"] = new() { InputPerMillionUsd = 2m, OutputPerMillionUsd = 10m } };

        var snapshot = AgentRunBudget.Apply(task, result, prices).PriceSnapshot.ShouldNotBeNull();

        snapshot.Source.ShouldBe(ModelPriceSources.CredentialRow, "the operator's own per-model row priced this run, not the built-in table — an auditor must be able to tell which");
        snapshot.InputUsdPerMillion.ShouldBe(2m);
        snapshot.OutputUsdPerMillion.ShouldBe(10m);
        snapshot.Digest.ShouldBe(AgentCostPricing.SnapshotOf(ModelPriceSources.CredentialRow, 2m, 10m).Digest);
    }

    [Fact]
    public void Apply_stamps_the_rates_even_when_the_usage_could_not_be_priced()
    {
        // A capped run whose CLI reported no usage was still valued against a specific table. MUTATION: stamping the
        // snapshot only on the success return — the indeterminate row would then lose the rates that WOULD have
        // applied, which is exactly the evidence an "why was this indeterminate" audit needs.
        var task = new AgentTask { Goal = "g", Harness = "h", Model = "priced", MaxCostUsd = 1m };
        var result = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit" };
        var prices = new Dictionary<string, ModelPrice> { ["priced"] = new() { InputPerMillionUsd = 1m, OutputPerMillionUsd = 3m } };

        var accounted = AgentRunBudget.Apply(task, result, prices);

        accounted.CostIndeterminate.ShouldBeTrue();
        accounted.PriceSnapshot.ShouldNotBeNull().InputUsdPerMillion.ShouldBe(1m);
    }

    [Fact]
    public void Apply_stamps_no_snapshot_when_no_table_prices_the_model()
    {
        // The honest absence: null snapshot means "nothing priced this", never "priced at zero".
        var task = new AgentTask { Goal = "g", Harness = "h", MaxCostUsd = 1m };
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Model = "no-such-model", TokenUsage = new AgentTokenUsage { InputTokens = 1, OutputTokens = 1 } };

        AgentRunBudget.Apply(task, result, ModelPriceResolver.Empty).PriceSnapshot.ShouldBeNull();
    }

    [Fact]
    public void The_price_snapshot_round_trips_through_AgentJson_so_the_stamp_is_durable()
    {
        // The stamp is only worth anything if it SURVIVES result_json — the envelope it is persisted through.
        var result = new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded, ExitReason = "completed",
            PriceSnapshot = AgentCostPricing.SnapshotOf(ModelPriceSources.EnvOverride, 2.5m, 10m),
        };

        var rehydrated = System.Text.Json.JsonSerializer.Deserialize<AgentRunResult>(System.Text.Json.JsonSerializer.Serialize(result, AgentJson.Options), AgentJson.Options)!;

        rehydrated.PriceSnapshot.ShouldBe(result.PriceSnapshot);
    }

    [Theory]
    [InlineData(5.0, 5.0, 0.0, 5.0)]      // the quick lane's first run: node cap and run cap are the SAME projected number
    [InlineData(0.5, 5.0, 0.0, 0.5)]      // a tighter task ceiling is the honest estimate
    [InlineData(100.0, 12.0, 0.0, 12.0)]  // a task ceiling ABOVE the run cap is unreachable — claiming it would refuse the first launch outright
    [InlineData(null, 5.0, 0.0, 5.0)]     // no task ceiling: an opaque CLI's honest maximum IS what the run has left
    [InlineData(5.0, 5.0, 0.5, 4.5)]      // a SECOND run after the first settled small — it claims the remainder, not the ceiling
    [InlineData(null, 5.0, 4.9, 0.1)]
    public void The_pre_launch_estimate_claims_what_the_run_has_left(double? taskCap, double runCap, double committed, double expected)
    {
        // MUTATION THIS CATCHES: `min(task.MaxCostUsd ?? cap, cap)` — the whole ceiling instead of the remainder.
        // Rows 5 and 6 then claim the full $5 against a run that has already committed some of it, so ReserveAsync
        // refuses, and a workflow run's SECOND agent could never launch however little the first actually spent.
        var task = new AgentTask { Goal = "g", Harness = "h", MaxCostUsd = (decimal?)taskCap };

        AgentRunExecutor.RunSpendEstimate(task, (decimal)runCap, (decimal)committed).ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData(null, 5.0, 5.0)]    // the run's ceiling is fully committed — a spent cap must not start another CLI
    [InlineData(null, 5.0, 7.0)]    // over-committed (a settle up past the estimate) is just as spent
    [InlineData(0.0, 5.0, 0.0)]     // "spend nothing" is a bound, not an absent one
    [InlineData(-1.0, 5.0, 0.0)]    // a negative ceiling reached the ledger's own ArgumentOutOfRangeException and surfaced untyped
    public void A_launch_with_nothing_left_to_claim_has_no_estimate(double? taskCap, double runCap, double committed)
    {
        // MUTATION THIS CATCHES: flooring the estimate at 0 and reserving it anyway. `committed + 0 > cap` is FALSE
        // at exactly-cap, so the ledger would ADMIT a run whose ceiling is entirely spent — the precise hole this
        // slice exists to close — and a MaxCostUsd of 0 would buy a CLI launch for a run authorized to spend nothing.
        var task = new AgentTask { Goal = "g", Harness = "h", MaxCostUsd = (decimal?)taskCap };

        AgentRunExecutor.RunSpendEstimate(task, (decimal)runCap, (decimal)committed)
            .ShouldBeNull("nothing left to claim is a REFUSAL, never a free launch on a zero-dollar claim");
    }

    /// <summary>
    /// What a terminal writer OUTSIDE the executor may settle a live claim at. Every non-figure — no result, an
    /// unparseable one, a run whose own accounting came back indeterminate — must read as NOTHING KNOWN, because
    /// the ledger settles a null pessimistically at the reserve while any number it is handed is recorded as a bill
    /// the team was charged. An operator cancel and the reconciler's abandon have no other evidence to go on.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("not json at all", null)]
    [InlineData("""{"status":"Failed","exitReason":"x"}""", null)]
    [InlineData("""{"status":"Failed","exitReason":"x","costUsd":0.75}""", 0.75)]
    [InlineData("""{"status":"Failed","exitReason":"x","costUsd":0.75,"costIndeterminate":true}""", null)]
    [InlineData("""{"status":"Failed","exitReason":"x","cumulativeCostUsd":9.5}""", null)]
    public void Observed_spend_is_read_only_from_a_result_that_recorded_one(string? resultJson, double? expected)
    {
        // MUTATION THIS CATCHES: reading CumulativeCostUsd (the retry chain's running total) instead of this
        // attempt's own CostUsd — the live claim would be charged for rounds that already settled at their own
        // exits. Or dropping the CostIndeterminate guard, which turns "we could not price this" into a bill.
        AgentRunBudget.ObservedUsd(resultJson).ShouldBe((decimal?)expected);
    }

    /// <summary>
    /// The spend scope key's grammar, both ways. It is DURABLE state — every live <c>budget_reservation</c> row of
    /// the agent-run plane carries one — and three separate things read it: the terminal close matches every key of
    /// a run by its <c>{runId:N}</c> prefix, a refusal names the attempt back to an operator, and the ledger's own
    /// exact-match replay rule depends on one attempt's key never colliding with another's.
    /// </summary>
    [Theory]
    [InlineData(1, 0, "/e1")]
    [InlineData(1, 2, "/e1/r2")]
    [InlineData(43, 0, "/e43")]
    public void A_spend_scope_key_names_its_run_its_attempt_and_its_round(long epoch, int round, string suffix)
    {
        // MUTATION THIS CATCHES: dropping the epoch from the key. Two attempts of one run would collide on a single
        // row, the second is refused as an intent mismatch, and the first attempt's spend settles at the second's.
        var runId = Guid.NewGuid();
        var key = AgentRunExecutor.RunSpendScopeKey(runId, epoch, round);

        key.ShouldBe($"{runId:N}{suffix}");
        key.ShouldStartWith(runId.ToString("N"), Case.Sensitive, "every key of one run must fall under the prefix its terminal closes by");
        AgentRunExecutor.RunSpendScopeEpoch(key).ShouldBe(epoch.ToString());
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", "unrecorded")]
    [InlineData("0123456789abcdef0123456789abcdef/r1", "unrecorded")]
    public void A_key_minted_before_the_attempt_grain_names_no_attempt(string legacyKey, string expected)
    {
        // Rows written before this shape existed are still live in production and still closed by the prefix; a
        // refusal must describe them honestly rather than inventing an attempt number for them.
        AgentRunExecutor.RunSpendScopeEpoch(legacyKey).ShouldBe(expected);
    }

    [Theory]
    [InlineData(true, 0.99, false)]
    [InlineData(false, 1.00, true)]
    [InlineData(false, 1.01, true)]
    public void Further_calls_stop_on_indeterminate_or_at_the_observed_ceiling(bool indeterminate, double cumulative, bool expectedFromSpend)
    {
        var task = new AgentTask { Goal = "g", Harness = "h", MaxCostUsd = 1m };
        var result = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "failed", CostIndeterminate = indeterminate, CumulativeCostUsd = (decimal)cumulative };

        AgentRunExecutor.CostBudgetStopsFurtherCalls(task, result).ShouldBe(indeterminate || expectedFromSpend);
    }
}
