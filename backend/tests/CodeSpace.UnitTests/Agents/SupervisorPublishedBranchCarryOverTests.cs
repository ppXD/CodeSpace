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

    // ─── Harness ────────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<SupervisorRepositoryBranch>> ResolveAsync(SupervisorPriorDecision[] priorDecisions, Repository[] repositories, params PublishManifest[] manifests)
    {
        var workflowRunId = Guid.NewGuid();

        foreach (var manifest in manifests) manifest.WorkflowRunId = workflowRunId;

        await using var db = BuildDb(repositories);

        var resolver = new SupervisorPublishedBranchResolver(db, new StubManifestStore(manifests), NullLogger<SupervisorPublishedBranchResolver>.Instance);

        return await resolver.ResolveAsync(workflowRunId, TeamId, priorDecisions, primaryRepositoryId: null, CancellationToken.None);
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

    private static PublishManifest Pushed(Guid agentRunId, Guid repositoryId, string alias, string branch) => new()
    {
        Id = Guid.NewGuid(), TeamId = TeamId, Kind = PublishManifestKind.Agent, AgentRunId = agentRunId, RepositoryId = repositoryId,
        RepositoryAlias = alias, Branch = branch, PublishStateValue = PublishState.Pushed, CreatedDate = DateTimeOffset.UtcNow,
    };

    private static SupervisorAgentResult Unit(string status = "Succeeded", bool? acceptancePassed = null) =>
        new() { AgentRunId = Guid.NewGuid(), Status = status, ProducedBranch = "codespace/agent/x", AcceptancePassed = acceptancePassed };

    private static SupervisorPriorDecision Staging(string kind, params SupervisorAgentResult[] units)
    {
        var ids = units.Select(u => u.AgentRunId).ToArray();
        var outcome = SupervisorOutcome.FoldAgentResults(
            JsonSerializer.Serialize(new { agentRunIds = ids, agentCount = ids.Length }, AgentJson.Options), units);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcome };
    }

    private static SupervisorPriorDecision Plan(string subtaskId) => new()
    {
        Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = $$"""{"goal":"ship it","subtasks":[{"id":"{{subtaskId}}","title":"{{subtaskId}}","instruction":"do it"}]}""", OutcomeJson = "{}",
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
