using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions;

/// <summary>
/// Unit-proves the PURE manifest-branch chooser (<see cref="SessionManifestBranches"/>) — the I2 choke point
/// <see cref="SessionProjection"/> and <see cref="SessionBranchResolver"/> both defer to, so a turn's display and its
/// continuity read the SAME notion of "what did this run produce."
/// </summary>
[Trait("Category", "Unit")]
public class SessionManifestBranchesTests
{
    private static PublishManifest Row(string alias, string? branch, PublishState state = PublishState.Pushed, PublishManifestKind kind = PublishManifestKind.Agent, Guid? repositoryId = null, DateTimeOffset createdDate = default) => new()
    {
        RepositoryAlias = alias, Branch = branch, PublishStateValue = state, Kind = kind, RepositoryId = repositoryId ?? Guid.NewGuid(), CreatedDate = createdDate,
    };

    [Fact]
    public void No_manifests_resolves_nothing()
    {
        SessionManifestBranches.ResolveSingleRepoBranch(null).ShouldBeNull();
        SessionManifestBranches.ResolveSingleRepoBranch(Array.Empty<PublishManifest>()).ShouldBeNull();
        SessionManifestBranches.ResolveRepositoryBranches(null).ShouldBeEmpty();
    }

    [Fact]
    public void One_pushed_row_resolves_as_the_single_repo_branch()
    {
        var rows = new[] { Row("primary", "codespace/agent/x") };

        SessionManifestBranches.ResolveSingleRepoBranch(rows).ShouldBe("codespace/agent/x");
        SessionManifestBranches.ResolveRepositoryBranches(rows).ShouldBeEmpty("exactly one live branch is the FLAT shape, never the per-repo array");
    }

    [Fact]
    public void Multiple_pushed_rows_resolve_as_repository_branches_never_the_flat_single()
    {
        var repoA = Guid.NewGuid(); var repoB = Guid.NewGuid();
        var rows = new[] { Row("web", "codespace/agent/web", repositoryId: repoA), Row("api", "codespace/agent/api", repositoryId: repoB) };

        SessionManifestBranches.ResolveSingleRepoBranch(rows).ShouldBeNull();
        var multi = SessionManifestBranches.ResolveRepositoryBranches(rows);
        multi.Count.ShouldBe(2);
        multi.ShouldContain(b => b.RepositoryId == repoA && b.Branch == "codespace/agent/web");
        multi.ShouldContain(b => b.RepositoryId == repoB && b.Branch == "codespace/agent/api");
    }

    [Theory]
    [InlineData(PublishState.None)]
    [InlineData(PublishState.PatchOnly)]
    public void A_row_with_no_live_pushed_branch_never_counts(PublishState notPushed)
    {
        var rows = new[] { Row("primary", "codespace/agent/x", state: notPushed) };

        SessionManifestBranches.ResolveSingleRepoBranch(rows).ShouldBeNull();
        SessionManifestBranches.ResolveRepositoryBranches(rows).ShouldBeEmpty();
    }

    [Fact]
    public void A_row_with_a_pushed_state_but_no_branch_string_never_counts()
    {
        // Defensive — PublishStateValue and Branch should always agree, but the chooser must not trust that blindly.
        var rows = new[] { Row("primary", null, state: PublishState.Pushed) };

        SessionManifestBranches.ResolveSingleRepoBranch(rows).ShouldBeNull();
    }

    [Fact]
    public void An_Integration_row_wins_over_the_supervisors_own_per_subtask_Agent_rows()
    {
        // A supervisor turn's Agent-kind rows are its internal fan-out (per spawned subtask) — once an Integration
        // row exists (the fold), it is the turn's OWN outcome, not the raw ingredients that went into it.
        var rows = new[]
        {
            Row("primary", "codespace/agent/subtask-a", kind: PublishManifestKind.Agent),
            Row("primary", "codespace/agent/subtask-b", kind: PublishManifestKind.Agent),
            Row("primary", "codespace/integration/run/turn1", kind: PublishManifestKind.Integration),
        };

        SessionManifestBranches.ResolveSingleRepoBranch(rows).ShouldBe("codespace/integration/run/turn1");
    }

    [Fact]
    public void With_no_Integration_row_the_single_Agent_row_is_the_outcome()
    {
        // A plain single-agent turn (no supervisor, nothing to fold) — its one Agent row IS the turn's outcome.
        var rows = new[] { Row("primary", "codespace/agent/solo", kind: PublishManifestKind.Agent) };

        SessionManifestBranches.ResolveSingleRepoBranch(rows).ShouldBe("codespace/agent/solo");
    }

    [Fact]
    public void A_repo_branch_entry_with_no_resolvable_repository_id_is_skipped()
    {
        var rows = new[]
        {
            new PublishManifest { RepositoryAlias = "web", Branch = "codespace/agent/web", PublishStateValue = PublishState.Pushed, Kind = PublishManifestKind.Agent, RepositoryId = null },
            Row("api", "codespace/agent/api"),
        };

        var multi = SessionManifestBranches.ResolveRepositoryBranches(rows);
        multi.Count.ShouldBe(1, "the null-repository-id entry can't be attributed to a specific repo");
        multi[0].Branch.ShouldBe("codespace/agent/api");
    }

    [Fact]
    public void A_retried_subtask_leaving_two_Agent_rows_for_the_same_single_repo_collapses_to_the_newest()
    {
        var repoId = Guid.NewGuid();
        var rows = new[]
        {
            Row("primary", "codespace/agent/attempt-1", repositoryId: repoId, createdDate: DateTimeOffset.UtcNow.AddMinutes(-5)),
            Row("primary", "codespace/agent/attempt-2-retry", repositoryId: repoId, createdDate: DateTimeOffset.UtcNow),
        };

        SessionManifestBranches.ResolveSingleRepoBranch(rows).ShouldBe("codespace/agent/attempt-2-retry",
            "two rows for the SAME repo (a retry) must collapse to one — the newest — never leave the single-repo case unresolved as if it were ambiguous");
    }

    [Fact]
    public void A_retried_subtask_leaving_two_Agent_rows_for_one_repo_in_a_multi_repo_turn_never_double_counts_that_repo()
    {
        var repoA = Guid.NewGuid(); var repoB = Guid.NewGuid();
        var rows = new[]
        {
            Row("web", "codespace/agent/web-attempt-1", repositoryId: repoA, createdDate: DateTimeOffset.UtcNow.AddMinutes(-5)),
            Row("web", "codespace/agent/web-attempt-2-retry", repositoryId: repoA, createdDate: DateTimeOffset.UtcNow),
            Row("api", "codespace/agent/api", repositoryId: repoB, createdDate: DateTimeOffset.UtcNow),
        };

        var multi = SessionManifestBranches.ResolveRepositoryBranches(rows);
        multi.Count.ShouldBe(2, "repoA's two retried rows must collapse to one entry, never both");
        multi.ShouldContain(b => b.RepositoryId == repoA && b.Branch == "codespace/agent/web-attempt-2-retry");
        multi.ShouldContain(b => b.RepositoryId == repoB && b.Branch == "codespace/agent/api");
    }
}
