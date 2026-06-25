using CodeSpace.Messages.Queries.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Workflows;

/// <summary>
/// The summary query folds ONLY the bar's scope dimensions into the run filter — it carries no status / pagination
/// fields, because the per-status counts are computed by the service over that scoped base (so a status on the wire
/// can't skew them).
/// </summary>
[Trait("Category", "Unit")]
public class GetTeamRunSummaryQueryTests
{
    [Fact]
    public void ToFilter_folds_the_scope_dimensions_and_carries_no_status()
    {
        var repoId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        var filter = new GetTeamRunSummaryQuery
        {
            RepositoryIds = new[] { repoId },
            ActorIds = new[] { actorId },
            RunKinds = new[] { "task" },
            Today = DateTimeOffset.UtcNow,
        }.ToFilter();

        filter.RepositoryIds.ShouldBe(new[] { repoId });
        filter.ActorIds.ShouldBe(new[] { actorId });
        filter.RunKinds.ShouldBe(new[] { "task" });
        filter.Statuses.ShouldBeNull("the summary computes per-status counts itself, so it must not pre-narrow by status");
    }
}
