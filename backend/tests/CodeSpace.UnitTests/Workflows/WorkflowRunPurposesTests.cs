using CodeSpace.Messages.Constants;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Rule 8 — pin every <see cref="WorkflowRunPurposes"/> constant by wire value. The string lands in
/// <c>workflow_run.purpose</c> (an open-string column) and is read by <c>WorkflowService.CollapseToLatestPerLineage</c>
/// to exclude a run from the team Runs index; renaming it silently un-hides every already-stamped row.
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowRunPurposesTests
{
    [Theory]
    [InlineData("qualification", nameof(WorkflowRunPurposes.Qualification))]
    public void Wire_value_pinned(string expectedWireValue, string constantName)
    {
        var field = typeof(WorkflowRunPurposes).GetField(constantName);
        field.ShouldNotBeNull($"const {constantName} must exist on WorkflowRunPurposes");
        var actual = field!.GetRawConstantValue() as string;
        actual.ShouldBe(expectedWireValue, $"{constantName} drifted from the wire format — update consumers (the team Runs index exclusion, analytics) before renaming.");
    }

    [Fact]
    public void Constants_are_unique()
    {
        var values = typeof(WorkflowRunPurposes).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        values.Distinct().Count().ShouldBe(values.Count, "two constants share the same wire value");
    }
}
