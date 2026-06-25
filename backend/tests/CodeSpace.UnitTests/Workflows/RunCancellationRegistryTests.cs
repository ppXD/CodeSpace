using CodeSpace.Core.Services.Workflows.Engine;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pure-logic tests for the in-process cooperative-cancel bridge (<see cref="RunCancellationRegistry"/>): a
/// registered run's token trips on <see cref="IRunCancellationRegistry.Cancel"/>, an unregistered / disposed run
/// is a no-op, the linked token propagates, and a stale registration is replaced last-writer-wins.
/// </summary>
[Trait("Category", "Unit")]
public class RunCancellationRegistryTests
{
    [Fact]
    public void Cancel_trips_the_registered_runs_token()
    {
        var registry = new RunCancellationRegistry();
        var runId = Guid.NewGuid();

        using var registration = registry.Register(runId, CancellationToken.None);
        registration.Token.IsCancellationRequested.ShouldBeFalse();

        registry.Cancel(runId);

        registration.Token.IsCancellationRequested.ShouldBeTrue("an operator cancel for this run trips the walk's token");
    }

    [Fact]
    public void Cancel_for_an_unregistered_run_is_a_noop()
    {
        var registry = new RunCancellationRegistry();

        Should.NotThrow(() => registry.Cancel(Guid.NewGuid()));
    }

    [Fact]
    public void Disposing_a_registration_unregisters_so_a_later_cancel_does_not_throw()
    {
        var registry = new RunCancellationRegistry();
        var runId = Guid.NewGuid();

        var registration = registry.Register(runId, CancellationToken.None);
        registration.Dispose();

        Should.NotThrow(() => registry.Cancel(runId), "a finished+disposed walk's run is gone — a late cancel is a clean no-op");
    }

    [Fact]
    public void A_linked_token_cancellation_propagates_to_the_runs_token()
    {
        var registry = new RunCancellationRegistry();
        using var linked = new CancellationTokenSource();

        using var registration = registry.Register(Guid.NewGuid(), linked.Token);

        linked.Cancel();

        registration.Token.IsCancellationRequested.ShouldBeTrue("a host/shutdown cancel on the linked token still stops the walk");
    }

    [Fact]
    public void Re_registering_a_run_replaces_the_prior_source_last_writer_wins()
    {
        var registry = new RunCancellationRegistry();
        var runId = Guid.NewGuid();

        registry.Register(runId, CancellationToken.None);   // a stale entry a crashed walk left behind
        using var current = registry.Register(runId, CancellationToken.None);

        registry.Cancel(runId);

        current.Token.IsCancellationRequested.ShouldBeTrue("Cancel trips the current (latest) registration — the stale one was replaced");
    }
}
