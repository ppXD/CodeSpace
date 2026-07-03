using CodeSpace.Core.Services.Supervisor;
using Shouldly;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>
/// The free fix: the stop decision's outcome (<c>{ stopped, outcome, summary }</c>) carries the model's closing line,
/// but nothing read it. <see cref="SupervisorOutcome.ReadStopSummary"/> surfaces it (best-effort — absent / malformed
/// reads null), so the room's turn headline + the phase board can both show "here's what I did".
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorOutcomeStopSummaryTests
{
    [Fact]
    public void Reads_the_summary_field_a_stop_outcome_recorded()
    {
        const string outcome = """{"stopped":true,"outcome":"completed","summary":"Shipped the feature and the tests pass."}""";

        SupervisorOutcome.ReadStopSummary(outcome).ShouldBe("Shipped the feature and the tests pass.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"stopped":true,"outcome":"completed"}""")]
    [InlineData("""{"summary":123}""")]
    public void A_missing_or_malformed_summary_reads_null(string? outcomeJson)
    {
        SupervisorOutcome.ReadStopSummary(outcomeJson).ShouldBeNull();
    }

    [Fact]
    public void Reads_the_retry_rationale_why_and_evidence()
    {
        const string payload = """{"subtaskId":"sc","rationale":{"why":"The first attempt only skimmed one engine.","evidence":"attempt 1 exited with 'search tool rate-limited'."}}""";

        var (why, evidence) = SupervisorOutcome.ReadRetryRationale(payload);

        why.ShouldBe("The first attempt only skimmed one engine.");
        evidence.ShouldBe("attempt 1 exited with 'search tool rate-limited'.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"subtaskId":"sc"}""")]
    [InlineData("""{"subtaskId":"sc","rationale":{}}""")]
    [InlineData("""{"subtaskId":"sc","rationale":{"why":42}}""")]
    public void A_missing_or_malformed_retry_rationale_reads_nulls(string? payloadJson)
    {
        SupervisorOutcome.ReadRetryRationale(payloadJson).ShouldBe((null, null));
    }

    [Theory]
    // The generic reader surfaces the root rationale off ANY verb's payload — a plan, a spawn, a stop — not just retry.
    [InlineData("""{"goal":"ship it","subtasks":[],"rationale":{"why":"decompose first","evidence":"two subsystems"}}""")]
    [InlineData("""{"subtaskIds":["s1"],"rationale":{"why":"decompose first","evidence":"two subsystems"}}""")]
    [InlineData("""{"outcome":"completed","summary":"done","rationale":{"why":"decompose first","evidence":"two subsystems"}}""")]
    public void Reads_the_rationale_off_any_verb_payload_root(string payloadJson)
    {
        var (why, evidence) = SupervisorOutcome.ReadRationale(payloadJson);

        why.ShouldBe("decompose first");
        evidence.ShouldBe("two subsystems");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]                                   // a resolve with no note freezes as "{}" → no rationale
    [InlineData("""{"outcome":"completed"}""")]
    [InlineData("""{"rationale":{"evidence":""}}""")]    // empty evidence, no why → both null
    // rationale present but NOT an object (string / array / number / null) → the ValueKind guard reads nulls
    [InlineData("""{"rationale":"because"}""")]
    [InlineData("""{"rationale":[{"why":"w"}]}""")]
    [InlineData("""{"rationale":42}""")]
    [InlineData("""{"rationale":null}""")]
    // a valid-JSON but NON-OBJECT root (array / number / bare string) → the root ValueKind guard reads nulls
    [InlineData("[]")]
    [InlineData("123")]
    [InlineData("\"x\"")]
    public void A_missing_or_malformed_rationale_reads_nulls(string? payloadJson)
    {
        SupervisorOutcome.ReadRationale(payloadJson).ShouldBe((null, null));
    }

    [Fact]
    public void A_historical_three_key_retry_payload_still_reads_its_rationale()
    {
        // The pre-hoist 3-key retry shape {subtaskId, revisedInstruction, rationale} — a real historical row must still
        // yield its why/evidence through the generic reader (root .rationale), no migration.
        const string payload = """{"subtaskId":"sc","revisedInstruction":"try the injected clock","rationale":{"why":"w","evidence":"e"}}""";

        SupervisorOutcome.ReadRationale(payload).ShouldBe(("w", "e"));
    }

    [Fact]
    public void The_retry_rationale_reader_delegates_to_the_generic_reader()
    {
        // Assert the CONCRETE tuple (not just equality of the two readers, which passes even if both return nulls) —
        // pinning that a historical retry PayloadJson literal still yields its why/evidence through the retry entry point.
        const string payload = """{"subtaskId":"sc","rationale":{"why":"w","evidence":"e"}}""";

        SupervisorOutcome.ReadRetryRationale(payload).ShouldBe(("w", "e"), "ReadRetryRationale is a thin alias over the generic reader");
    }
}
