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

    [Fact]
    public void Digest_changes_when_any_rate_or_source_changes()
    {
        // MUTATION THIS CATCHES: hashing only the model name, or only one of the two rates. The digest is what a
        // budget reservation stamps as its price_version and what a result carries as its audit stamp — if an output
        // rate doubling left the digest untouched, two admissions made under materially different prices would be
        // indistinguishable, which is the whole defect the snapshot exists to close.
        var baseline = ModelPriceSnapshot.Of(ModelPriceSources.CredentialRow, 2m, 10m);

        baseline.Digest.ShouldBe(ModelPriceSnapshot.Of(ModelPriceSources.CredentialRow, 2m, 10m).Digest, "the same source + rates must hash identically, or nothing can be compared across runs");
        baseline.Digest.ShouldNotBe(ModelPriceSnapshot.Of(ModelPriceSources.CredentialRow, 2.5m, 10m).Digest, "a changed INPUT rate must change the digest");
        baseline.Digest.ShouldNotBe(ModelPriceSnapshot.Of(ModelPriceSources.CredentialRow, 2m, 20m).Digest, "a changed OUTPUT rate must change the digest");
        baseline.Digest.ShouldNotBe(ModelPriceSnapshot.Of(ModelPriceSources.EnvOverride, 2m, 10m).Digest, "the same rates from a DIFFERENT table are a different pricing decision");
    }

    [Fact]
    public void Digest_is_culture_invariant_so_two_hosts_agree_on_one_price()
    {
        // A culture-dependent decimal format ("2,5" on de-DE) would mint two digests for one price, so a run admitted
        // on one worker would look re-priced on another. Pinned by driving the mint under a comma-decimal culture.
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            ModelPriceSnapshot.Of(ModelPriceSources.CredentialRow, 2.5m, 10m).Digest
                .ShouldBe("2a14725d968a2d08", "the canonical string is invariant-culture (sha256 of 'credential-model-row|2.5|10') — a host's locale must never change a price's identity");

            // Trailing scale is a storage artefact of NUMERIC(12,4), not a different price: 10 and 10.0000 must hash the same.
            ModelPriceSnapshot.Of(ModelPriceSources.CredentialRow, 2.5000m, 10.0000m).Digest.ShouldBe("2a14725d968a2d08");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void SnapshotFor_names_which_of_the_three_tables_priced_the_model()
    {
        // The resolution ORDER is the contract PriceFor already promises (row → env → built-in); this pins that the
        // snapshot NAMES the winner rather than guessing. MUTATION: checking the built-in table before the env would
        // attribute an operator's correction to the seeded defaults.
        var rowPrices = new Dictionary<string, ModelPrice> { ["claude-opus-4-8"] = new() { InputPerMillionUsd = 7m, OutputPerMillionUsd = 9m } };

        WithPriceEnv("claude-opus-4-8=6/30;gpt-5.4-codex=2/8", () =>
        {
            AgentCostPricing.SnapshotFor("claude-opus-4-8", rowPrices).ShouldNotBeNull().Source.ShouldBe(ModelPriceSources.CredentialRow, "the operator's own row outranks both the env and the built-in table");
            AgentCostPricing.SnapshotFor("gpt-5.4-codex", rowPrices).ShouldNotBeNull().Source.ShouldBe(ModelPriceSources.EnvOverride, "a model only the env names is priced BY the env");
            AgentCostPricing.SnapshotFor("claude-sonnet-4-6", rowPrices).ShouldNotBeNull().Source.ShouldBe(ModelPriceSources.BuiltIn, "a model neither the row nor the env names falls through to the seeded table");
        });

        AgentCostPricing.SnapshotFor("no-such-model").ShouldBeNull("an unpriced model has no snapshot — the same honest null PriceFor returns");
        AgentCostPricing.SnapshotFor(null).ShouldBeNull();
    }

    [Fact]
    public void SnapshotFor_carries_exactly_the_rates_PriceFor_would_use()
    {
        // The two must never diverge: a snapshot claiming rates the pricer did not apply is worse than no snapshot.
        WithPriceEnv("gpt-5.4-codex=2/8", () =>
        {
            var snapshot = AgentCostPricing.SnapshotFor("gpt-5.4-codex").ShouldNotBeNull();
            var price = AgentCostPricing.PriceFor("gpt-5.4-codex").ShouldNotBeNull();

            snapshot.InputUsdPerMillion.ShouldBe(price.InputPerMillionUsd);
            snapshot.OutputUsdPerMillion.ShouldBe(price.OutputPerMillionUsd);
        });
    }

    [Fact]
    public void Price_source_names_are_durable_state_and_pinned()
    {
        // These literals land in result_json and in budget_reservation.price_version (through the digest's input).
        // Renaming one splits the audit trail into two vocabularies and changes every future digest silently.
        ModelPriceSources.CredentialRow.ShouldBe("credential-model-row");
        ModelPriceSources.EnvOverride.ShouldBe("env-override");
        ModelPriceSources.BuiltIn.ShouldBe("built-in-table");
        ModelPriceSnapshot.UnpricedVersion.ShouldBe("unpriced");
    }

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
