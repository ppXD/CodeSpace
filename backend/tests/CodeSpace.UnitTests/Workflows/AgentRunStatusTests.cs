using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class AgentRunStatusTests
{
    [Fact]
    public void Defines_the_full_agent_run_lifecycle()
    {
        // Pinned: the durable AgentRun lifecycle vocabulary. Removing a value is a breaking change
        // (orphans persisted rows whose status string no longer maps). Adding one is fine.
        Enum.GetNames<AgentRunStatus>().ShouldBe(
            new[] { "Queued", "Running", "Succeeded", "Failed", "Cancelled", "TimedOut" },
            ignoreOrder: true);
    }
}
