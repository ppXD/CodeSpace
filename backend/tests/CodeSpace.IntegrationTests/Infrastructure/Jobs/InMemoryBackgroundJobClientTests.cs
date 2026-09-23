using Autofac;
using Shouldly;

namespace CodeSpace.IntegrationTests.Infrastructure.Jobs;

/// <summary>
/// The two ways the shared job client gets back to executing: a test's own <see cref="InMemoryBackgroundJobClient.ManualExecution"/>
/// scope, and the per-class <see cref="InMemoryBackgroundJobClient.Reset"/>. Both are what stand between one class's
/// record-only switch and the next class's drain, so each is pinned on the client itself — no database involved.
/// </summary>
[Trait("Category", "Unit")]
public sealed class InMemoryBackgroundJobClientTests : IDisposable
{
    private readonly IContainer _container = new ContainerBuilder().Build();

    public void Dispose() => _container.Dispose();

    [Fact]
    public void Manual_execution_records_without_queueing_then_hands_the_client_back_executing_and_empty()
    {
        var jobs = new InMemoryBackgroundJobClient(_container);

        using (jobs.ManualExecution())
        {
            jobs.Enqueue(() => Task.CompletedTask);

            jobs.AutoExecute.ShouldBeFalse();
            jobs.Calls.Count.ShouldBe(1, "record-only still records the dispatch a test asserts on");
            jobs.PendingCount.ShouldBe(0, "record-only must queue nothing a later drain would run");
        }

        jobs.AutoExecute.ShouldBeTrue("disposing the scope restores the value it found");
        jobs.Calls.ShouldBeEmpty("what the scope recorded belongs to the test that opened it");
    }

    [Fact]
    public void A_nested_manual_scope_restores_the_outer_scopes_value_not_the_default()
    {
        var jobs = new InMemoryBackgroundJobClient(_container);

        using (jobs.ManualExecution())
        {
            using (jobs.ManualExecution()) jobs.AutoExecute.ShouldBeFalse();

            jobs.AutoExecute.ShouldBeFalse("the inner scope hands back what IT found — the outer scope is still record-only");
        }

        jobs.AutoExecute.ShouldBeTrue();
    }

    [Fact]
    public void Reset_returns_the_client_to_its_constructed_state()
    {
        var jobs = new InMemoryBackgroundJobClient(_container);

        jobs.Enqueue(() => Task.CompletedTask);
        jobs.AutoExecute = false;
        jobs.ThrowOnEnqueue = new InvalidOperationException("armed by a class that never enqueued again");

        jobs.Reset();

        jobs.AutoExecute.ShouldBeTrue("the next class drains with the default it was written against");
        jobs.ThrowOnEnqueue.ShouldBeNull("an armed throw must not fire inside the next class's first dispatch");
        jobs.Calls.ShouldBeEmpty();
        jobs.PendingCount.ShouldBe(0, "a job the previous class never drained must not run inside the next class's drain");
    }
}
