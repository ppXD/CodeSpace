using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The ancestry walk the dispatcher refuses a run by, over an in-memory chain of runs. It climbs only while each hop is a
/// sub-workflow child, so a rerun's or replay's parent — its lineage, finished by definition — never counts; it refuses on
/// the first finished or missing ancestor; and it ends on a cycle or a chain longer than nesting allows instead of looping.
/// </summary>
[Trait("Category", "Unit")]
public class RunAncestryTests
{
    [Fact]
    public async Task A_run_that_is_not_a_child_has_no_ancestor_to_refuse_it()
    {
        var chain = new Chain();
        var original = chain.Add(WorkflowRunSourceTypes.Manual, null, WorkflowRunStatus.Failure);
        var rerun = chain.Add(WorkflowRunSourceTypes.Rerun, original, WorkflowRunStatus.Pending);

        (await chain.HasFinishedAncestorAsync(rerun)).ShouldBeFalse("a rerun's parent is its lineage, not a run waiting for it");
        chain.Loaded.ShouldBe(new List<Guid> { rerun }, "the walk reads the run itself and stops");
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Pending)]
    [InlineData(WorkflowRunStatus.Enqueued)]
    [InlineData(WorkflowRunStatus.Running)]
    [InlineData(WorkflowRunStatus.Suspended)]
    public async Task A_child_under_a_live_parent_is_not_refused(WorkflowRunStatus live)
    {
        var chain = new Chain();
        var parent = chain.Add(WorkflowRunSourceTypes.Manual, null, live);
        var child = chain.Add(WorkflowRunSourceTypes.ChildWorkflow, parent, WorkflowRunStatus.Pending);

        (await chain.HasFinishedAncestorAsync(child)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Success)]
    [InlineData(WorkflowRunStatus.Failure)]
    [InlineData(WorkflowRunStatus.Cancelled)]
    public async Task A_child_under_a_finished_parent_is_refused(WorkflowRunStatus finished)
    {
        var chain = new Chain();
        var parent = chain.Add(WorkflowRunSourceTypes.Manual, null, finished);
        var child = chain.Add(WorkflowRunSourceTypes.ChildWorkflow, parent, WorkflowRunStatus.Pending);

        (await chain.HasFinishedAncestorAsync(child)).ShouldBeTrue($"nothing waits for a child of a {finished} parent");
    }

    [Fact]
    public async Task A_grandchild_is_refused_when_its_root_has_finished_though_its_parent_lives()
    {
        var chain = new Chain();
        var root = chain.Add(WorkflowRunSourceTypes.Manual, null, WorkflowRunStatus.Failure);
        var child = chain.Add(WorkflowRunSourceTypes.ChildWorkflow, root, WorkflowRunStatus.Suspended);
        var grandchild = chain.Add(WorkflowRunSourceTypes.ChildWorkflow, child, WorkflowRunStatus.Pending);

        (await chain.HasFinishedAncestorAsync(grandchild)).ShouldBeTrue("the root's finish ends every level below it, not only the level right under it");
    }

    [Fact]
    public async Task The_walk_stops_at_a_rerun_hop_and_never_reads_its_lineage()
    {
        var chain = new Chain();
        var original = chain.Add(WorkflowRunSourceTypes.Manual, null, WorkflowRunStatus.Failure);
        var fork = chain.Add(WorkflowRunSourceTypes.Rerun, original, WorkflowRunStatus.Suspended);
        var child = chain.Add(WorkflowRunSourceTypes.ChildWorkflow, fork, WorkflowRunStatus.Pending);

        (await chain.HasFinishedAncestorAsync(child)).ShouldBeFalse("the live fork is waiting for its child; the fork's own parent is lineage");
        chain.Loaded.ShouldNotContain(original, "the walk ends at the first hop that is not a sub-workflow child");
    }

    [Fact]
    public async Task A_child_whose_parent_row_is_missing_is_refused()
    {
        var chain = new Chain();
        var child = chain.Add(WorkflowRunSourceTypes.ChildWorkflow, Guid.NewGuid(), WorkflowRunStatus.Pending);

        (await chain.HasFinishedAncestorAsync(child)).ShouldBeTrue("a parent that is gone holds no wait for the child");
    }

    [Fact]
    public async Task A_cycle_of_children_is_refused_and_the_walk_ends()
    {
        var chain = new Chain();
        var a = Guid.NewGuid();
        var b = chain.Add(WorkflowRunSourceTypes.ChildWorkflow, a, WorkflowRunStatus.Running);
        chain.Add(WorkflowRunSourceTypes.ChildWorkflow, b, WorkflowRunStatus.Running, a);

        (await chain.HasFinishedAncestorAsync(a)).ShouldBeTrue("a chain that loops back is not one the engine staged");
        chain.Loaded.Count.ShouldBeLessThanOrEqualTo(3, "the walk notices the repeat instead of going round");
    }

    [Fact]
    public async Task A_chain_as_deep_as_nesting_allows_is_not_refused()
    {
        var (chain, leaf) = LiveChain(RunAncestry.MaxChildHops);

        (await chain.HasFinishedAncestorAsync(leaf)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_chain_deeper_than_nesting_allows_is_refused()
    {
        var (chain, leaf) = LiveChain(RunAncestry.MaxChildHops + 1);

        (await chain.HasFinishedAncestorAsync(leaf)).ShouldBeTrue("the walk is bounded — a longer chain is not one the engine staged");
        chain.Loaded.Count.ShouldBe(RunAncestry.MaxChildHops + 1, "the walk stops at its bound");
    }

    [Fact]
    public async Task An_unknown_run_is_not_refused_here()
    {
        // The dispatcher's own Pending CAS finds no row and declines; the walk has nothing to say about it.
        (await new Chain().HasFinishedAncestorAsync(Guid.NewGuid())).ShouldBeFalse();
    }

    /// <summary>A live root with <paramref name="childHops"/> sub-workflow children stacked under it; returns the deepest.</summary>
    private static (Chain Chain, Guid Leaf) LiveChain(int childHops)
    {
        var chain = new Chain();
        var current = chain.Add(WorkflowRunSourceTypes.Manual, null, WorkflowRunStatus.Suspended);

        for (var i = 0; i < childHops; i++) current = chain.Add(WorkflowRunSourceTypes.ChildWorkflow, current, WorkflowRunStatus.Suspended);

        return (chain, current);
    }

    private sealed class Chain
    {
        private readonly Dictionary<Guid, RunAncestry.Hop> _runs = new();

        public List<Guid> Loaded { get; } = new();

        public Guid Add(string sourceType, Guid? parentRunId, WorkflowRunStatus status, Guid? id = null)
        {
            var runId = id ?? Guid.NewGuid();
            _runs[runId] = new RunAncestry.Hop(sourceType, parentRunId, status);
            return runId;
        }

        public Task<bool> HasFinishedAncestorAsync(Guid runId) => RunAncestry.HasFinishedAncestorAsync(runId, Load);

        private Task<RunAncestry.Hop?> Load(Guid runId)
        {
            Loaded.Add(runId);
            return Task.FromResult(_runs.GetValueOrDefault(runId));
        }
    }
}
