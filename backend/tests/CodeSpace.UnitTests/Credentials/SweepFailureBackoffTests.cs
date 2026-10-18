using CodeSpace.Core.Services.Credentials;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace CodeSpace.UnitTests.Credentials;

/// <summary>
/// The memory a recurring sweep keeps about candidates it could not handle: a few failures in a row set a candidate aside for
/// a window, the window ends, a success forgets, and a failure from long ago is not held against a candidate that went quiet.
/// </summary>
[Trait("Category", "Unit")]
public class SweepFailureBackoffTests
{
    private const string Sweep = "test-sweep";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
    private readonly SweepFailureBackoff _backoff;
    private readonly Guid _candidate = Guid.NewGuid();

    public SweepFailureBackoffTests() { _backoff = new SweepFailureBackoff(_clock); }

    [Fact]
    public void A_candidate_is_set_aside_by_exactly_the_failure_that_reaches_the_threshold()
    {
        for (var failure = 1; failure < SweepFailureBackoff.FailuresBeforeBackoff; failure++)
        {
            _backoff.RecordFailure(Sweep, _candidate).ShouldBeFalse($"failure {failure} of {SweepFailureBackoff.FailuresBeforeBackoff}: still a candidate");
            _backoff.IsBackedOff(Sweep, _candidate).ShouldBeFalse();
        }

        _backoff.RecordFailure(Sweep, _candidate).ShouldBeTrue("the failure that sets it aside says so, so the sweep can log it once");
        _backoff.IsBackedOff(Sweep, _candidate).ShouldBeTrue();
    }

    [Fact]
    public void The_window_ends_and_one_more_failure_sets_the_candidate_aside_again()
    {
        SetAside();

        _clock.Advance(SweepFailureBackoff.BackoffWindow - TimeSpan.FromSeconds(1));
        _backoff.IsBackedOff(Sweep, _candidate).ShouldBeTrue("still inside the window");

        _clock.Advance(TimeSpan.FromSeconds(2));
        _backoff.IsBackedOff(Sweep, _candidate).ShouldBeFalse("the window ended, so the candidate is attempted once more");

        _backoff.RecordFailure(Sweep, _candidate).ShouldBeTrue("one failure — not a fresh three — sets it aside again: it already failed that many in a row");
        _backoff.IsBackedOff(Sweep, _candidate).ShouldBeTrue();
    }

    [Fact]
    public void A_success_forgets_the_failures()
    {
        for (var failure = 1; failure < SweepFailureBackoff.FailuresBeforeBackoff; failure++) _backoff.RecordFailure(Sweep, _candidate);

        _backoff.RecordSuccess(Sweep, _candidate);

        for (var failure = 1; failure < SweepFailureBackoff.FailuresBeforeBackoff; failure++) _backoff.RecordFailure(Sweep, _candidate).ShouldBeFalse("failures in a row, not failures in total");

        _backoff.IsBackedOff(Sweep, _candidate).ShouldBeFalse();
    }

    [Fact]
    public void A_failure_from_long_ago_is_not_held_against_a_candidate_that_went_quiet()
    {
        for (var failure = 1; failure < SweepFailureBackoff.FailuresBeforeBackoff; failure++) _backoff.RecordFailure(Sweep, _candidate);

        _clock.Advance(SweepFailureBackoff.ForgetAfter + TimeSpan.FromMinutes(1));

        _backoff.RecordFailure(Sweep, _candidate).ShouldBeFalse("judged afresh: this is its first failure");
        _backoff.IsBackedOff(Sweep, _candidate).ShouldBeFalse();
    }

    [Fact]
    public void What_is_set_aside_is_one_candidate_of_one_sweep()
    {
        SetAside();

        _backoff.IsBackedOff("another-sweep", _candidate).ShouldBeFalse("the same id in another sweep is another candidate");
        _backoff.IsBackedOff(Sweep, Guid.NewGuid()).ShouldBeFalse();
    }

    private void SetAside()
    {
        for (var failure = 0; failure < SweepFailureBackoff.FailuresBeforeBackoff; failure++) _backoff.RecordFailure(Sweep, _candidate);
    }
}
