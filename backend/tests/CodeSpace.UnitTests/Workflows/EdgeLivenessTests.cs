using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <see cref="EdgeLiveness.IsLive"/> is the crux of branch routing AND error routing — a subtle
/// miss silently mis-routes runs. This pins the full truth table. The key regression it locks: a
/// SUCCESSFUL non-branch node (null routing hints) must NOT light its <c>error</c> edge — earlier
/// the "no hints ⇒ every edge live" shortcut wrongly fired the error branch on success.
/// </summary>
[Trait("Category", "Unit")]
public class EdgeLivenessTests
{
    [Theory]
    // ── Success: normal/branch handles fire per routing hints; the error handle NEVER fires. ──
    [InlineData(NodeStatus.Success, "out", null, true)]      // single default output, no hints
    [InlineData(NodeStatus.Success, null, null, true)]       // null handle == the default output
    [InlineData(NodeStatus.Success, "error", null, false)]   // ← the bug: error must stay dead on success
    [InlineData(NodeStatus.Success, "error", "true", false)] // error dead even alongside a branch decision
    [InlineData(NodeStatus.Success, "true", "true", true)]    // branch: the chosen handle is live
    [InlineData(NodeStatus.Success, "false", "true", false)] // branch: the unchosen handle is dead
    [InlineData(NodeStatus.Success, "out", "", false)]        // empty hints = nothing chosen ⇒ dead
    // ── Failure: ONLY the error handle fires. ──
    [InlineData(NodeStatus.Failure, "error", null, true)]
    [InlineData(NodeStatus.Failure, "out", null, false)]
    [InlineData(NodeStatus.Failure, null, null, false)]
    [InlineData(NodeStatus.Failure, "true", null, false)]
    // ── Skipped / not-yet-terminal: everything dead (skip propagates). ──
    [InlineData(NodeStatus.Skipped, "error", null, false)]
    [InlineData(NodeStatus.Skipped, "out", null, false)]
    [InlineData(NodeStatus.Pending, "error", null, false)]
    [InlineData(NodeStatus.Running, "out", null, false)]
    [InlineData(NodeStatus.Suspended, "error", null, false)]
    public void IsLive_truth_table(NodeStatus status, string? handle, string? hintsCsv, bool expected)
    {
        IReadOnlySet<string>? hints = hintsCsv == null
            ? null
            : new HashSet<string>(hintsCsv.Length == 0 ? Array.Empty<string>() : hintsCsv.Split(','));

        EdgeLiveness.IsLive(status, handle, hints).ShouldBe(expected);
    }

    [Fact]
    public void Error_handle_is_dead_on_success_even_for_a_non_branch_node()
    {
        // The exact regression: a successful node with no routing hints must not light its error edge.
        EdgeLiveness.IsLive(NodeStatus.Success, WorkflowHandles.Error, null).ShouldBeFalse();
    }
}
