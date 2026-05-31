using CodeSpace.Messages.Constants;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Rule 8 — <see cref="WorkflowHandles.Error"/> is a wire-format string: it's persisted in
/// saved <c>EdgeDefinition.SourceHandle</c> values and mirrored by the editor. Renaming it
/// orphans every existing error-routing edge, so pin the literal and make a rename a
/// compile-error-visible decision.
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowHandlesTests
{
    [Fact]
    public void Error_handle_literal_is_pinned()
    {
        WorkflowHandles.Error.ShouldBe("error");
    }

    [Fact]
    public void Catch_handle_literal_is_pinned()
    {
        // Persisted in flow.try catch edges' SourceHandle; a rename orphans every saved try/catch.
        WorkflowHandles.Catch.ShouldBe("catch");
    }
}
