using CodeSpace.Messages.Constants;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Rule 8 — every constant in <see cref="WorkflowRunRecordTypes"/> is a wire-format string
/// used by the run-detail UI, replay tooling, and any external consumer reading the ledger
/// directly. Renaming a constant breaks those consumers; this test makes the rename a
/// compile-error-visible decision rather than a silent ledger-schema break.
///
/// Format convention: dotted-namespace, lowercase, source-family first. Catch typos and
/// accidental camelCasing.
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowRunRecordTypesTests
{
    [Theory]
    // Run-level lifecycle records.
    [InlineData("run.queued",            nameof(WorkflowRunRecordTypes.RunQueued))]
    [InlineData("run.started",           nameof(WorkflowRunRecordTypes.RunStarted))]
    [InlineData("release.loaded",        nameof(WorkflowRunRecordTypes.ReleaseLoaded))]
    [InlineData("scope.resolved",        nameof(WorkflowRunRecordTypes.ScopeResolved))]
    [InlineData("variables.snapshotted", nameof(WorkflowRunRecordTypes.VariablesSnapshotted))]
    [InlineData("run.completed",         nameof(WorkflowRunRecordTypes.RunCompleted))]
    [InlineData("run.failed",            nameof(WorkflowRunRecordTypes.RunFailed))]
    [InlineData("run.cancelled",         nameof(WorkflowRunRecordTypes.RunCancelled))]
    [InlineData("run.replayed",          nameof(WorkflowRunRecordTypes.RunReplayed))]
    [InlineData("supervisor.run_recovered", nameof(WorkflowRunRecordTypes.SupervisorRunRecovered))]
    // Node + iteration + external_call + log.
    [InlineData("node.started",          nameof(WorkflowRunRecordTypes.NodeStarted))]
    [InlineData("node.completed",        nameof(WorkflowRunRecordTypes.NodeCompleted))]
    [InlineData("node.failed",           nameof(WorkflowRunRecordTypes.NodeFailed))]
    [InlineData("node.skipped",          nameof(WorkflowRunRecordTypes.NodeSkipped))]
    [InlineData("node.suspended",        nameof(WorkflowRunRecordTypes.NodeSuspended))]
    // attempt.failed is a NON-node.* sub-event by design (stays out of the workflow_run_node `node.%` view); a
    // silent rename would break that exclusion + the run-detail chaining, so pin the literal (Rule 8).
    [InlineData("attempt.failed",        nameof(WorkflowRunRecordTypes.AttemptFailed))]
    [InlineData("iteration.started",     nameof(WorkflowRunRecordTypes.IterationStarted))]
    [InlineData("iteration.completed",   nameof(WorkflowRunRecordTypes.IterationCompleted))]
    [InlineData("external_call.started", nameof(WorkflowRunRecordTypes.ExternalCallStarted))]
    [InlineData("external_call.completed", nameof(WorkflowRunRecordTypes.ExternalCallCompleted))]
    [InlineData("external_call.failed",  nameof(WorkflowRunRecordTypes.ExternalCallFailed))]
    [InlineData("interaction.started",   nameof(WorkflowRunRecordTypes.InteractionStarted))]
    [InlineData("interaction.completed", nameof(WorkflowRunRecordTypes.InteractionCompleted))]
    [InlineData("interaction.failed",    nameof(WorkflowRunRecordTypes.InteractionFailed))]
    [InlineData("log",                   nameof(WorkflowRunRecordTypes.Log))]
    public void Wire_value_pinned(string expectedWireValue, string constantName)
    {
        var field = typeof(WorkflowRunRecordTypes).GetField(constantName);
        field.ShouldNotBeNull($"const {constantName} must exist on WorkflowRunRecordTypes");
        var actualValue = field!.GetRawConstantValue() as string;
        actualValue.ShouldBe(expectedWireValue, $"{constantName} drifted from the wire format — update consumers (frontend, replay tooling) before renaming.");
    }

    [Fact]
    public void All_constants_follow_dotted_namespace_convention()
    {
        // Every constant should be lowercase + dotted (or single-word like "log").
        // Catches accidental camelCase / kebab-case / typos.
        var constants = typeof(WorkflowRunRecordTypes).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        constants.ShouldNotBeEmpty();

        foreach (var c in constants)
        {
            // Allow underscores within a segment (e.g. external_call.started) — they keep
            // multi-word kinds readable without breaking the dotted-namespace contract.
            c.ShouldMatch("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)*$",
                $"'{c}' violates the dotted-lowercase-namespace convention");
        }
    }

    [Fact]
    public void Constants_are_unique()
    {
        var constants = typeof(WorkflowRunRecordTypes).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        constants.Distinct().Count().ShouldBe(constants.Count, "two constants share the same wire value");
    }
}
