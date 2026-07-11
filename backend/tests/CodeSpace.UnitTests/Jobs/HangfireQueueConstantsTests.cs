using CodeSpace.Core.Constants;
using Shouldly;

namespace CodeSpace.UnitTests.Jobs;

/// <summary>
/// Pins the Hangfire queue names (Rule 8). The control-plane pool and the agent pool are configured against these
/// exact strings, and the agent.run executor enqueues route to <see cref="HangfireConstants.AgentQueue"/> — so a
/// rename here without updating both the server pools AND the enqueue sites would silently leave agent jobs on a
/// queue NO server processes (they'd never run) or collapse the isolation. A hard pin makes the rename test-visible.
/// </summary>
[Trait("Category", "Unit")]
public class HangfireQueueConstantsTests
{
    [Fact]
    public void Queue_names_are_pinned()
    {
        HangfireConstants.DefaultQueue.ShouldBe("default");
        HangfireConstants.AgentQueue.ShouldBe("agents");
    }

    [Fact]
    public void The_two_queues_are_distinct()
    {
        HangfireConstants.AgentQueue.ShouldNotBe(HangfireConstants.DefaultQueue,
            "the agent pool and the control-plane pool must process DIFFERENT queues — same name would re-merge them and defeat the anti-starvation split");
    }
}
