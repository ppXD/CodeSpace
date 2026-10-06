using CodeSpace.Core.Services.Agents.Commands;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the per-run queue <c>agent.run_command</c> enters when an agent calls it. One entry per run at a time,
/// none across runs, a cancelled waiter gives its place back, and a run whose commands are done holds no lane.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CallerCommandLanesTests
{
    [Fact]
    public async Task A_second_entry_for_the_same_run_waits_until_the_first_leaves()
    {
        var lanes = new CallerCommandLanes();
        var runId = Guid.NewGuid();

        var first = await lanes.EnterAsync(runId, CancellationToken.None);
        var second = lanes.EnterAsync(runId, CancellationToken.None);

        second.IsCompleted.ShouldBeFalse("fixture check: the second entry starts out waiting");

        await first.DisposeAsync();
        await using var entered = await second.WaitAsync(TimeSpan.FromSeconds(5));

        lanes.Count.ShouldBe(1, "the run still has a command in its lane");
    }

    [Fact]
    public async Task Entries_for_different_runs_never_wait_on_each_other()
    {
        var lanes = new CallerCommandLanes();

        await using var first = await lanes.EnterAsync(Guid.NewGuid(), CancellationToken.None);
        var second = lanes.EnterAsync(Guid.NewGuid(), CancellationToken.None);

        second.IsCompletedSuccessfully.ShouldBeTrue("another run's command is not queued behind this one");
        await (await second).DisposeAsync();
    }

    [Fact]
    public async Task A_cancelled_waiter_gives_its_place_back_and_a_finished_run_holds_no_lane()
    {
        var lanes = new CallerCommandLanes();
        var runId = Guid.NewGuid();
        using var cancel = new CancellationTokenSource();

        var first = await lanes.EnterAsync(runId, CancellationToken.None);
        var waiter = lanes.EnterAsync(runId, cancel.Token);

        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => waiter);

        await first.DisposeAsync();

        lanes.Count.ShouldBe(0, "a run whose commands are all done (or gave up) leaves nothing behind");
        await (await lanes.EnterAsync(runId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).DisposeAsync();
    }
}
