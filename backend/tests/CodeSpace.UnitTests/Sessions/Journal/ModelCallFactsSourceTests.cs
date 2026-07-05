using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions.Journal.FactsSources;
using CodeSpace.Messages.Constants;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: the model-call mapper — turns a completed/failed interaction ledger record (+ its paired start) into the
/// structured facts the expanded model fold shows. Pins purpose/model/tokens off the payload, latency off the start↔
/// completion span, cost off the SHARED pricing (fail-open null on an unpriced model), and status off the record type.
/// A usage-silent call reads unknown tokens (not a bogus zero); an unpaired call reads null latency. Pure — no DB.
/// </summary>
[Trait("Category", "Unit")]
public class ModelCallFactsSourceTests
{
    [Fact]
    public void Maps_purpose_model_tokens_cost_and_latency_from_the_paired_records()
    {
        var start = Record(WorkflowRunRecordTypes.InteractionStarted, "{}", at: At(0));
        var completed = Record(WorkflowRunRecordTypes.InteractionCompleted,
            "{\"kind\":\"supervisor.decision\",\"model\":\"claude-opus-4-8\",\"usage\":{\"inputTokens\":1000,\"outputTokens\":200}}", at: At(5000));

        var call = ModelCallFactsSource.From(completed, start);

        call.Purpose.ShouldBe("supervisor.decision", "the interaction kind is the call's purpose");
        call.Model.ShouldBe("claude-opus-4-8");
        call.InputTokens.ShouldBe(1000);
        call.OutputTokens.ShouldBe(200);
        call.Tokens.ShouldBe(1200);
        call.LatencyMs.ShouldBe(5000, "the start→completion span");
        call.CostUsd.ShouldNotBeNull("claude-opus-4-8 is priced, so the per-call cost is known");
        call.Status.ShouldBe("completed");
        call.Error.ShouldBeNull("a completed call carries no error");
        call.FinishNote.ShouldBeNull("a call with no finish reason completed cleanly");
    }

    [Theory]
    // A completed-but-cut-off call carries a caution off the SAME finish classifier the timeline beat reads — so the
    // row doesn't read as a clean success on an incomplete answer. Status stays "completed" (it DID complete).
    [InlineData("max_tokens", "output truncated")]
    [InlineData("length", "output truncated")]
    [InlineData("content_filter", "content filtered")]
    [InlineData("stop", null)]
    [InlineData("end_turn", null)]
    public void A_cut_off_completion_carries_a_finish_note(string finishReason, string? expectedNote)
    {
        var payload = "{\"kind\":\"llm.complete\",\"model\":\"m\",\"usage\":{\"inputTokens\":9,\"outputTokens\":4000,\"finishReason\":\"NR\"}}".Replace("NR", finishReason);

        var call = ModelCallFactsSource.From(Record(WorkflowRunRecordTypes.InteractionCompleted, payload, At(0)), null);

        call.Status.ShouldBe("completed", "a truncated/filtered call still COMPLETED — the note is orthogonal to status");
        call.FinishNote.ShouldBe(expectedNote);
    }

    [Fact]
    public void A_failed_call_carries_no_finish_note()
    {
        var failed = Record(WorkflowRunRecordTypes.InteractionFailed, "{\"kind\":\"llm.complete\",\"usage\":{\"finishReason\":\"max_tokens\"}}", At(0));

        ModelCallFactsSource.From(failed, null).FinishNote.ShouldBeNull("a failed call's Error carries the reason, not a finish note");
    }

    [Fact]
    public void An_unpaired_call_has_null_latency()
    {
        var completed = Record(WorkflowRunRecordTypes.InteractionCompleted, "{\"kind\":\"llm.complete\",\"model\":\"claude-opus-4-8\",\"usage\":{\"inputTokens\":10,\"outputTokens\":5}}", at: At(0));

        ModelCallFactsSource.From(completed, start: null).LatencyMs.ShouldBeNull("no paired start → latency is unknown");
    }

    [Fact]
    public void An_unpriced_model_fails_open_to_null_cost_but_still_counts_tokens()
    {
        var completed = Record(WorkflowRunRecordTypes.InteractionCompleted, "{\"kind\":\"llm.complete\",\"model\":\"gpt-5.4-codex\",\"usage\":{\"inputTokens\":10,\"outputTokens\":5}}", at: At(0));

        var call = ModelCallFactsSource.From(completed, null);

        call.CostUsd.ShouldBeNull("an unpriced / unknown model fails open to null cost — never a bogus zero");
        call.Tokens.ShouldBe(15, "tokens are still counted");
    }

    [Fact]
    public void A_failed_usage_silent_call_reads_failed_with_null_tokens_and_cost()
    {
        var failed = Record(WorkflowRunRecordTypes.InteractionFailed, "{\"kind\":\"llm.complete\",\"model\":\"claude-opus-4-8\",\"error\":\"gateway timeout\"}", at: At(0));

        var call = ModelCallFactsSource.From(failed, null);

        call.Status.ShouldBe("failed");
        call.Tokens.ShouldBeNull("no usage → unknown, not zero");
        call.CostUsd.ShouldBeNull("no tokens → no cost");
        call.Error.ShouldBe("gateway timeout", "a failed call surfaces WHY it failed off the record, so the row shows the reason not a bare red 'FAILED'");
    }

    private static WorkflowRunRecord Record(string type, string payload, DateTimeOffset at) =>
        new() { Id = Guid.NewGuid(), RunId = Guid.NewGuid(), Sequence = 1, RecordType = type, PayloadJson = payload, OccurredAt = at };

    private static DateTimeOffset At(int ms) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(ms);
}
