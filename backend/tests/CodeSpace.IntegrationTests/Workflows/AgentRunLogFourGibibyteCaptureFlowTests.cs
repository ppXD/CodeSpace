using CodeSpace.IntegrationTests.Infrastructure;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The 4 GiB case: four times <see cref="int.MaxValue"/> of agent log through the whole real write path, asserted for
/// contiguity, end-to-end digest equality and bounded memory (see <see cref="SyntheticCaptureHarness"/>). Nothing had
/// ever driven a total past 32 bits, so until this existed every claim that the heads are <c>long</c> and the reads
/// clamped was a claim about source text.
///
/// <para><b>Tier: <c>Category=LargeCapture</c>, its own lane.</b> It needs exactly what the Integration tier needs —
/// local PostgreSQL, no live model, no live bucket, no privileged container — so none of the repo's existing
/// long-running categories describes it, and borrowing one would either misdescribe it or park it behind
/// fully-qualified-name filters that would never select it. What disqualifies it from the default gate is only cost:
/// about nine minutes, against a few seconds for the 32 MiB guard in
/// <see cref="AgentRunLogLargeCaptureFlowTests"/>. So it follows the convention the <c>Sandbox</c> tier already uses
/// — a category the default gate excludes by name, and a dedicated workflow that runs exactly it and asserts it was
/// not silently skipped (<c>.github/workflows/large-capture.yml</c>). It is one <c>--filter</c> away locally:
/// <c>dotnet test backend/tests/CodeSpace.IntegrationTests --filter "Category=LargeCapture"</c>.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "LargeCapture")]
public sealed class AgentRunLogFourGibibyteCaptureFlowTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_four_gibibyte_log_finalizes_over_contiguous_segments_without_the_payload_being_held()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(40));

        await new SyntheticCaptureHarness(fixture).RunAsync(SyntheticCaptureHarness.FourGibibytes, deadline.Token);
    }
}
