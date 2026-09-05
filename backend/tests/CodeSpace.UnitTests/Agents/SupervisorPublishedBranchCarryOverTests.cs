using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: CONSERVATION across a plan-generation boundary on the PUBLISH rung. The merge rung's own carry-over
/// (<c>SupervisorMergeCarryOverTests</c>) fixed half of a live failure; the other half is here. DC-3's ledger-direct
/// fallback built its active-agent filter from the plan window's staging decisions, which is an EMPTY set — not null
/// — once the model re-plans AFTER a wave finished, so every genuinely pushed manifest was filtered out and the run
/// logged "Supervisor publish … resolved 0 target(s)" with three accepted branches sitting in the ledger. Pins that
/// an empty active generation falls back to the SAME settled-work floor the merge carries over, that a generation
/// which staged anything is untouched, and that the withhold door still holds on the carry-over path.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorPublishedBranchCarryOverTests
{
    private static readonly Guid TeamId = Guid.NewGuid();

    [Fact]
    public async Task A_replan_after_the_wave_finished_still_resolves_the_branches_it_stranded()
    {
        // The live trajectory (run 3a49c716): plan(2) → spawn×2, both Succeeded and PUSHED → plan → publish.
        var web = Guid.NewGuid();
        var api = Guid.NewGuid();
        var a = Unit();
        var b = Unit();

        var branches = await ResolveAsync(
            new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, a, b), Plan("s2") },
            new[] { Repo(web), Repo(api) },
            Pushed(a.AgentRunId, web, "web", "codespace/agent/a"), Pushed(b.AgentRunId, api, "api", "codespace/agent/b"));

        branches.Select(x => x.SourceBranch).ShouldBe(new[] { "codespace/agent/a", "codespace/agent/b" }, ignoreOrder: true,
            "two finished, pushed, accepted branches must not become unpublishable because the model re-planned after they landed");
        branches.Select(x => x.TargetBranch).ShouldAllBe(t => t == "main");
    }

    [Fact]
    public async Task An_active_generation_that_staged_its_own_work_is_untouched()
    {
        var stale = Guid.NewGuid();
        var current = Guid.NewGuid();
        var old = Unit();
        var fresh = Unit();

        var branches = await ResolveAsync(
            new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, old), Plan("s2"), Staging(SupervisorDecisionKinds.Spawn, fresh) },
            new[] { Repo(stale), Repo(current) },
            Pushed(old.AgentRunId, stale, "web", "codespace/agent/old"), Pushed(fresh.AgentRunId, current, "api", "codespace/agent/fresh"));

        branches.Select(x => x.SourceBranch).ShouldBe(new[] { "codespace/agent/fresh" },
            "the active generation staged its own work — a superseded generation's branch stays audit evidence, exactly as before");
    }

    [Theory]
    [InlineData("Succeeded", false, null)]
    [InlineData("Succeeded", null, VerificationDisposition.Waived)]
    [InlineData("Failed", null, null)]
    public async Task An_earlier_result_that_never_settled_green_is_never_carried_over(string status, bool? acceptancePassed, VerificationDisposition? verdict)
    {
        var repositoryId = Guid.NewGuid();
        var unpublishable = Unit(status, acceptancePassed) with { AcceptanceVerdict = verdict };

        var branches = await ResolveAsync(
            new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, unpublishable), Plan("s2") },
            new[] { Repo(repositoryId) },
            Pushed(unpublishable.AgentRunId, repositoryId, "primary", "codespace/agent/x"));

        branches.ShouldBeEmpty("a raw push happens before the grade folds — the carry-over uses the SAME withhold door every other entrance to the head does, and only ever conserves FINISHED work");
    }

    [Fact]
    public async Task A_generation_whose_only_staged_unit_was_rejected_still_resolves_the_earlier_branch()
    {
        // The unified trigger: "staged nothing" and "staged only withheld work" are the SAME state to a door to the
        // head. This rung used to fire only on the first, so a re-plan that spawned once and got rejected published
        // nothing while the merge rung happily carried the earlier branch over — two rungs, two answers, one tape.
        var stale = Guid.NewGuid();
        var current = Guid.NewGuid();
        var done = Unit();
        var rejected = Unit(acceptancePassed: false);

        var branches = await ResolveAsync(
            new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, done), Plan("s2"), Staging(SupervisorDecisionKinds.Spawn, rejected) },
            new[] { Repo(stale), Repo(current) },
            Pushed(done.AgentRunId, stale, "web", "codespace/agent/done"), Pushed(rejected.AgentRunId, current, "api", "codespace/agent/rejected"));

        branches.Select(x => x.SourceBranch).ShouldBe(new[] { "codespace/agent/done" },
            "a rejected unit is no result at all — the generation has nothing of its own, and the rejected branch never reaches the head either way");
    }

    [Theory]
    [InlineData(false, "codespace/integration/turn1")]   // an ordinary re-plan STRANDS that head — conserve it
    [InlineData(true, null)]                             // the plan called that direction wrong — publish nothing of it
    public async Task An_earlier_generations_integrated_head_outranks_its_contributors_unless_the_plan_abandoned_it(bool abandoned, string? expected)
    {
        // Arm 1 — the carry-over must not REORDER the ladder it reads. A gen-1 merge that integrated cleanly is the
        // run's reviewable head; publishing gen-1's individual contributor branches instead would open a PR on ONE
        // agent's partial work (newest-per-alias picks a single contributor) while a combined head sat one rung above.
        //
        // Arm 2 — and the abandonment has to reach that SAME rung. The exclusion lived only on the ledger-direct floor,
        // so the integrated-branch rung above it still read the whole tape and published exactly the head the flag had
        // declared unpublishable: the merge folded none of the abandoned work, and the run shipped it anyway.
        var repositoryId = Guid.NewGuid();
        var a = Unit();
        var b = Unit();

        var branches = await ResolveAsync(
            new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, a, b), IntegratedMerge("codespace/integration/turn1"), Plan("s2", abandonEarlierResults: abandoned) },
            new[] { Repo(repositoryId) },
            primaryRepositoryId: repositoryId,
            Pushed(a.AgentRunId, repositoryId, "primary", "codespace/agent/a"), Pushed(b.AgentRunId, repositoryId, "primary", "codespace/agent/b"));

        branches.Select(x => x.SourceBranch).ShouldBe(expected is null ? Array.Empty<string>() : new[] { expected },
            "the head an earlier generation actually integrated is what a re-plan strands — unless that plan abandoned the direction that produced it");
        branches.Select(x => x.TargetBranch).ShouldAllBe(t => t == "main");
    }

    [Fact]
    public async Task A_contributor_branch_is_never_published_past_a_run_that_already_integrated()
    {
        // The same hazard where the walk CANNOT surface the older head: gen 2 staged fresh work (rejected), which is
        // the walk's own barrier — an earlier branch must not be surfaced past un-integrated work. With no head to
        // fall back to, the carry-over must stay shut rather than ship a partial contributor branch.
        var repositoryId = Guid.NewGuid();
        var a = Unit();
        var rejected = Unit(acceptancePassed: false);

        var branches = await ResolveAsync(
            new[]
            {
                Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, a), IntegratedMerge("codespace/integration/turn1"),
                Plan("s2"), Staging(SupervisorDecisionKinds.Spawn, rejected), Plan("s3"),
            },
            new[] { Repo(repositoryId) },
            primaryRepositoryId: repositoryId,
            Pushed(a.AgentRunId, repositoryId, "primary", "codespace/agent/a"));

        branches.ShouldBeEmpty("silence beats a partial head — a run that integrated once must never deliver one agent's own branch instead");
    }

    [Fact]
    public async Task An_integrity_failed_resolve_still_bars_the_carry_over_after_a_replan()
    {
        // The barrier reads the ACTIVE generation's staging frontier, so a re-plan moved the failed resolve out of
        // view and the carry-over published exactly the older contributor branch the barrier exists to forbid — the
        // conflicted, incomplete head the reconciliation was meant to replace.
        var repositoryId = Guid.NewGuid();
        var a = Unit();
        var resolver = Unit();

        var branches = await ResolveAsync(
            new[]
            {
                Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, a), ConflictedMerge(),
                IntegrityFailedResolve(resolver), Plan("s2"),
            },
            new[] { Repo(repositoryId) },
            Pushed(a.AgentRunId, repositoryId, "primary", "codespace/agent/a"));

        branches.ShouldBeEmpty("a plan-generation boundary must not clear a barrier — the resolver's own contributors could not be materialized, so nothing here is publishable");
    }

    [Fact]
    public async Task An_integrity_failed_resolve_bars_a_generation_whose_only_unit_was_rejected()
    {
        // The barrier has to widen with the trigger it guards: the unified predicate lets a generation whose staged
        // work is ALL withheld carry earlier work over, so a rejected spawn must not count as a frontier either — it
        // takes nothing to the head, and so supersedes nothing behind it. Read the other way, widening the carry-over
        // would itself have re-opened the exact hole this barrier exists to close.
        var repositoryId = Guid.NewGuid();
        var a = Unit();
        var resolver = Unit();
        var rejected = Unit(acceptancePassed: false);

        var branches = await ResolveAsync(
            new[]
            {
                Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, a), ConflictedMerge(),
                IntegrityFailedResolve(resolver), Plan("s2"), Staging(SupervisorDecisionKinds.Spawn, rejected),
            },
            new[] { Repo(repositoryId) },
            Pushed(a.AgentRunId, repositoryId, "primary", "codespace/agent/a"));

        branches.ShouldBeEmpty("a rejected unit cannot clear a barrier any more than a re-plan can — the incomplete head the resolve was meant to replace stays unpublishable");
    }

    [Fact]
    public async Task A_resolvers_own_branch_is_carried_over_instead_of_the_contributor_it_reconciled()
    {
        // The carry-over floor has to span every agent-STAGING verb. Filtering it to spawn/retry conserved the stale
        // half a resolver had already reconciled and silently dropped the reconciliation itself.
        var repositoryId = Guid.NewGuid();
        var a = Unit();
        var resolver = Unit();

        var branches = await ResolveAsync(
            new[]
            {
                Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, a), ConflictedMerge(),
                Staging(SupervisorDecisionKinds.Resolve, resolver), Plan("s2"),
            },
            new[] { Repo(repositoryId) },
            Pushed(a.AgentRunId, repositoryId, "primary", "codespace/agent/a", DateTimeOffset.UtcNow.AddMinutes(-5)),
            Pushed(resolver.AgentRunId, repositoryId, "primary", "codespace/resolve/r", DateTimeOffset.UtcNow));

        branches.Select(x => x.SourceBranch).ShouldBe(new[] { "codespace/resolve/r" },
            "the newest manifest per alias wins — and the resolver's branch has to be IN the carried-over set for it to win at all");
    }

    // ─── Harness ────────────────────────────────────────────────────────────────

    private static Task<IReadOnlyList<SupervisorRepositoryBranch>> ResolveAsync(SupervisorPriorDecision[] priorDecisions, Repository[] repositories, params PublishManifest[] manifests) =>
        ResolveAsync(priorDecisions, repositories, primaryRepositoryId: null, manifests);

    private static async Task<IReadOnlyList<SupervisorRepositoryBranch>> ResolveAsync(SupervisorPriorDecision[] priorDecisions, Repository[] repositories, Guid? primaryRepositoryId, params PublishManifest[] manifests)
    {
        var workflowRunId = Guid.NewGuid();

        foreach (var manifest in manifests) manifest.WorkflowRunId = workflowRunId;

        await using var db = BuildDb(repositories);

        var resolver = new SupervisorPublishedBranchResolver(db, new StubManifestStore(manifests), NullLogger<SupervisorPublishedBranchResolver>.Instance);

        return await resolver.ResolveAsync(workflowRunId, TeamId, priorDecisions, primaryRepositoryId, CancellationToken.None);
    }

    private static CodeSpaceDbContext BuildDb(Repository[] repositories)
    {
        var db = new CodeSpaceDbContext(new DbContextOptionsBuilder<CodeSpaceDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        db.Repository.AddRange(repositories);
        db.SaveChanges();

        return db;
    }

    private static Repository Repo(Guid id) => new()
    {
        Id = id, TeamId = TeamId, ProviderInstanceId = Guid.NewGuid(), ExternalId = "1", NamespacePath = "acme", Name = "app",
        FullPath = "acme/app", DefaultBranch = "main", WebUrl = "https://example.test/acme/app",
    };

    private static PublishManifest Pushed(Guid agentRunId, Guid repositoryId, string alias, string branch, DateTimeOffset? createdDate = null) => new()
    {
        Id = Guid.NewGuid(), TeamId = TeamId, Kind = PublishManifestKind.Agent, AgentRunId = agentRunId, RepositoryId = repositoryId,
        RepositoryAlias = alias, Branch = branch, PublishStateValue = PublishState.Pushed, CreatedDate = createdDate ?? DateTimeOffset.UtcNow,
    };

    /// <summary>A merge that landed one reviewable head — what makes the run's earlier work an INTEGRATED branch rather than a set of contributor branches.</summary>
    private static SupervisorPriorDecision IntegratedMerge(string integratedBranch) =>
        Merge(new { status = "Clean", integratedBranch });

    /// <summary>A merge whose integration CONFLICTED — it landed nothing, so the contributors it names are still the run's only published artifacts.</summary>
    private static SupervisorPriorDecision ConflictedMerge() =>
        Merge(new { status = "Conflicted", reason = "overlapping edits" });

    private static SupervisorPriorDecision Merge(object integration) => new()
    {
        Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Merge, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}",
        OutcomeJson = JsonSerializer.Serialize(new { merged = Array.Empty<object>(), count = 0, integration }, AgentJson.Options),
    };

    /// <summary>A <c>resolve</c> whose contributor branches could not be materialized — the shape <c>HasResolveContributorIntegrity</c> reads, which bars publishing an older contributor in its place.</summary>
    private static SupervisorPriorDecision IntegrityFailedResolve(SupervisorAgentResult resolver)
    {
        var outcome = SupervisorOutcome.FoldAgentResults(
            JsonSerializer.Serialize(new { agentRunIds = new[] { resolver.AgentRunId }, agentCount = 1, resolveContributorIntegrity = new { issues = new[] { new { kind = "MissingRow" } } } }, AgentJson.Options),
            new[] { resolver });

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Resolve, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcome };
    }

    private static SupervisorAgentResult Unit(string status = "Succeeded", bool? acceptancePassed = null) =>
        new() { AgentRunId = Guid.NewGuid(), Status = status, ProducedBranch = "codespace/agent/x", AcceptancePassed = acceptancePassed };

    private static SupervisorPriorDecision Staging(string kind, params SupervisorAgentResult[] units)
    {
        var ids = units.Select(u => u.AgentRunId).ToArray();
        var outcome = SupervisorOutcome.FoldAgentResults(
            JsonSerializer.Serialize(new { agentRunIds = ids, agentCount = ids.Length }, AgentJson.Options), units);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcome };
    }

    private static SupervisorPriorDecision Plan(string subtaskId, bool abandonEarlierResults = false) => new()
    {
        Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = $$"""{"goal":"ship it","subtasks":[{"id":"{{subtaskId}}","title":"{{subtaskId}}","instruction":"do it"}]{{(abandonEarlierResults ? ""","abandonEarlierResults":true""" : "")}}}""", OutcomeJson = "{}",
    };

    /// <summary>Only <see cref="IPublishManifestStore.ListForWorkflowRunAsync"/> is a ledger-direct read — every other member is out of this resolver's reach and must stay unreachable, never quietly stubbed.</summary>
    private sealed class StubManifestStore : IPublishManifestStore
    {
        private readonly IReadOnlyList<PublishManifest> _rows;

        public StubManifestStore(IReadOnlyList<PublishManifest> rows) => _rows = rows;

        public Task<IReadOnlyList<PublishManifest>> ListForWorkflowRunAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PublishManifest>>(_rows.Where(m => m.WorkflowRunId == workflowRunId && m.TeamId == teamId).OrderByDescending(m => m.CreatedDate).ToList());

        public Task UpsertForAgentRunAsync(Guid agentRunId, PublishManifestUpsert input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpsertForAgentRunAsync(Guid agentRunId, PublishManifestUpsert input, long expectedFenceEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpsertForIntegrationAsync(PublishManifestUpsert input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StampAcceptanceForAgentRunAsync(Guid agentRunId, PublishAcceptanceState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PublishManifest>> ListForAgentRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<PublishManifest>>> ListForAgentRunsAsync(IReadOnlyCollection<Guid> agentRunIds, Guid teamId, int maxAgentRunIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<PublishManifest>>> ListForWorkflowRunsAsync(IReadOnlyCollection<Guid> workflowRunIds, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
