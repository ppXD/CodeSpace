using CodeSpace.Messages.Constants;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Rule 8 — pin every <see cref="WorkflowRunSourceTypes"/> constant by wire value. These
/// strings land in <c>workflow_run_request.source_type</c> (an open-string column), surface
/// to operators via <c>sys.source_type</c>, and feed every analytics / replay tool that
/// filters by source. Renaming any of them silently breaks every existing row + every
/// dashboard. Hard-pin the literals here so a rename is a compile-error-visible decision.
///
/// <para>The dispatcher convention — provider-event sources use the matcher's
/// <c>TypeKey</c> verbatim (e.g. <c>trigger.pr.opened</c>), NOT the
/// <c>provider.&lt;vendor&gt;.&lt;event&gt;</c> form. The doc on
/// <see cref="WorkflowRunSourceTypes.ProviderPrefix"/> documents this as the canonical
/// shape. Schedule / Api / ChildWorkflow producers write their constants directly.</para>
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowRunSourceTypesTests
{
    [Theory]
    [InlineData("manual",         nameof(WorkflowRunSourceTypes.Manual))]
    [InlineData("replay",         nameof(WorkflowRunSourceTypes.Replay))]
    [InlineData("schedule.cron",  nameof(WorkflowRunSourceTypes.ScheduleCron))]
    [InlineData("api",            nameof(WorkflowRunSourceTypes.Api))]
    [InlineData("workflow.child", nameof(WorkflowRunSourceTypes.ChildWorkflow))]
    [InlineData("provider.",      nameof(WorkflowRunSourceTypes.ProviderPrefix))]
    public void Wire_value_pinned(string expectedWireValue, string constantName)
    {
        var field = typeof(WorkflowRunSourceTypes).GetField(constantName);
        field.ShouldNotBeNull($"const {constantName} must exist on WorkflowRunSourceTypes");
        var actual = field!.GetRawConstantValue() as string;
        actual.ShouldBe(expectedWireValue, $"{constantName} drifted from the wire format — update consumers (frontend run list, replay tooling, analytics) before renaming.");
    }

    [Fact]
    public void All_values_follow_dotted_lowercase_convention()
    {
        // Same convention as WorkflowRunRecordTypes — lowercase letters / digits / underscores,
        // optionally dotted-namespaced. The ProviderPrefix value is a partial form (ends with .)
        // intentionally — call sites concatenate matcher-specific suffix.
        var values = typeof(WorkflowRunSourceTypes).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        values.ShouldNotBeEmpty();

        foreach (var v in values)
        {
            // Allow trailing dot for ProviderPrefix; otherwise standard dotted-lowercase.
            var trimmed = v.TrimEnd('.');
            trimmed.ShouldMatch("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)*$",
                $"'{v}' violates the dotted-lowercase-namespace convention");
        }
    }

    [Fact]
    public void Constants_are_unique()
    {
        var values = typeof(WorkflowRunSourceTypes).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        values.Distinct().Count().ShouldBe(values.Count, "two constants share the same wire value");
    }
}
