using CodeSpace.Core.Services.Supervisor.Observation;
using CodeSpace.Core.Services.Supervisor.Observation.Exceptions;
using CodeSpace.Messages.Dtos.Workflows.Supervisor;
using Shouldly;

namespace CodeSpace.UnitTests.Supervisor;

[Trait("Category", "Unit")]
public sealed class SupervisorPlanObservationLeafContractTests
{
    [Fact]
    public void Leaf_contract_is_hard_bounded_and_structurally_has_no_decision_body()
    {
        SupervisorPlanObservationLeafLimits.MaximumSubtasks.ShouldBe(20);
        SupervisorPlanObservationLeafLimits.MaximumIdChars.ShouldBeGreaterThan(0);
        SupervisorPlanObservationLeafLimits.MaximumTitleChars.ShouldBeGreaterThan(0);
        SupervisorPlanObservationLeafLimits.MaximumModelChars.ShouldBeGreaterThan(0);

        var names = typeof(SupervisorPlanObservationItem).GetProperties().Select(property => property.Name)
            .Concat(typeof(SupervisorPlanSubtaskObservationLeaf).GetProperties().Select(property => property.Name))
            .Concat(typeof(SupervisorPlanModelUsageObservationLeaf).GetProperties().Select(property => property.Name));

        names.ShouldNotContain(name => name.Contains("Payload", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Outcome", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Instruction", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Page_request_reuses_the_identity_bound_story_axis_and_128_500_limits()
    {
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var cursor = new SupervisorDecisionObservationStoryCursor(teamId, runId, 12, 20).Encode();

        new SupervisorPlanObservationPageRequest(teamId, runId).Limit.ShouldBe(128);
        Should.NotThrow(() => new SupervisorPlanObservationPageRequest(teamId, runId, SupervisorDecisionObservationStoryPageMode.Older, cursor, 500).ValidateShape());
        Should.Throw<SupervisorDecisionObservationReadRequestException>(() => new SupervisorPlanObservationPageRequest(teamId, runId, SupervisorDecisionObservationStoryPageMode.Tail, cursor).ValidateShape());
        Should.Throw<SupervisorDecisionObservationReadRequestException>(() => new SupervisorPlanObservationPageRequest(teamId, runId, SupervisorDecisionObservationStoryPageMode.Newer, null).ValidateShape());
        Should.Throw<SupervisorDecisionObservationReadRequestException>(() => new SupervisorPlanObservationPageRequest(teamId, runId, Limit: 501).ValidateShape());
    }

    [Fact]
    public void Sql_pages_plan_identities_before_leaf_extraction_and_never_returns_whole_jsonb_columns()
    {
        var sql = string.Join('\n',
            SupervisorPlanObservationLeafReader.TailSql,
            SupervisorPlanObservationLeafReader.OlderSql,
            SupervisorPlanObservationLeafReader.NewerSql);

        sql.ShouldContain("LIMIT @take");
        sql.ShouldContain("decision_kind = @plan_kind");
        sql.ShouldContain("story_order");
        sql.ShouldContain("observation_revision");
        sql.ShouldNotContain("sequence", Case.Insensitive);
        sql.ShouldNotContain("OFFSET", Case.Insensitive);
        sql.ShouldNotContain("COUNT(", Case.Insensitive);
        sql.ShouldNotContain("AS payload_json", Case.Insensitive);
        sql.ShouldNotContain("AS outcome_json", Case.Insensitive);
        sql.ShouldNotContain("decision.payload_jsonb,", Case.Insensitive);
        sql.ShouldNotContain("decision.outcome_jsonb,", Case.Insensitive);
    }

    [Theory]
    [InlineData(SupervisorPlanObservationLeafState.Exact, true)]
    [InlineData(SupervisorPlanObservationLeafState.Missing, false)]
    [InlineData(SupervisorPlanObservationLeafState.Invalid, false)]
    [InlineData(SupervisorPlanObservationLeafState.Truncated, false)]
    [InlineData(SupervisorPlanObservationLeafState.Corrupt, false)]
    public void Only_exact_leaf_state_is_complete(SupervisorPlanObservationLeafState state, bool expected)
    {
        state.IsComplete().ShouldBe(expected);
    }
}
