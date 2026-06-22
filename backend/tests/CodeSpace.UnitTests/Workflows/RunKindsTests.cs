using CodeSpace.Messages.Constants;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the <see cref="RunKinds"/> token VALUES. run_kind is a Postgres GENERATED column whose CASE (migration 0067)
/// emits these exact literals; the filter compares against these constants. If a constant value drifts from the SQL
/// CASE literal, the run-kind filter silently stops matching — this test hard-pins the wire values so the rename is a
/// compile/test-visible decision, not an invisible break (the same discipline as Rule 8's env-var pinning).
/// </summary>
public class RunKindsTests
{
    [Fact]
    public void Token_values_match_the_generated_column_CASE_literals()
    {
        RunKinds.Workflow.ShouldBe("workflow");
        RunKinds.Task.ShouldBe("task");
        RunKinds.Event.ShouldBe("event");
        RunKinds.Replay.ShouldBe("replay");
        RunKinds.Schedule.ShouldBe("schedule");
        RunKinds.Child.ShouldBe("child");
        RunKinds.Api.ShouldBe("api");
        RunKinds.Other.ShouldBe("other");
    }
}
