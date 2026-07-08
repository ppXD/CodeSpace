using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions;

/// <summary>
/// Unit-proves the PURE conversation projection (<see cref="SessionProjection"/>) — the grouping / latest-wins /
/// attempt-ladder logic the read service delegates to. Covers: a plain turn, a reran turn (latest-wins headline + the
/// ordered ladder), the result fallback chain, single-repo vs multi-repo branches, orphaned attempts skipped, turn
/// ordering, the per-turn pending-decision marker, and the list's latest-run-per-session pick.
/// </summary>
[Trait("Category", "Unit")]
public class SessionProjectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 28, 10, 0, 0, TimeSpan.Zero);

    private static SessionProjection.RunRow Run(
        Guid id, int? turn, WorkflowRunStatus status = WorkflowRunStatus.Success, Guid? root = null,
        string source = "manual", string outputs = "{}", string goal = "{}", string? projection = "single-agent",
        string? rerunFrom = null, DateTimeOffset? created = null, IReadOnlyList<Guid>? scope = null) =>
        new(id, root, turn, status, projection, source, rerunFrom, created ?? T0, null, null, null, outputs, goal, scope ?? Array.Empty<Guid>());

    private static readonly HashSet<Guid> NonePending = new();

    [Fact]
    public void A_plain_turn_projects_the_run_outcome_with_no_attempt_ladder()
    {
        var id = Guid.NewGuid();
        var runs = new[] { Run(id, turn: 1, outputs: """{"summary":"did it","branch":"cs/x"}""", goal: """{"goal":"do X"}""") };

        var turns = SessionProjection.BuildTurns(runs, NonePending);

        var t = turns.ShouldHaveSingleItem();
        t.TurnIndex.ShouldBe(1);
        t.TurnRunId.ShouldBe(id);
        t.RunId.ShouldBe(id, "no rerun → the turn run is also the displayed run");
        t.UserMessage.ShouldBe("do X", "the user message is the run's goal");
        t.RunStatus.ShouldBe(WorkflowRunStatus.Success);
        t.ProjectionKind.ShouldBe("single-agent");
        t.Result.ShouldBe("did it");
        t.ProducedBranch.ShouldBe("cs/x");
        t.RepositoryResults.ShouldBeNull();
        t.AttemptCount.ShouldBe(1);
        t.Attempts.ShouldBeNull("a never-reran turn carries no ladder");
        t.HasPendingDecision.ShouldBeFalse();
    }

    [Fact]
    public void A_reran_turn_shows_the_latest_attempt_outcome_with_the_full_ordered_ladder()
    {
        var b0 = Guid.NewGuid(); var b1 = Guid.NewGuid(); var b2 = Guid.NewGuid();
        var runs = new[]
        {
            Run(b0, turn: 2, status: WorkflowRunStatus.Failure, source: "manual", created: T0, goal: """{"goal":"fix the bug"}""", outputs: """{"summary":"failed"}"""),
            Run(b1, turn: null, root: b0, status: WorkflowRunStatus.Failure, source: "replay", created: T0.AddMinutes(1)),
            Run(b2, turn: null, root: b0, status: WorkflowRunStatus.Success, source: "rerun", rerunFrom: "nodeX", created: T0.AddMinutes(2), outputs: """{"summary":"fixed","branch":"cs/y"}"""),
        };

        var t = SessionProjection.BuildTurns(runs, NonePending).ShouldHaveSingleItem();

        t.TurnIndex.ShouldBe(2);
        t.TurnRunId.ShouldBe(b0, "the turn's stable identity is the original run");
        t.RunId.ShouldBe(b2, "the displayed run is the newest attempt");
        t.RunStatus.ShouldBe(WorkflowRunStatus.Success, "latest-wins: the newest attempt's outcome is the turn's");
        t.Result.ShouldBe("fixed");
        t.ProducedBranch.ShouldBe("cs/y");
        t.UserMessage.ShouldBe("fix the bug", "the message comes from the original turn run, not a rerun fork");
        t.AttemptCount.ShouldBe(3);

        t.Attempts.ShouldNotBeNull();
        t.Attempts!.Select(a => a.RunId).ShouldBe(new[] { b0, b1, b2 }, "oldest → newest");
        t.Attempts.Select(a => a.AttemptNumber).ShouldBe(new[] { 1, 2, 3 });
        t.Attempts[0].IsLatest.ShouldBeFalse();
        t.Attempts[^1].IsLatest.ShouldBeTrue();
        t.Attempts[^1].RerunFromNodeId.ShouldBe("nodeX");
        t.Attempts[^1].SourceType.ShouldBe("rerun");
    }

    [Theory]
    [InlineData("""{"combined":"map synthesis"}""", "map synthesis")]
    [InlineData("""{"reason":"supervisor reason"}""", "supervisor reason")]
    [InlineData("""{"summary":"s","combined":"c"}""", "s")]
    public void The_result_reads_generically_across_projection_shapes(string outputs, string expected)
    {
        var t = SessionProjection.BuildTurns(new[] { Run(Guid.NewGuid(), turn: 1, outputs: outputs) }, NonePending).ShouldHaveSingleItem();
        t.Result.ShouldBe(expected);
    }

    [Fact]
    public void A_multi_repo_turn_surfaces_per_repo_branches_and_no_flat_branch()
    {
        var repoA = Guid.NewGuid(); var repoB = Guid.NewGuid();
        var outputs = $$"""{"repositoryResults":[{"repositoryId":"{{repoA}}","producedBranch":"cs/a"},{"repositoryId":"{{repoB}}","producedBranch":"cs/b"}]}""";

        var t = SessionProjection.BuildTurns(new[] { Run(Guid.NewGuid(), turn: 1, outputs: outputs) }, NonePending).ShouldHaveSingleItem();

        t.ProducedBranch.ShouldBeNull("a multi-repo turn has no single flat branch");
        t.RepositoryResults.ShouldNotBeNull();
        t.RepositoryResults!.Count.ShouldBe(2);
        t.RepositoryResults.ShouldContain(r => r.RepositoryId == repoA && r.ProducedBranch == "cs/a");
        t.RepositoryResults.ShouldContain(r => r.RepositoryId == repoB && r.ProducedBranch == "cs/b");
    }

    [Fact]
    public void A_lineage_with_no_top_level_turn_is_skipped()
    {
        // An orphaned attempt whose root run isn't in the loaded set (e.g. its root is a child) — not a turn.
        var runs = new[] { Run(Guid.NewGuid(), turn: null, root: Guid.NewGuid(), source: "replay") };

        SessionProjection.BuildTurns(runs, NonePending).ShouldBeEmpty();
    }

    [Fact]
    public void Turns_are_ordered_by_turn_index_regardless_of_input_order()
    {
        var runs = new[]
        {
            Run(Guid.NewGuid(), turn: 3),
            Run(Guid.NewGuid(), turn: 1),
            Run(Guid.NewGuid(), turn: 2),
        };

        SessionProjection.BuildTurns(runs, NonePending).Select(t => t.TurnIndex).ShouldBe(new[] { 1, 2, 3 });
    }

    [Fact]
    public void The_pending_marker_reflects_the_turns_latest_attempt()
    {
        var b0 = Guid.NewGuid(); var b1 = Guid.NewGuid();
        var runs = new[]
        {
            Run(b0, turn: 1, created: T0),
            Run(b1, turn: null, root: b0, created: T0.AddMinutes(1)),
        };

        // Only the latest attempt (b1) is parked on a decision → the turn is "needs you"; the old attempt b0 is not consulted.
        var t = SessionProjection.BuildTurns(runs, new HashSet<Guid> { b1 }).ShouldHaveSingleItem();
        t.HasPendingDecision.ShouldBeTrue();

        var t2 = SessionProjection.BuildTurns(runs, new HashSet<Guid> { b0 }).ShouldHaveSingleItem();
        t2.HasPendingDecision.ShouldBeFalse("a pending decision on a superseded attempt is not the turn's live state");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"repositoryResults":[]}""")]
    [InlineData("""{"repositoryResults":[{"producedBranch":"no-id"}]}""")]
    public void ReadRepositoryResults_is_null_for_absent_malformed_or_empty(string outputs)
    {
        SessionProjection.ReadRepositoryResults(outputs).ShouldBeNull();
    }

    [Fact]
    public void The_manifest_branch_wins_over_a_conflicting_raw_OutputsJson_branch()
    {
        // I2: nobody ever guesses a branch name off raw OutputsJson once the manifest has an authoritative answer.
        var id = Guid.NewGuid();
        var runs = new[] { Run(id, turn: 1, outputs: """{"summary":"did it","branch":"stale-guessed-branch"}""") };
        var manifests = new Dictionary<Guid, IReadOnlyList<PublishManifest>>
        {
            [id] = new[] { new PublishManifest { RepositoryAlias = "primary", Branch = "codespace/agent/real", PublishStateValue = PublishState.Pushed, Kind = PublishManifestKind.Agent } },
        };

        var t = SessionProjection.BuildTurns(runs, NonePending, manifests).ShouldHaveSingleItem();

        t.ProducedBranch.ShouldBe("codespace/agent/real");
    }

    [Fact]
    public void With_no_manifest_row_the_legacy_raw_OutputsJson_branch_is_still_used()
    {
        // Older data, or a supervisor fold nobody has opened a PR for yet (RoomPullRequestService is still the only
        // writer of an Integration row today) — never a silent blank where the raw read would have found something.
        var id = Guid.NewGuid();
        var runs = new[] { Run(id, turn: 1, outputs: """{"branch":"cs/legacy"}""") };

        var t = SessionProjection.BuildTurns(runs, NonePending, new Dictionary<Guid, IReadOnlyList<PublishManifest>>()).ShouldHaveSingleItem();

        t.ProducedBranch.ShouldBe("cs/legacy");
    }

    [Fact]
    public void The_manifests_multi_repo_rows_win_over_a_conflicting_raw_repositoryResults_array()
    {
        var id = Guid.NewGuid();
        var repoA = Guid.NewGuid(); var repoB = Guid.NewGuid();
        var staleOutputs = $$"""{"repositoryResults":[{"repositoryId":"{{repoA}}","producedBranch":"stale-a"},{"repositoryId":"{{repoB}}","producedBranch":"stale-b"}]}""";
        var runs = new[] { Run(id, turn: 1, outputs: staleOutputs) };
        var manifests = new Dictionary<Guid, IReadOnlyList<PublishManifest>>
        {
            [id] = new[]
            {
                new PublishManifest { RepositoryAlias = "web", RepositoryId = repoA, Branch = "codespace/agent/real-a", PublishStateValue = PublishState.Pushed, Kind = PublishManifestKind.Agent },
                new PublishManifest { RepositoryAlias = "api", RepositoryId = repoB, Branch = "codespace/agent/real-b", PublishStateValue = PublishState.Pushed, Kind = PublishManifestKind.Agent },
            },
        };

        var t = SessionProjection.BuildTurns(runs, NonePending, manifests).ShouldHaveSingleItem();

        t.ProducedBranch.ShouldBeNull();
        t.RepositoryResults.ShouldNotBeNull();
        t.RepositoryResults!.ShouldContain(r => r.RepositoryId == repoA && r.ProducedBranch == "codespace/agent/real-a");
        t.RepositoryResults.ShouldContain(r => r.RepositoryId == repoB && r.ProducedBranch == "codespace/agent/real-b");
    }

    [Fact]
    public void A_manifest_row_that_never_pushed_is_ignored_falling_back_to_the_raw_read()
    {
        // A PatchOnly / None manifest row carries no live branch to prefer — the legacy read is the ONLY answer.
        var id = Guid.NewGuid();
        var runs = new[] { Run(id, turn: 1, outputs: """{"branch":"cs/legacy"}""") };
        var manifests = new Dictionary<Guid, IReadOnlyList<PublishManifest>>
        {
            [id] = new[] { new PublishManifest { RepositoryAlias = "primary", Branch = null, PublishStateValue = PublishState.PatchOnly, Kind = PublishManifestKind.Agent } },
        };

        var t = SessionProjection.BuildTurns(runs, NonePending, manifests).ShouldHaveSingleItem();

        t.ProducedBranch.ShouldBe("cs/legacy");
    }

    [Fact]
    public void LatestRunBySession_picks_the_newest_run_per_session()
    {
        var s1 = Guid.NewGuid(); var s2 = Guid.NewGuid();
        var newest1 = Guid.NewGuid();
        var rows = new[]
        {
            new SessionProjection.SessionRunRow(s1, Guid.NewGuid(), WorkflowRunStatus.Failure, "single-agent", T0),
            new SessionProjection.SessionRunRow(s1, newest1, WorkflowRunStatus.Running, "supervisor", T0.AddMinutes(5)),
            new SessionProjection.SessionRunRow(s2, Guid.NewGuid(), WorkflowRunStatus.Success, "single-agent", T0),
        };

        var latest = SessionProjection.LatestRunBySession(rows);

        latest[s1].Id.ShouldBe(newest1);
        latest[s1].Status.ShouldBe(WorkflowRunStatus.Running, "the newest run drives the session's live badge");
        latest[s1].ProjectionKind.ShouldBe("supervisor");
        latest.Count.ShouldBe(2);
    }
}
