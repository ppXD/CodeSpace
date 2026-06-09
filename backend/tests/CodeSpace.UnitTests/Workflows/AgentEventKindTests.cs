using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class AgentEventKindTests
{
    [Fact]
    public void Defines_the_normalized_event_vocabulary()
    {
        // Pinned: the closed vocabulary every harness normalizes its native stream into, and the value
        // persisted in agent_run_event.kind. Removing a value orphans persisted rows whose string no
        // longer maps; adding one is fine (additive). Harnesses map anything unrecognized to Warning,
        // never drop it — so the set only ever grows.
        Enum.GetNames<AgentEventKind>().ShouldBe(new[]
        {
            "Queued", "Started", "AssistantMessage", "Reasoning", "PlanUpdate", "ToolCall",
            "CommandExecuted", "FileChanged", "TestOutput", "ApprovalRequested", "ApprovalResolved",
            "Warning", "Error", "FinalSummary", "Completed",
        }, ignoreOrder: true);
    }
}
