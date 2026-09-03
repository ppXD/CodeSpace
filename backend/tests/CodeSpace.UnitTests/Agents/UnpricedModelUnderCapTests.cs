using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit (D1): the PURE half of "price every pool model and fail closed under a cost cap".
///
/// <para>Three things are pinned here. (1) The PRICE RESOLUTION ORDER — an operator's per-row price beats the env
/// override beats the built-in table beats unknown; without the row layer a Codex/OpenAI/Custom pool model is
/// unpriceable and every cost cap over it is inert. (2) The fail-CLOSED POLICY — under a cap an unpriced model is
/// refused, WITHOUT a cap nothing changes (the refutation this prevents: a $5-capped Codex run that spends past $5
/// and still terminalizes Success, because its spend summed as <c>?? 0m</c> forever). (3) The stop's vocabulary,
/// which an operator reads, and which must NAME the model + the remedy.</para>
/// </summary>
[Trait("Category", "Unit")]
[Collection("ModelPriceEnvMutation")]   // the resolution-order theory drives the process-global price env var
public sealed class UnpricedModelUnderCapTests
{
    private const string PoolModel = "gpt-5.4-codex";   // absent from the built-in table BY DESIGN — the whole point

    /// <summary>The id the ENV-mutating tests use. Distinct from every id another test asserts on, because the price override is a process-global read live by every parallel pricing test — overriding a shared id would race them.</summary>
    private const string EnvTestModel = "d1-env-override-probe";

    private static readonly IReadOnlyDictionary<string, ModelPrice> RowPriced = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
    {
        [PoolModel] = new() { InputPerMillionUsd = 2m, OutputPerMillionUsd = 10m },
    };

    private static readonly IReadOnlyDictionary<string, ModelPrice> EnvTestModelRowPriced = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
    {
        [EnvTestModel] = new() { InputPerMillionUsd = 2m, OutputPerMillionUsd = 10m },
    };

    // ── (1) Resolution order: row > env > built-in > null ─────────────────────────────

    [Fact]
    public void A_row_price_makes_an_otherwise_unpriceable_pool_model_priceable()
    {
        AgentCostPricing.CostUsd(PoolModel, 1_000_000, 1_000_000).ShouldBeNull("no row, no env, not in the built-in table");

        AgentCostPricing.CostUsd(PoolModel, 1_000_000, 1_000_000, RowPriced).ShouldBe(12m, "2 in + 10 out per million");
    }

    [Fact]
    public void A_row_price_BEATS_the_env_override()
    {
        using var env = new PriceEnv($"{EnvTestModel}=1/1");

        AgentCostPricing.CostUsd(EnvTestModel, 1_000_000, 0).ShouldBe(1m, "env only");
        AgentCostPricing.CostUsd(EnvTestModel, 1_000_000, 0, EnvTestModelRowPriced).ShouldBe(2m, "the operator's own row wins over the deployment-wide env table");
    }

    [Fact]
    public void The_env_override_prices_a_pool_model_and_the_built_in_table_is_the_last_resort()
    {
        AgentCostPricing.CostUsd(EnvTestModel, 1_000_000, 0).ShouldBeNull("nothing prices it yet");

        using var env = new PriceEnv($"{EnvTestModel}=3/0");

        AgentCostPricing.CostUsd(EnvTestModel, 1_000_000, 0).ShouldBe(3m, "the env layer prices what the built-in table cannot");
        AgentCostPricing.CostUsd("claude-opus-4-8", 1_000_000, 0).ShouldBe(5m, "an unrelated built-in model is untouched — the built-in table is the last resort, not a casualty");
    }

    [Fact]
    public void A_row_price_is_matched_case_insensitively_and_trimmed_like_every_other_layer() =>
        AgentCostPricing.CostUsd($"  {PoolModel.ToUpperInvariant()} ", 1_000_000, 0, RowPriced).ShouldBe(2m);

    // ── (2) The policy: blocks under a cap, silent without one ───────────────────────

    [Theory]
    [InlineData(5.0, true)]     // capped + unpriced → refused
    [InlineData(null, false)]   // uncapped + unpriced → unchanged (a null cost stays null and nothing blocks)
    public void Blocks_only_when_the_run_declares_a_cap(double? cap, bool expected) =>
        UnpricedModelUnderCap.Blocks(PoolModel, (decimal?)cap).ShouldBe(expected);

    [Fact]
    public void A_priced_model_is_never_blocked_however_it_got_its_price()
    {
        UnpricedModelUnderCap.Blocks(PoolModel, 5m, RowPriced).ShouldBeFalse("priced by the operator's row");
        UnpricedModelUnderCap.Blocks("claude-opus-4-8", 5m).ShouldBeFalse("priced by the built-in table");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unnamed_model_is_never_blocked(string? model) =>
        UnpricedModelUnderCap.Blocks(model, 5m).ShouldBeFalse("a blank model is the harness default, a name this layer never knew — not an unpriced pool pick");

    [Fact]
    public void The_refusal_detail_NAMES_the_model_and_both_remedies()
    {
        var detail = UnpricedModelUnderCap.Detail(PoolModel, 5m);

        detail.ShouldContain(PoolModel, Case.Sensitive, "an operator cannot act on a stop that doesn't say which model");
        detail.ShouldContain("5");
        detail.ShouldContain("model manager");
        detail.ShouldContain("remove the cap");
    }

    // ── The brain-plane admission point (LlmBudgetGuard) ─────────────────────────────

    [Fact]
    public void The_brain_plane_estimate_prices_a_pool_model_off_the_row_table()
    {
        LlmBudgetGuard.EstimateUsd(PoolModel, "sys", "user", 1000).ShouldBeNull("unpriceable everywhere");

        LlmBudgetGuard.EstimateUsd(PoolModel, "sys", "user", 1000, RowPriced).ShouldNotBeNull();
    }

    [Fact]
    public void The_unpriced_refusal_is_a_SIBLING_of_the_budget_refusal_not_a_subclass()
    {
        // The two demand different remedies (raise/drop the cap vs. price the model) and the turn loop stamps
        // different stop reasons; a subclass would let one catch swallow the other and report the wrong one.
        var refusal = new LlmUnpricedModelException("supervisor.decision", PoolModel, 5m);

        refusal.ShouldNotBeAssignableTo<LlmBudgetExceededException>();
        refusal.Model.ShouldBe(PoolModel);
        refusal.Detail.ShouldBe(UnpricedModelUnderCap.Detail(PoolModel, 5m));
        ((CodeSpace.Messages.Failures.IFailure)refusal).Kind.ShouldBe(CodeSpace.Messages.Failures.FailureKind.PreconditionRequired, "the remedy is nameable — price it, then the SAME call works");
    }

    // ── (3) The bounds gate ─────────────────────────────────────────────────────────

    [Fact]
    public void Bounds_force_stop_a_capped_run_that_already_spent_on_an_unpriced_model()
    {
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxCostUsd = 5m });

        // RunSpendUsd reads $0 precisely BECAUSE the model is unpriceable — the cap comparison alone would never
        // trip, which is the defect. The unpriced signal must stop it anyway.
        SupervisorBounds.PostDecision(Context(runSpend: 0m, unpriced: PoolModel), plan, Spawn("a"))
            .ShouldBe(SupervisorStopReasons.UnpricedModelUnderCap);
    }

    [Fact]
    public void An_UNCAPPED_run_with_the_same_unpriced_spend_is_untouched()
    {
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig());   // no MaxCostUsd

        SupervisorBounds.PostDecision(Context(runSpend: 0m, unpriced: PoolModel), plan, Spawn("a"))
            .ShouldBeNull("no cap means nothing to enforce — byte-identical to before D1");
    }

    [Fact]
    public void The_unpriced_stop_takes_PRECEDENCE_over_the_cost_cap_stop()
    {
        // Once an unpriced model is in the mix, RunSpendUsd is an underestimate of unknown size — reporting
        // "cost cap reached" would tell the operator to raise the cap, which fixes nothing.
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxCostUsd = 5m });

        SupervisorBounds.PostDecision(Context(runSpend: 99m, unpriced: PoolModel), plan, Spawn("a"))
            .ShouldBe(SupervisorStopReasons.UnpricedModelUnderCap);
    }

    [Fact]
    public void A_non_side_effecting_decision_is_still_never_bounded()
    {
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxCostUsd = 5m });

        SupervisorBounds.PostDecision(Context(runSpend: 0m, unpriced: PoolModel), plan, new SupervisorDecision { Kind = SupervisorDecisionKinds.Stop, PayloadJson = "{}" })
            .ShouldBeNull("a stop spends nothing — refusing it would strand the run instead of ending it");
    }

    [Fact]
    public void The_stop_reason_literal_is_pinned() =>
        // Surfaced to the operator + load-bearing for the deterministic re-derived stop (Rule 8).
        SupervisorStopReasons.UnpricedModelUnderCap.ShouldBe("unpriced model under cost cap");

    // ── The tape fold that feeds the bound ───────────────────────────────────────────

    [Fact]
    public void FirstUnpricedModel_names_a_model_that_actually_consumed_tokens()
    {
        var results = new List<SupervisorAgentResult>
        {
            new() { AgentRunId = Guid.NewGuid(), Status = "Succeeded", Model = "claude-opus-4-8", InputTokens = 1000, OutputTokens = 10 },
            new() { AgentRunId = Guid.NewGuid(), Status = "Succeeded", Model = PoolModel, InputTokens = 5000, OutputTokens = 500 },
        };

        SupervisorOutcome.FirstUnpricedModel(results).ShouldBe(PoolModel);
        SupervisorOutcome.FirstUnpricedModel(results, RowPriced).ShouldBeNull("the operator priced it");
    }

    [Fact]
    public void A_zero_token_result_is_NOT_an_unpriced_spend()
    {
        // A compact folded before the token fields existed, or an agent that captured no usage, cost nothing to
        // run whatever its model — force-stopping a capped run on it would be a false positive.
        var results = new List<SupervisorAgentResult> { new() { AgentRunId = Guid.NewGuid(), Status = "Succeeded", Model = PoolModel, InputTokens = 0, OutputTokens = 0 } };

        SupervisorOutcome.FirstUnpricedModel(results).ShouldBeNull();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private static SupervisorTurnContext Context(decimal runSpend, string? unpriced) =>
        new() { Goal = "g", TurnNumber = 1, RunSpendUsd = runSpend, UnpricedSpendModel = unpriced };

    private static SupervisorDecision Spawn(params string[] ids) => new()
    {
        Kind = SupervisorDecisionKinds.Spawn,
        PayloadJson = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = ids }, AgentJson.Options),
    };

    /// <summary>Sets the price-override env var for the duration of a test and restores the prior value — the table is read live off the process env.</summary>
    private sealed class PriceEnv : IDisposable
    {
        private readonly string? _prior = Environment.GetEnvironmentVariable(AgentCostPricing.PriceTableEnvVar);

        public PriceEnv(string value) => Environment.SetEnvironmentVariable(AgentCostPricing.PriceTableEnvVar, value);

        public void Dispose() => Environment.SetEnvironmentVariable(AgentCostPricing.PriceTableEnvVar, _prior);
    }
}
