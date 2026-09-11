namespace CodeSpace.Core.Services.Agents.AgentRunLogging;

/// <summary>
/// How a log capture stream waits out a TRANSIENT storage outage instead of throwing away the bytes it is holding.
///
/// <para>Before this existed the bridge had exactly one answer to a provider that would not accept a segment: give the
/// append its operation budget, then terminalize the stream and let the queued bytes fall on the floor. The tail of a
/// run's log was therefore lost to a fault the provider itself reported as retryable — a 503 that would have cleared
/// in seconds cost the operator the end of the transcript, and nothing said so. Backpressure is the honest answer: no
/// offset advances, no segment is dropped, the durable head stays exactly where it was, and the sandbox spool keeps
/// being the buffer it already is until the provider comes back.</para>
///
/// <para><b>The values are committed, not configured.</b> There is no environment switch for any of them: an operator
/// who needs a different ceiling changes it here, in a pull request, where the change is reviewable and the test below
/// pins it. Every one of them is also a CEILING rather than a schedule — the loop reacts to what the provider actually
/// does, and these only bound how long and how much a stall may cost before the loss has to be named.</para>
/// </summary>
public sealed record CaptureBackpressureOptions
{
    /// <summary>First wait after a transient refusal; each further attempt doubles it up to <see cref="DefaultRetryCeiling"/>, with jitter so N stalled streams do not re-converge on one provider. Pinned by a test (Rule 8).</summary>
    public static readonly TimeSpan DefaultRetryBase = TimeSpan.FromSeconds(5);

    /// <summary>The longest a stalled stream waits between attempts. A recovering provider is noticed within two minutes rather than at the end of the park window.</summary>
    public static readonly TimeSpan DefaultRetryCeiling = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long one stream may hold its queued segments before the outage stops being something to wait out. Sized
    /// against a provider incident rather than a blip: 30 minutes outlives a regional failover or a credential
    /// rotation, and a run whose agent is still working keeps every byte it produced across one.
    /// </summary>
    public static readonly TimeSpan DefaultParkAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How many source bytes one stalled stream may hold in memory while it drains the sandbox spool ahead of a
    /// provider that is not accepting them. 16 MiB is sixteen maximum-size segments — enough that a short outage never
    /// stops the spool being drained, and small enough that every stream of every concurrent run on a worker can hold
    /// one. It is deliberately far below the spool's own cap (<c>LocalProcessRunner.SpoolCapBytes</c>, 2 GiB by
    /// default): the spool is the durable buffer, this is only the window being drained out of it, and a stall that
    /// fills this window has outgrown what a worker can hold and has to name its loss instead of pretending otherwise.
    /// Pinned by a test (Rule 8).
    ///
    /// <para><b>It is a ceiling checked BETWEEN reads, not a hard cap on the buffer.</b> The pump tests the window
    /// before each read, so a read already in flight can carry the held span up to one maximum segment (1 MiB) past
    /// this value, and the park lands on the next attempt. Sized for that on purpose: moving the test after the read
    /// would buy an exactness nothing needs — the value is one worker's memory budget, not a correctness boundary —
    /// at the cost of a hotter loop and a longer pump.</para>
    /// </summary>
    public const long DefaultMaxLocalBacklogBytes = 16L * 1024 * 1024;

    /// <summary>The terminal capture code a stream carries when a transient outage outlived every ceiling here. Pinned by a test: it is the string an operator greps for.</summary>
    public const string RemoteOutageExhaustedCode = "capture.remote-outage-exhausted";

    /// <summary>The committed production ceilings. Tests construct their own instance; nothing reads one from the environment.</summary>
    public static readonly CaptureBackpressureOptions Default = new();

    public TimeSpan RetryBase { get; init; } = DefaultRetryBase;
    public TimeSpan RetryCeiling { get; init; } = DefaultRetryCeiling;
    public TimeSpan ParkAfter { get; init; } = DefaultParkAfter;
    public long MaxLocalBacklogBytes { get; init; } = DefaultMaxLocalBacklogBytes;

    /// <summary>
    /// The wait before attempt <paramref name="attempts"/> of a stalled stream: <see cref="RetryBase"/> doubled per
    /// prior attempt, clamped to <see cref="RetryCeiling"/>, then jittered down by up to a quarter. Jitter only ever
    /// SHORTENS the wait, so the ceiling stays a real ceiling and a recovering provider is never noticed late.
    /// </summary>
    public TimeSpan RetryDelay(int attempts, Random jitter)
    {
        var exponent = Math.Min(Math.Max(attempts - 1, 0), 8);
        var scaled = Math.Min(RetryBase.Ticks * Math.Pow(2, exponent), RetryCeiling.Ticks);

        return TimeSpan.FromTicks((long)(scaled * (1 - jitter.NextDouble() * 0.25)));
    }
}
