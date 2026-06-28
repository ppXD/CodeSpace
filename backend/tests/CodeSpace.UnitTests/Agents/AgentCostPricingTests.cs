using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the pure pricing helper (SOTA #4) — tokens → USD. Pins the Rule-8 env-var const name, the seeded
/// per-1M math for known models, the fail-OPEN null for an unknown/blank model (never $0, never a throw), and the
/// lenient env override (operator-correctable drift + Codex prices; a malformed entry is skipped, not fatal).
/// </summary>
[Trait("Category", "Unit")]
[Collection("ModelPriceEnvMutation")]   // serialize the price-env mutator against the parallel suite — it mutates a process-global env var live price-readers consult
public sealed class AgentCostPricingTests
{
    [Fact]
    public void PriceTableEnvVar_name_is_pinned() =>
        // Renaming this silently strands every operator who corrected a price / added Codex via the env (Rule 8).
        AgentCostPricing.PriceTableEnvVar.ShouldBe("CODESPACE_AGENT_MODEL_PRICES");

    [Theory]
    [InlineData("claude-opus-4-8", 1_000_000, 1_000_000, 30)]   // 5 in + 25 out
    [InlineData("claude-sonnet-4-6", 1_000_000, 0, 3)]
    [InlineData("claude-haiku-4-5", 0, 1_000_000, 5)]
    [InlineData("claude-opus-4-8", 200_000, 100_000, 3.5)]      // 0.2*5 + 0.1*25
    public void Known_models_price_per_million(string model, int input, int output, decimal expectedUsd) =>
        AgentCostPricing.CostUsd(model, input, output).ShouldBe(expectedUsd);

    [Theory]
    [InlineData("gpt-5.4-codex")]   // Codex absent from the default table by design → unknown
    [InlineData("some-future-model")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Unknown_or_blank_model_is_null_not_zero(string? model) =>
        AgentCostPricing.CostUsd(model, 1_000_000, 1_000_000).ShouldBeNull("cost-unknown fails OPEN — null, never $0 (which would read as free) and never a throw");

    [Fact]
    public void Model_match_is_case_insensitive_and_trimmed() =>
        AgentCostPricing.CostUsd(" CLAUDE-OPUS-4-8 ", 1_000_000, 0).ShouldBe(5);

    [Fact]
    public void TokenUsage_and_Model_round_trip_through_AgentJson_so_the_priced_inputs_are_durable()
    {
        // SOTA #4 prices a CAPTURED-then-persisted figure: AgentRunResult.TokenUsage (the run result jsonb) + the
        // AgentTask.Model (the task jsonb). Pin that BOTH survive the canonical AgentJson serialization the ledger
        // uses — if they didn't, the read plane + the cost bound would silently price $0 off a dropped field.
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", TokenUsage = new AgentTokenUsage { InputTokens = 123_456, OutputTokens = 7_890 } };
        var task = new AgentTask { Goal = "g", Harness = "claude-code", Model = "claude-opus-4-8" };

        var rehydratedResult = JsonSerializer.Deserialize<AgentRunResult>(JsonSerializer.Serialize(result, AgentJson.Options), AgentJson.Options)!;
        var rehydratedTask = JsonSerializer.Deserialize<AgentTask>(JsonSerializer.Serialize(task, AgentJson.Options), AgentJson.Options)!;

        rehydratedResult.TokenUsage.ShouldNotBeNull();
        rehydratedResult.TokenUsage!.InputTokens.ShouldBe(123_456);
        rehydratedResult.TokenUsage.OutputTokens.ShouldBe(7_890);
        rehydratedTask.Model.ShouldBe("claude-opus-4-8");

        // …and end-to-end: the rehydrated figures price exactly as the live ones would.
        AgentCostPricing.CostUsd(rehydratedTask.Model, rehydratedResult.TokenUsage.InputTokens, rehydratedResult.TokenUsage.OutputTokens)
            .ShouldBe(AgentCostPricing.CostUsd("claude-opus-4-8", 123_456, 7_890));
    }

    [Fact]
    public void Env_override_adds_a_codex_price_and_overrides_a_default()
    {
        WithPriceEnv("gpt-5.4-codex=2/8;claude-opus-4-8=6/30", () =>
        {
            AgentCostPricing.CostUsd("gpt-5.4-codex", 1_000_000, 1_000_000).ShouldBe(10, "the env adds a price for a previously-unknown model");
            AgentCostPricing.CostUsd("claude-opus-4-8", 1_000_000, 0).ShouldBe(6, "the env overrides the seeded default for a known model");
            AgentCostPricing.CostUsd("claude-sonnet-4-6", 1_000_000, 0).ShouldBe(3, "a model the env did NOT mention keeps its seeded default");
        });
    }

    [Fact]
    public void Env_override_skips_malformed_entries_without_crashing()
    {
        WithPriceEnv("garbage;no-equals;m=onlyone;m2=a/b;gpt-5.4-codex=2/8", () =>
        {
            // Every malformed entry (no '=', single price, non-numeric) is skipped; the one valid entry still applies.
            AgentCostPricing.CostUsd("gpt-5.4-codex", 1_000_000, 0).ShouldBe(2, "the lone valid entry parses; the garbage around it is tolerated");
            AgentCostPricing.CostUsd("m", 1_000_000, 0).ShouldBeNull("a single-value entry is malformed → skipped → still unknown");
            AgentCostPricing.CostUsd("claude-opus-4-8", 1_000_000, 0).ShouldBe(5, "defaults survive a malformed override");
        });
    }

    [Fact]
    public void An_absurd_env_price_is_skipped_so_pricing_never_overflows_into_a_throw()
    {
        // A fat-fingered price big enough to overflow decimal * int.MaxValue would crash supervisor rehydrate AND
        // 500 the cost read query (neither catches OverflowException). It must be SKIPPED at parse, never priced.
        WithPriceEnv("m=99999999999999999999/1", () =>
        {
            Should.NotThrow(() => AgentCostPricing.CostUsd("m", int.MaxValue, int.MaxValue));
            AgentCostPricing.CostUsd("m", int.MaxValue, int.MaxValue).ShouldBeNull("the absurd price is out of range → entry skipped → model unknown");
        });

        // And a SANE max-tier price at int.MaxValue tokens stays well within decimal range (no overflow).
        Should.NotThrow(() => AgentCostPricing.CostUsd("claude-fable-5", int.MaxValue, int.MaxValue));
    }

    [Theory]
    [InlineData("m=2,5/1")]    // comma is AMBIGUOUS (decimal vs thousands) → rejected, not silently parsed as 25
    [InlineData("m=1/2,5")]
    public void Env_override_rejects_thousands_separator_ambiguous_prices(string entry)
    {
        WithPriceEnv(entry, () => AgentCostPricing.CostUsd("m", 1_000_000, 1_000_000).ShouldBeNull("a comma-bearing price is ambiguous → skipped → model unknown (never a 10x misparse)"));
    }

    [Fact]
    public void Negative_token_counts_clamp_to_zero_so_a_corrupt_count_cannot_produce_a_negative_cost()
    {
        // A harness/corrupt result reporting a negative token count must never yield a negative cost — that would
        // SUBTRACT from the summed spend the cost cap reads, masking real spend.
        AgentCostPricing.CostUsd("claude-opus-4-8", -1_000_000, -1_000_000).ShouldBe(0m);
        AgentCostPricing.CostUsd("claude-opus-4-8", -1_000_000, 1_000_000).ShouldBe(25m, "the negative input clamps to 0; the positive output still prices");
    }

    [Fact]
    public void MaxPricePerMillionUsd_const_is_pinned() =>
        AgentCostPricing.MaxPricePerMillionUsd.ShouldBe(100_000m);

    private static void WithPriceEnv(string value, Action body)
    {
        var original = Environment.GetEnvironmentVariable(AgentCostPricing.PriceTableEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AgentCostPricing.PriceTableEnvVar, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentCostPricing.PriceTableEnvVar, original);
        }
    }
}
