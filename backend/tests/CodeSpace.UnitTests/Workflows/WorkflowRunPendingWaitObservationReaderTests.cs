using CodeSpace.Core.Services.Workflows.Display;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public sealed class WorkflowRunPendingWaitObservationReaderTests
{
    [Fact]
    public void Query_is_team_scoped_single_row_and_projects_only_a_bounded_prompt_prefix()
    {
        WorkflowRunPendingWaitObservationReader.MaximumPromptCharacters.ShouldBe(2048);
        WorkflowRunPendingWaitObservationReader.Sql.ShouldContain("run.team_id = @team_id");
        WorkflowRunPendingWaitObservationReader.Sql.ShouldContain("LIMIT 1");
        WorkflowRunPendingWaitObservationReader.Sql.ShouldContain("left(selected.payload_jsonb ->> 'prompt', @max_prompt_chars)");
        WorkflowRunPendingWaitObservationReader.Sql.ShouldContain("char_length(selected.payload_jsonb ->> 'prompt')");
        WorkflowRunPendingWaitObservationReader.Sql.ShouldNotContain("SELECT wait.payload_jsonb", Case.Insensitive);
    }
}
