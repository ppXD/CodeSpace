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
}
