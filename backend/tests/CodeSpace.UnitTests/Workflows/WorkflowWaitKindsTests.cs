using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Constants;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Rule 8 — every <see cref="WorkflowWaitKinds"/> value is a wire-format string: it's persisted in
/// <c>workflow_run_wait.wait_kind</c> AND pinned by the DB CHECK constraint (see the 00xx migrations).
/// Renaming one orphans every parked run of that kind and trips the CHECK, so pin the literals and
/// make a rename a compile-error-visible decision. <see cref="WorkflowEngine.ValidateWaitKind"/> is
/// the engine-side gate that must admit exactly the same set.
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowWaitKindsTests
{
    [Theory]
    [InlineData(WorkflowWaitKinds.Timer, "Timer")]
    [InlineData(WorkflowWaitKinds.Approval, "Approval")]
    [InlineData(WorkflowWaitKinds.Callback, "Callback")]
    [InlineData(WorkflowWaitKinds.Subworkflow, "Subworkflow")]
    [InlineData(WorkflowWaitKinds.Action, "Action")]
    public void Wait_kind_literals_are_pinned(string actual, string expected)
    {
        actual.ShouldBe(expected);
    }

    [Theory]
    [InlineData(WorkflowWaitKinds.Timer)]
    [InlineData(WorkflowWaitKinds.Approval)]
    [InlineData(WorkflowWaitKinds.Callback)]
    [InlineData(WorkflowWaitKinds.Subworkflow)]
    [InlineData(WorkflowWaitKinds.Action)]
    public void ValidateWaitKind_admits_every_known_kind(string kind)
    {
        WorkflowEngine.ValidateWaitKind("node-1", kind).ShouldBe(kind);
    }

    [Fact]
    public void ValidateWaitKind_rejects_an_unknown_kind()
    {
        var ex = Should.Throw<Exception>(() => WorkflowEngine.ValidateWaitKind("node-1", "Telepathy"));

        ex.Message.ShouldContain("node-1");
        ex.Message.ShouldContain("Telepathy");
        ex.Message.ShouldContain("Action", customMessage: "the error must list the admitted kinds so an author sees Action is now valid");
    }
}
