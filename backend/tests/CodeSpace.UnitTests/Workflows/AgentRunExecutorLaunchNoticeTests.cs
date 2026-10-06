using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The one timeline event a launch's notices become (<see cref="AgentRunExecutor.DescribeLaunchNotices"/>): every
/// notice in order up to the bound, then a count of the rest, and no event at all when there is nothing to say; and
/// whether a later launch of the same run says anything again (<see cref="AgentRunExecutor.DescribeLaunchNoticeChange"/>).
/// That a revise round is announced only when its agent changed what the notices describe is pinned against the real
/// executor by <c>RealHarnessWorkspaceMemoryTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentRunExecutorLaunchNoticeTests
{
    [Fact]
    public void No_notice_means_no_event() =>
        AgentRunExecutor.DescribeLaunchNotices(Array.Empty<string>()).ShouldBeNull();

    [Fact]
    public void Every_notice_up_to_the_bound_is_said_in_order()
    {
        var notices = Notices(AgentRunExecutor.MaxLaunchNotices);

        AgentRunExecutor.DescribeLaunchNotices(notices).ShouldBe(string.Join(" ", notices));
    }

    [Fact]
    public void Past_the_bound_the_rest_are_counted_not_repeated()
    {
        var notices = Notices(AgentRunExecutor.MaxLaunchNotices + 3);

        AgentRunExecutor.DescribeLaunchNotices(notices).ShouldBe(string.Join(" ", notices.Take(AgentRunExecutor.MaxLaunchNotices)) + " (3 more)");
    }

    [Theory]
    [InlineData(null, null, null)]   // the launch leaves nothing out and nothing was said: no event
    [InlineData("A", null, "A")]     // the launch leaves something out: say it
    [InlineData("A", "A", null)]     // a revise round leaves out the same: said once per run
    [InlineData("B", "A", "B")]      // a revise round leaves out something else: say what
    [InlineData(null, "A", AgentRunExecutor.LaunchNoticesClearedNote)]   // a revise round leaves out nothing the last did: say that it loads again
    public void A_later_launch_is_announced_only_when_what_it_leaves_out_changes(string? notice, string? said, string? announced) =>
        AgentRunExecutor.DescribeLaunchNoticeChange(notice is null ? Array.Empty<string>() : new[] { notice }, said).ShouldBe(announced);

    [Fact]
    public void The_bound_is_pinned() =>
        AgentRunExecutor.MaxLaunchNotices.ShouldBe(10);

    private static IReadOnlyList<string> Notices(int count) =>
        Enumerable.Range(1, count).Select(i => $"Left the memory in 'repo-{i}' out of this run: CLAUDE.md resolves outside the workspace.").ToList();
}
