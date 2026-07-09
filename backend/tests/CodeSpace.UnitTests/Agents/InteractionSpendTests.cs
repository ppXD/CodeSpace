using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Cost;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>Pins <see cref="InteractionSpend.From"/> + <see cref="BrainPlaneSpendSummary.From"/> (P3.5) — pricing an <c>interaction.completed</c> ledger row into a per-kind spend figure, fail-open on anything malformed/unpriceable.</summary>
public sealed class InteractionSpendTests
{
    [Fact]
    public void A_well_formed_record_prices_its_kind_model_and_tokens()
    {
        var record = Record("""{"kind":"supervisor.decision","model":"claude-opus-4-8","usage":{"inputTokens":1000000,"outputTokens":1000000}}""");

        var row = InteractionSpend.From(record);

        row.Kind.ShouldBe("supervisor.decision");
        row.Model.ShouldBe("claude-opus-4-8");
        row.InputTokens.ShouldBe(1_000_000);
        row.OutputTokens.ShouldBe(1_000_000);
        row.CostUsd.ShouldBe(30m, "opus-4-8 is $5/M input + $25/M output — 1M + 1M = $30");
    }

    [Fact]
    public void An_unknown_model_prices_null_not_zero()
    {
        var record = Record("""{"kind":"critic.review","model":"some-unpriced-model","usage":{"inputTokens":1000,"outputTokens":1000}}""");

        InteractionSpend.From(record).CostUsd.ShouldBeNull("an unpriceable model is UNKNOWN cost, not a bogus $0");
    }

    [Fact]
    public void A_missing_model_prices_null()
    {
        var record = Record("""{"kind":"supervisor.decision","usage":{"inputTokens":1000,"outputTokens":1000}}""");

        InteractionSpend.From(record).CostUsd.ShouldBeNull();
    }

    [Fact]
    public void A_missing_kind_defaults_to_unknown_never_throws()
    {
        var record = Record("""{"model":"claude-opus-4-8","usage":{"inputTokens":1,"outputTokens":1}}""");

        InteractionSpend.From(record).Kind.ShouldBe("unknown");
    }

    [Fact]
    public void Malformed_json_fails_open_to_zero_tokens_and_unknown_kind_never_throws()
    {
        var record = Record("not json at all {{{");

        var row = InteractionSpend.From(record);

        row.Kind.ShouldBe("unknown");
        row.Model.ShouldBeNull();
        row.InputTokens.ShouldBe(0);
        row.OutputTokens.ShouldBe(0);
        row.CostUsd.ShouldBeNull();
    }

    [Fact]
    public void Missing_usage_fields_default_to_zero_tokens()
    {
        var record = Record("""{"kind":"grader.acceptance","model":"claude-haiku-4-5"}""");

        var row = InteractionSpend.From(record);

        row.InputTokens.ShouldBe(0);
        row.OutputTokens.ShouldBe(0);
        row.CostUsd.ShouldBe(0m, "a known model with zero usage prices to a real $0, not unknown");
    }

    [Fact]
    public void Summary_sums_only_known_rows_grouped_by_kind()
    {
        var rows = new[]
        {
            InteractionSpend.From(Record("""{"kind":"supervisor.decision","model":"claude-opus-4-8","usage":{"inputTokens":1000000,"outputTokens":0}}""")),   // $5
            InteractionSpend.From(Record("""{"kind":"supervisor.decision","model":"claude-opus-4-8","usage":{"inputTokens":1000000,"outputTokens":0}}""")),   // $5
            InteractionSpend.From(Record("""{"kind":"critic.review","model":"claude-haiku-4-5","usage":{"inputTokens":1000000,"outputTokens":0}}""")),         // $1
            InteractionSpend.From(Record("""{"kind":"grader.acceptance","model":"some-unpriced-model","usage":{"inputTokens":1000000,"outputTokens":0}}""")),  // unknown → excluded
        };

        var summary = BrainPlaneSpendSummary.From(rows);

        summary.TotalUsd.ShouldBe(11m, "the two supervisor.decision rows ($5 each) plus the critic.review row ($1) — the unpriceable grader row contributes nothing");
        summary.ByKind["supervisor.decision"].ShouldBe(10m);
        summary.ByKind["critic.review"].ShouldBe(1m);
        summary.ByKind.ContainsKey("grader.acceptance").ShouldBeFalse("a kind with ONLY unpriceable rows is absent, never a bogus $0 entry");
    }

    [Fact]
    public void An_empty_or_all_unpriceable_set_summarizes_to_the_shared_Empty_instance()
    {
        BrainPlaneSpendSummary.From(Array.Empty<InteractionSpendRow>()).ShouldBe(BrainPlaneSpendSummary.Empty);

        var allUnknown = new[] { InteractionSpend.From(Record("""{"kind":"x","model":"unpriced","usage":{"inputTokens":1,"outputTokens":1}}""")) };
        BrainPlaneSpendSummary.From(allUnknown).ShouldBe(BrainPlaneSpendSummary.Empty);
    }

    private static WorkflowRunRecord Record(string payloadJson) => new()
    {
        Id = Guid.NewGuid(),
        RunId = Guid.NewGuid(),
        Sequence = 1,
        RecordType = "interaction.completed",
        PayloadJson = payloadJson,
    };
}
