using CodeSpace.IntegrationTests.Infrastructure;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The default-gate guard for the large-log capture path: a multi-segment log through the whole real write path, with
/// the same contiguity, digest and memory assertions the 4 GiB case makes (see
/// <see cref="SyntheticCaptureHarness"/>). 32 MiB plus an odd remainder is thirty-three segments — thirty-two at the
/// bridge's ceiling and one short tail — so it drives manifest verification across five of its eight-segment pages,
/// the last of them partial, and it is the only tier where a segment is not exactly maximal.
///
/// <para>Its memory fence is a fraction of the payload rather than the absolute ceiling
/// (<c>SyntheticCaptureHarness.LiveHeapCeiling</c>), so a path that accumulated this log is caught here in seconds
/// too. What this tier provably cannot see is a total or an offset truncated to 32 bits — 32 MiB fits in an
/// <c>int</c> — which is what the 4 GiB tier is for.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunLogLargeCaptureFlowTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_multi_segment_log_finalizes_over_contiguous_segments_without_the_payload_being_held()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        await new SyntheticCaptureHarness(fixture).RunAsync(SyntheticCaptureHarness.SmallCaptureBytes, deadline.Token);
    }
}
