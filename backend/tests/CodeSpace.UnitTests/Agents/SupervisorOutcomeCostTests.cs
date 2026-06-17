using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the SOTA #4 capture→ledger→bound SEAM, pinned WITHOUT a DB. The supervisor's realized-spend cost bound
/// reads its figure off the DURABLE spawn outcome (no new query), so the priced inputs (tokens + model) MUST ride
/// inside the folded <c>agentResults</c> array and survive the round-trip through <see cref="SupervisorOutcome.FoldAgentResults"/>
/// / <see cref="SupervisorOutcome.ReadAgentResults"/>. These tests prove: (1) <see cref="SupervisorOutcome.ProjectCompact"/>
/// stamps the priced inputs onto the compact result; (2) <see cref="SupervisorOutcome.SpendUsd"/> sums priced spend +
/// fails open on an unpriceable agent; (3) the fold round-trips those inputs so <c>FoldRunSpendUsd</c> re-derives the
/// same spend off the ledger on every rehydrate. If this seam breaks, the cost cap silently reads $0 and never trips.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorOutcomeCostTests
{
    [Fact]
    public void ProjectCompact_stamps_the_priced_inputs_from_the_result_and_the_passed_model()
    {
        var id = Guid.NewGuid();
        var resultJson = ResultJson(input: 200_000, output: 40_000, summary: "did the thing");

        var compact = SupervisorOutcome.ProjectCompact(id, "Succeeded", rowError: null, resultJson, model: "claude-opus-4-8");

        compact.InputTokens.ShouldBe(200_000);
        compact.OutputTokens.ShouldBe(40_000);
        compact.Model.ShouldBe("claude-opus-4-8");
    }

    [Fact]
    public void ProjectCompact_defaults_to_zero_tokens_and_null_model_when_unknown()
    {
        // A cancelled/abandoned agent (null ResultJson) or a usage-silent harness contributes nothing — never a throw,
        // never a phantom cost. Model null when the caller has no TaskJson.
        var compact = SupervisorOutcome.ProjectCompact(Guid.NewGuid(), "Cancelled", rowError: "operator cancelled", resultJson: null, model: null);

        compact.InputTokens.ShouldBe(0);
        compact.OutputTokens.ShouldBe(0);
        compact.Model.ShouldBeNull();
        compact.Error.ShouldBe("operator cancelled");
    }

    [Fact]
    public void SpendUsd_prices_the_known_models_and_sums_them()
    {
        // opus-4-8 = $5/M input + $25/M output. 1M input + 1M output → $5 + $25 = $30.
        var results = new[]
        {
            Compact(input: 1_000_000, output: 1_000_000, model: "claude-opus-4-8"),   // $30
            Compact(input: 1_000_000, output: 0, model: "claude-sonnet-4-6"),         // $3/M → $3
        };

        SupervisorOutcome.SpendUsd(results).ShouldBe(33m);
    }

    [Fact]
    public void SpendUsd_fails_open_on_an_unpriceable_agent_counting_only_the_known_ones()
    {
        var results = new[]
        {
            Compact(input: 1_000_000, output: 1_000_000, model: "claude-opus-4-8"),   // $30 (known)
            Compact(input: 9_000_000, output: 9_000_000, model: null),                // unknown model → 0
            Compact(input: 9_000_000, output: 9_000_000, model: "gpt-5-codex"),       // not in the default table → 0
        };

        // The unpriceable agents contribute 0 — a usage-silent / unknown-model agent can NEVER inflate the bill nor
        // (since this is the spend the cap reads) block the run. The agent-COUNT cap is the hard bound on them.
        SupervisorOutcome.SpendUsd(results).ShouldBe(30m);
    }

    [Fact]
    public void SpendUsd_is_zero_for_an_empty_or_all_unknown_set()
    {
        SupervisorOutcome.SpendUsd(Array.Empty<SupervisorAgentResult>()).ShouldBe(0m);
        SupervisorOutcome.SpendUsd(new[] { Compact(1, 1, model: null) }).ShouldBe(0m);
    }

    [Fact]
    public void The_priced_inputs_survive_the_fold_round_trip_so_the_bound_reads_them_off_the_ledger()
    {
        // The SEAM: a spawn outcome carrying agentRunIds + agentCount → fold in the compact results → read them back.
        // The spend computed off the READ-BACK results must equal the spend off the original — proving the durable
        // ledger row carries the priced inputs that FoldRunSpendUsd sums on rehydrate (no new query, replay-stable).
        var id = Guid.NewGuid();
        var spawnOutcome = JsonSerializer.Serialize(new { agentRunIds = new[] { id.ToString() }, agentCount = 1 }, AgentJson.Options);

        var folded = new[] { Compact(input: 1_000_000, output: 1_000_000, model: "claude-opus-4-8", id: id) };
        var foldedJson = SupervisorOutcome.FoldAgentResults(spawnOutcome, folded);

        var readBack = SupervisorOutcome.ReadAgentResults(foldedJson);

        readBack.Count.ShouldBe(1);
        readBack[0].InputTokens.ShouldBe(1_000_000);
        readBack[0].OutputTokens.ShouldBe(1_000_000);
        readBack[0].Model.ShouldBe("claude-opus-4-8");
        SupervisorOutcome.SpendUsd(readBack).ShouldBe(30m, "the priced inputs round-trip through the durable outcome so the cost bound re-derives the spend");
    }

    [Fact]
    public void The_fold_preserves_the_staged_agent_count_so_the_count_cap_counter_is_unperturbed()
    {
        // Folding agentResults must NOT disturb agentCount — the E5 total-spawn counter reads it off the same outcome.
        var id = Guid.NewGuid();
        var spawnOutcome = JsonSerializer.Serialize(new { agentRunIds = new[] { id.ToString() }, agentCount = 1 }, AgentJson.Options);

        var foldedJson = SupervisorOutcome.FoldAgentResults(spawnOutcome, new[] { Compact(10, 10, "claude-opus-4-8", id) });

        SupervisorOutcome.ReadStagedAgentCount(foldedJson).ShouldBe(1);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private static SupervisorAgentResult Compact(int input, int output, string? model, Guid? id = null) => new()
    {
        AgentRunId = id ?? Guid.NewGuid(),
        Status = "Succeeded",
        InputTokens = input,
        OutputTokens = output,
        Model = model,
    };

    private static string ResultJson(int input, int output, string? summary) => JsonSerializer.Serialize(new AgentRunResult
    {
        Status = AgentRunStatus.Succeeded,
        ExitReason = "completed",
        Summary = summary,
        TokenUsage = new AgentTokenUsage { InputTokens = input, OutputTokens = output },
    }, AgentJson.Options);
}
