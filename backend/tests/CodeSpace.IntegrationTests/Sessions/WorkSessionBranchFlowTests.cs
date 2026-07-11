using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// Branch continuity (S4b + S4b-2): a CONTINUE clones EACH repo the run touches at the prior turn's PRODUCED branch
/// for THAT repo, so the agent builds on earlier CODE (not just the narrative). Proven against real Postgres through
/// the REAL <see cref="ITaskLaunchService"/> (the resolved refs land on the new run's frozen agent-code <c>baseRef</c>
/// [primary] + <c>relatedRepositories[].ref</c> [each related]; the provider's clone-at-ref is proven separately by
/// <c>LocalGitWorkspaceProviderTests</c>):
///   (a) a single-repo continue inherits the prior turn's branch; (b) the MOST RECENT produced branch wins, skipping a
///   later analysis-only turn; (c) no prior branch ⇒ no ref ⇒ the repo's default branch (safe fallback); (d) a
///   different repo never inherits another repo's branch; (e) a fresh launch carries no ref (byte-identical);
///   (f) a MULTI-repo continue inherits EACH repo's own prior branch from the prior turn's <c>repositoryResults</c>;
///   (g) a multi-repo prior turn also feeds a later SINGLE-repo continue's primary (the v1 Count!=1 limitation, fixed);
///   (h) I2: a turn's <see cref="PublishManifest"/> row wins over a disagreeing raw <c>OutputsJson.branch</c> guess;
///   (i)/(j) I2 MIXED-batch: in the same scanned window, a manifest-less turn never leaks an adjacent turn's manifest
///   branch, and a manifest-bearing turn's own manifest always wins over its own stale raw branch.
///
/// <para>Tier: high-fidelity Integration — real launch service + branch resolver over real Postgres; runs are
/// staged, not executed (the binding is established at launch). Per-repo branches read from the run's
/// <see cref="PublishManifest"/> row(s) when present (I2's single source of truth), else fall back to the legacy
/// <c>OutputsJson.repositoryResults[].producedBranch</c> (multi-repo) or flat <c>branch</c> (single-repo).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkSessionBranchFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkSessionBranchFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Continue_clones_the_primary_repo_at_the_prior_turns_branch()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoId = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedCodeTurnAsync(teamId, sessionId, turn: 1, repoId, branch: "run-1/x");

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoId, "Add tests on top"));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBe("run-1/x",
            "the continuing run clones the primary repo at the prior turn's produced branch — code carries forward");
    }

    [Fact]
    public async Task Continue_uses_the_most_recent_produced_branch_skipping_a_later_analysis_turn()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoId = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedCodeTurnAsync(teamId, sessionId, 1, repoId, "run-1/x");
        await SeedCodeTurnAsync(teamId, sessionId, 2, repoId, "run-2/x");
        await SeedCodeTurnAsync(teamId, sessionId, 3, repoId, branch: null);   // a later analysis-only turn changed no code

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoId, "Keep going"));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBe("run-2/x",
            "the latest code state is turn 2's branch (turn 3 produced none) — not turn 1, not base");
    }

    [Fact]
    public async Task Continue_clones_at_the_manifests_branch_even_when_OutputsJson_disagrees()
    {
        // I2 (real Postgres): PublishManifest is the single source of truth for what a turn produced — a CONTINUE
        // must never clone at a stale/guessed OutputsJson.branch once the manifest has the real answer.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoId = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);

        await SeedTurnWithManifestAsync(teamId, sessionId, turn: 1, repoId, outputsBranch: "stale-guessed-branch", manifestBranch: "codespace/agent/real");

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoId, "Add tests on top"));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBe("codespace/agent/real",
            "the manifest's branch wins over OutputsJson's — I2's single source of truth applies to session continuity, not just the room's own display");
    }

    [Fact]
    public async Task Continue_does_not_leak_an_older_turns_manifest_branch_onto_a_newer_manifest_less_turn()
    {
        // I2 (real Postgres, MIXED batch): the resolver bulk-loads manifests for every scanned turn in one query — a
        // newer turn with NO manifest row must fall back to ITS OWN raw OutputsJson, never accidentally pick up an
        // older turn's manifest branch through a dictionary-keying mistake.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoId = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);

        await SeedTurnWithManifestAsync(teamId, sessionId, turn: 1, repoId, outputsBranch: "run-1/stale-raw", manifestBranch: "run-1/manifest");
        await SeedCodeTurnAsync(teamId, sessionId, 2, repoId, "run-2/raw-only");   // newer, no manifest row at all

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoId, "Continue"));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBe("run-2/raw-only",
            "turn 2 (newest) has no manifest — must read its OWN raw branch, never turn 1's manifest branch");
    }

    [Fact]
    public async Task Continue_prefers_the_newer_turns_manifest_over_its_own_stale_raw_branch_in_a_mixed_batch()
    {
        // The reverse direction of the sibling test above: the NEWEST turn has BOTH a manifest and a disagreeing raw
        // OutputsJson, while an OLDER turn has only a raw branch — the bulk lookup must resolve the newest turn's own
        // manifest, never fall through to its raw read just because an earlier turn in the same batch had no manifest.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoId = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);

        await SeedCodeTurnAsync(teamId, sessionId, 1, repoId, "run-1/raw-only");   // older, no manifest row at all
        await SeedTurnWithManifestAsync(teamId, sessionId, turn: 2, repoId, outputsBranch: "run-2/stale-raw", manifestBranch: "run-2/manifest-wins");

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoId, "Continue"));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBe("run-2/manifest-wins",
            "turn 2 (newest) has its OWN manifest — it must win over its own stale raw branch");
    }

    [Fact]
    public async Task Continue_clones_the_rerun_winners_branch_not_the_failed_originals()
    {
        // Rerun-aware session reads (S4 fold): the ORIGINAL attempt failed with a stale/no manifest branch; a REAL
        // rerun (correct RootRunId lineage) then succeeded with its OWN pushed manifest branch. The turn's effective
        // attempt is the rerun — a CONTINUE must clone at ITS branch, never the superseded original's.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoId = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);

        await SeedRerunWinningTurnAsync(teamId, sessionId, turn: 1, repoId, originalBranch: "stale-failed-branch", rerunBranch: "codespace/agent/rerun-fixed");

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoId, "Keep going after the fix"));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBe("codespace/agent/rerun-fixed",
            "the rerun's own pushed branch wins over the failed original's stale/no branch");
    }

    [Fact]
    public async Task Continue_with_no_prior_branch_clones_the_default_branch()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoId = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedCodeTurnAsync(teamId, sessionId, 1, repoId, branch: null);   // analysis-only prior turn

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoId, "Now do the work"));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBeNull(
            "no prior turn produced a branch → no baseRef → the repo's default branch (the safe fallback)");
    }

    [Fact]
    public async Task Continue_targeting_a_different_repo_does_not_inherit_a_branch()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoA = await SeedRepositoryAsync(teamId);
        var repoB = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedCodeTurnAsync(teamId, sessionId, 1, repoA, "run-1/a");

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoB, "Work on a different repo"));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBeNull(
            "repo B has no prior branch in this session — repo A's branch must never bleed across repos");
    }

    [Fact]
    public async Task Continue_inherits_each_repos_own_branch_from_a_multi_repo_prior_turn()
    {
        // S4b-2: a multi-repo turn surfaces every writable repo's branch in OutputsJson.repositoryResults; a multi-repo
        // continue clones EACH repo (primary + related) at its OWN prior branch — no cross-repo bleed.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoA = await SeedRepositoryAsync(teamId);
        var repoB = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedMultiRepoTurnAsync(teamId, sessionId, 1, new Dictionary<Guid, string> { [repoA] = "run-1/a", [repoB] = "run-1/b" });

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoA, "Continue both", related: repoB));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBe("run-1/a", "the primary clones at its own prior branch");
        (await ReadAgentRelatedRefAsync(result.RunId, repoB)).ShouldBe("run-1/b", "the related repo clones at ITS own prior branch — per-repo continuity, no bleed");
    }

    [Fact]
    public async Task Continue_single_repo_inherits_the_primary_branch_even_from_a_multi_repo_prior_turn()
    {
        // The v1 fix: a multi-repo prior turn was SKIPPED entirely (Count != 1 guard), losing even the primary's
        // branch. Reading repositoryResults, a later SINGLE-repo continue now correctly inherits the primary's branch.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoA = await SeedRepositoryAsync(teamId);
        var repoB = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedMultiRepoTurnAsync(teamId, sessionId, 1, new Dictionary<Guid, string> { [repoA] = "run-1/a", [repoB] = "run-1/b" });

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoA, "Continue just A"));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBe("run-1/a",
            "a multi-repo prior turn now contributes the primary's branch (was skipped under the v1 Count!=1 guard)");
    }

    [Fact]
    public async Task Continue_accumulates_each_repos_newest_branch_across_mixed_single_and_multi_repo_turns()
    {
        // The heart of the generalized resolver: single-repo turns (flat branch) and multi-repo turns
        // (repositoryResults) BOTH feed one per-repo map, newest-wins per repo. Turn 1 produced both A + B; turn 2
        // (single-repo) re-touched only A. A continue over {A, B} must get A's NEWER branch AND B's OLDER one
        // (backfill — B's earlier code carries forward even though turn 2 didn't touch it).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoA = await SeedRepositoryAsync(teamId);
        var repoB = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedMultiRepoTurnAsync(teamId, sessionId, 1, new Dictionary<Guid, string> { [repoA] = "run-1/a", [repoB] = "run-1/b" });
        await SeedCodeTurnAsync(teamId, sessionId, 2, repoA, "run-2/a");   // a later SINGLE-repo turn touched only A

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoA, "Continue both", related: repoB));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBe("run-2/a", "A's NEWER single-repo branch shadows its older multi-repo one (newest-wins)");
        (await ReadAgentRelatedRefAsync(result.RunId, repoB)).ShouldBe("run-1/b", "B backfills from the older multi-repo turn — its code carries forward across turns");
    }

    [Fact]
    public async Task Continue_inherits_only_the_repo_a_multi_repo_turn_actually_produced()
    {
        // A multi-repo turn that produced a branch for A but not B (the common 'only one repo changed' steady state):
        // A inherits its branch, B falls back to its default — partial per-repo map, no cross-repo mis-attribution.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoA = await SeedRepositoryAsync(teamId);
        var repoB = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedMultiRepoTurnAsync(teamId, sessionId, 1, new Dictionary<Guid, string> { [repoA] = "run-1/a" });   // only A produced a branch

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoA, "Continue both", related: repoB));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBe("run-1/a", "A inherits its produced branch");
        (await ReadAgentRelatedRefAsync(result.RunId, repoB)).ShouldBeNull("B produced no branch — it falls back to its default, never A's");
    }

    [Fact]
    public async Task Continue_does_not_inherit_from_a_multi_repo_turn_that_produced_no_per_repo_branches()
    {
        // A multi-repo turn that changed nothing surfaces no repositoryResults (and its flat branch can't be attributed
        // per repo) → no inheritance → the safe default-branch fallback.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoA = await SeedRepositoryAsync(teamId);
        var repoB = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedTurnAsync(teamId, sessionId, 1, new[] { repoA, repoB }, branch: "run-1/primary");   // flat branch only, NO repositoryResults

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoA, "Continue"));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBeNull(
            "a multi-repo turn with only a flat branch (no per-repo results) attributes nothing — fall back to the default branch");
    }

    [Fact]
    public async Task A_fresh_launch_has_no_base_ref()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoId = await SeedRepositoryAsync(teamId);

        var result = await LaunchAsync(new TaskLaunchRequest
        {
            TeamId = teamId, ActorUserId = userId, SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Fresh start", RepositoryId = repoId, RequestedEffort = TaskEffortModes.Quick, Autonomy = "Confined",
        });

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBeNull("a fresh launch (no continue) clones the default branch — byte-identical");
    }

    [Fact]
    public async Task A_fresh_launch_with_an_operator_pinned_BaseBranch_clones_at_it_as_a_HARD_ref()
    {
        // H3: BaseBranch had a complete write chain (LaunchTaskCommand → TaskLaunchRequest → seed providers →
        // TaskLaunchSeed) and ZERO readers — an operator pinning a branch silently got the default. The pin must
        // reach the frozen agent node as a HARD ref (no baseRefFromSession soft marker: a missing pinned branch
        // fails LOUD at clone, never a silent default-branch fallback).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoId = await SeedRepositoryAsync(teamId);

        var result = await LaunchAsync(new TaskLaunchRequest
        {
            TeamId = teamId, ActorUserId = userId, SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Fix the login bug on the release line", RepositoryId = repoId, RequestedEffort = TaskEffortModes.Quick, Autonomy = "Confined",
            BaseBranch = "release/2.x",
        });

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBe("release/2.x", "the operator's pin must survive launch → seed → projection → the frozen node");

        var agent = await ReadAgentNodeAsync(result.RunId);
        agent.GetProperty("inputs").TryGetProperty("baseRefFromSession", out _)
            .ShouldBeFalse("an operator pin is HARD — only a SESSION-inherited transient branch carries the soft fallback marker");
    }

    [Fact]
    public async Task Continue_outranks_the_operator_pinned_BaseBranch()
    {
        // The prior turn's produced branch carries the thread's own work — cloning the pinned base instead would
        // silently discard every prior turn. The pin governs the FRESH launch only.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoId = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedCodeTurnAsync(teamId, sessionId, 1, repoId, "run-1/x");

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoId, "Keep going") with { BaseBranch = "release/2.x" });

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBe("run-1/x", "session continuity wins — the thread's own work lives on the prior produced branch");
    }

    [Fact]
    public async Task Branch_resolver_returns_the_most_recent_branch_for_the_repo()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedCodeTurnAsync(teamId, sessionId, 1, repoId, "run-1/x");
        await SeedCodeTurnAsync(teamId, sessionId, 2, repoId, "run-2/x");

        using var scope = _fixture.BeginScope();
        var resolved = await scope.Resolve<ISessionBranchResolver>().ResolveStartRefsAsync(sessionId, teamId, new[] { repoId }, CancellationToken.None);

        resolved[repoId].ShouldBe("run-2/x", "the resolver returns the newest produced branch for the repo");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static TaskLaunchRequest ContinueRequest(Guid teamId, Guid userId, Guid sessionId, Guid repoId, string text, Guid? related = null) => new()
    {
        TeamId = teamId, ActorUserId = userId, SurfaceKind = TaskLaunchSurfaceKinds.Chat,
        TaskText = text, ContinueSessionId = sessionId, RepositoryId = repoId,
        RelatedRepositories = related is { } r ? new[] { new TaskRelatedRepository { RepositoryId = r, Alias = "related", Access = "write" } } : null,
        RequestedEffort = TaskEffortModes.Quick, Autonomy = "Confined",
    };

    private async Task<LaunchTaskResult> LaunchAsync(TaskLaunchRequest request)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ITaskLaunchService>().LaunchAsync(request, CancellationToken.None);
    }

    /// <summary>Reads the projected agent.run node's <c>baseRef</c> input out of the frozen definition snapshot (null when absent ⇒ default branch).</summary>
    private async Task<string?> ReadAgentBaseRefAsync(Guid runId)
    {
        var agent = await ReadAgentNodeAsync(runId);
        return agent.GetProperty("inputs").TryGetProperty("baseRef", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>Reads a RELATED repo's <c>ref</c> out of the frozen agent.run node's <c>relatedRepositories</c> input (null when the repo/ref is absent ⇒ default branch).</summary>
    private async Task<string?> ReadAgentRelatedRefAsync(Guid runId, Guid repoId)
    {
        var agent = await ReadAgentNodeAsync(runId);

        if (!agent.GetProperty("inputs").TryGetProperty("relatedRepositories", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;

        foreach (var entry in arr.EnumerateArray())
        {
            if (entry.TryGetProperty("repositoryId", out var idEl) && idEl.GetString() == repoId.ToString())
                return entry.TryGetProperty("ref", out var refEl) && refEl.ValueKind == JsonValueKind.String ? refEl.GetString() : null;
        }

        return null;
    }

    private async Task<JsonElement> ReadAgentNodeAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        var root = JsonDocument.Parse(run.DefinitionSnapshotJson!).RootElement.Clone();
        return root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "agent");
    }

    private async Task<Guid> SeedSessionAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.CreateVersion7();
        db.WorkSession.Add(new WorkSession { Id = id, TeamId = teamId, Title = "thread", Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Stage a finished single-repo turn that targeted <paramref name="repoId"/> and (optionally) produced a branch — the shape a real single-agent code turn leaves (scope repo + OutputsJson.branch).</summary>
    private Task SeedCodeTurnAsync(Guid teamId, Guid sessionId, int turn, Guid repoId, string? branch) =>
        SeedTurnAsync(teamId, sessionId, turn, new[] { repoId }, branch);

    /// <summary>Stage a finished MULTI-repo turn whose OutputsJson carries per-repo <c>repositoryResults</c> ({ repositoryId, producedBranch }) — the shape a real multi-repo single-agent code turn leaves (scope = the repos; the flat <c>branch</c> mirrors the primary, the first entry).</summary>
    private async Task SeedMultiRepoTurnAsync(Guid teamId, Guid sessionId, int turn, IReadOnlyDictionary<Guid, string> repoBranches)
    {
        using var dbScope = _fixture.BeginScope();
        var db = dbScope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });

        var repositoryResults = repoBranches.Select(kv => new { repositoryId = kv.Key.ToString(), producedBranch = kv.Value }).ToList();
        var outputs = new { branch = repoBranches.Values.First(), repositoryResults };

        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = Guid.NewGuid(), TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = WorkflowRunStatus.Success, SessionId = sessionId, SessionTurnIndex = turn,
            ScopeRepositoryIds = repoBranches.Keys.ToList(),
            OutputsJson = JsonSerializer.Serialize(outputs),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>Stage a finished turn over an arbitrary repo scope (single- or multi-repo) with an optional produced branch.</summary>
    private async Task SeedTurnAsync(Guid teamId, Guid sessionId, int turn, IReadOnlyList<Guid> scope, string? branch)
    {
        using var dbScope = _fixture.BeginScope();
        var db = dbScope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = Guid.NewGuid(), TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = WorkflowRunStatus.Success, SessionId = sessionId, SessionTurnIndex = turn,
            ScopeRepositoryIds = scope.ToList(),
            OutputsJson = branch is null ? "{}" : JsonSerializer.Serialize(new { branch }),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>Stage a finished single-repo turn whose OutputsJson disagrees with its authoritative PublishManifest row — proves I2's "manifest wins" applies to session continuity, not just the room's own display.</summary>
    private async Task<Guid> SeedTurnWithManifestAsync(Guid teamId, Guid sessionId, int turn, Guid repoId, string outputsBranch, string manifestBranch)
    {
        using var dbScope = _fixture.BeginScope();
        var db = dbScope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = WorkflowRunStatus.Success, SessionId = sessionId, SessionTurnIndex = turn,
            ScopeRepositoryIds = new[] { repoId }.ToList(),
            OutputsJson = JsonSerializer.Serialize(new { branch = outputsBranch }),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, Kind = PublishManifestKind.Agent, WorkflowRunId = runId, RepositoryAlias = "primary",
            RepositoryId = repoId, Branch = manifestBranch, PublishStateValue = PublishState.Pushed,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>
    /// Stage a turn whose ORIGINAL attempt FAILED (with a stale raw branch, no manifest — a crashed run's leftover
    /// guess) and whose RERUN (real <c>RootRunId</c> lineage, later <c>CreatedDate</c>) SUCCEEDED with its own pushed
    /// <see cref="PublishManifest"/> branch — the shape a real rerun-after-failure leaves.
    /// </summary>
    private async Task SeedRerunWinningTurnAsync(Guid teamId, Guid sessionId, int turn, Guid repoId, string originalBranch, string rerunBranch)
    {
        using var dbScope = _fixture.BeginScope();
        var db = dbScope.Resolve<CodeSpaceDbContext>();

        var originalRequestId = Guid.NewGuid();
        var rerunRequestId = Guid.NewGuid();
        var originalId = Guid.NewGuid();
        var rerunId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.AddRange(
            new WorkflowRunRequest { Id = originalRequestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now },
            new WorkflowRunRequest { Id = rerunRequestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Rerun, ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now });

        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = originalId, TeamId = teamId, RunRequestId = originalRequestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = WorkflowRunStatus.Failure, SessionId = sessionId, SessionTurnIndex = turn,
            ScopeRepositoryIds = new[] { repoId }.ToList(),
            OutputsJson = JsonSerializer.Serialize(new { branch = originalBranch }),
            CreatedDate = now.AddMinutes(-5), CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = rerunId, TeamId = teamId, RunRequestId = rerunRequestId, SourceType = WorkflowRunSourceTypes.Rerun,
            Status = WorkflowRunStatus.Success, SessionId = sessionId, SessionTurnIndex = null, RootRunId = originalId, RerunFromNodeId = "agent",
            ScopeRepositoryIds = new[] { repoId }.ToList(),
            OutputsJson = JsonSerializer.Serialize(new { summary = "fixed on rerun" }),
            CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, Kind = PublishManifestKind.Agent, WorkflowRunId = rerunId, RepositoryAlias = "primary",
            RepositoryId = repoId, Branch = rerunBranch, PublishStateValue = PublishState.Pushed,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedRepositoryAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var instanceId = Guid.NewGuid();
        var repoId = Guid.NewGuid();

        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "GH", BaseUrl = $"https://gh-{suffix}.local", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Repository.Add(new Repository { Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = "api", FullPath = $"acme/api-{suffix}", WebUrl = "https://gh.local/acme/api", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return repoId;
    }

    private IDisposable PauseAutoExecute()
    {
        SetAutoExecute(clearFirst: true, value: false);
        return new Restore(this);
    }

    private void SetAutoExecute(bool clearFirst, bool value)
    {
        using var scope = _fixture.BeginScope();
        var jobClient = scope.Resolve<InMemoryBackgroundJobClient>();
        if (clearFirst) jobClient.Clear();
        jobClient.AutoExecute = value;
    }

    private sealed class Restore : IDisposable
    {
        private readonly WorkSessionBranchFlowTests _owner;
        public Restore(WorkSessionBranchFlowTests owner) { _owner = owner; }
        public void Dispose() => _owner.SetAutoExecute(clearFirst: false, value: true);
    }
}
