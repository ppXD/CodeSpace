using System.Data.Common;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// Drives the REAL PublishManifestStore (resolved through DI, proving IScopedDependency auto-registration) against
/// real Postgres: the update-first upsert never duplicates a row for the same (agent_run_id, repository_alias) — the
/// idempotency guarantee a retry / reattach / reconciler re-run depends on — including under a genuine CONCURRENT
/// race for a brand-new key, where the ux_publish_manifest_agent unique index is the actual serialization point, not
/// a mock. Also covers the kind=Integration natural key and that a manifest row is written regardless of a
/// non-Succeeded owning run (I1).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PublishManifestStoreTests
{
    private readonly PostgresFixture _fixture;

    public PublishManifestStoreTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Upserting_the_same_agent_run_and_repo_twice_leaves_exactly_one_row()
    {
        var teamId = await SeedTeamAsync();
        var agentRunId = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
            await Svc(scope).UpsertForAgentRunAsync(agentRunId, Upsert(teamId, branch: null, changedFileCount: 3), CancellationToken.None);

        using (var scope = _fixture.BeginScope())
            await Svc(scope).UpsertForAgentRunAsync(agentRunId, Upsert(teamId, branch: "codespace/agent/abc", changedFileCount: 3), CancellationToken.None);

        using var read = _fixture.BeginScope();
        var rows = await Svc(read).ListForAgentRunAsync(agentRunId, teamId, CancellationToken.None);

        rows.Count.ShouldBe(1, "the second upsert must UPDATE the existing row, never insert a duplicate");
        rows[0].PublishStateValue.ShouldBe(PublishState.Pushed, "the second upsert's fields win — the row reflects the LATEST state");
        rows[0].Branch.ShouldBe("codespace/agent/abc");
    }

    [Fact]
    public async Task A_concurrent_first_upsert_for_a_brand_new_key_still_leaves_exactly_one_row()
    {
        var teamId = await SeedTeamAsync();
        var agentRunId = Guid.NewGuid();

        // Two INDEPENDENT scopes (fresh DbContexts, like two racing workers observing the same run) both attempt the
        // FIRST-EVER upsert for this key at once — the ux_publish_manifest_agent unique index is the real
        // serialization point (not a mock): one INSERT wins, the other must hit DbUpdateException and fold into an
        // UPDATE (PublishManifestStore.UpsertAsync's catch path), never a duplicate-key crash bubbling to the caller.
        using var scopeA = _fixture.BeginScope();
        using var scopeB = _fixture.BeginScope();

        await Task.WhenAll(
            Svc(scopeA).UpsertForAgentRunAsync(agentRunId, Upsert(teamId, branch: "codespace/agent/a", changedFileCount: 1), CancellationToken.None),
            Svc(scopeB).UpsertForAgentRunAsync(agentRunId, Upsert(teamId, branch: "codespace/agent/b", changedFileCount: 1), CancellationToken.None));

        using var read = _fixture.BeginScope();
        var rows = await Svc(read).ListForAgentRunAsync(agentRunId, teamId, CancellationToken.None);

        rows.Count.ShouldBe(1, "a racing duplicate INSERT must fold into an UPDATE, never leave two rows for the same key");
    }

    [Fact]
    public async Task Different_repository_aliases_for_the_same_agent_run_get_separate_rows()
    {
        var teamId = await SeedTeamAsync();
        var agentRunId = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
            await Svc(scope).UpsertForAgentRunAsync(agentRunId, Upsert(teamId, branch: "b-web", changedFileCount: 1) with { RepositoryAlias = "web" }, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
            await Svc(scope).UpsertForAgentRunAsync(agentRunId, Upsert(teamId, branch: "b-api", changedFileCount: 1) with { RepositoryAlias = "api" }, CancellationToken.None);

        using var read = _fixture.BeginScope();
        var rows = await Svc(read).ListForAgentRunAsync(agentRunId, teamId, CancellationToken.None);

        rows.Count.ShouldBe(2, "a multi-repo run gets one manifest row per writable repo, keyed on (agent_run_id, repository_alias)");
        rows.Select(r => r.RepositoryAlias).ShouldBe(new[] { "api", "web" }, ignoreOrder: true);
    }

    [Fact]
    public async Task An_integration_row_is_keyed_on_the_workflow_run_not_an_agent_run()
    {
        var teamId = await SeedTeamAsync();
        var workflowRunId = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
            await Svc(scope).UpsertForIntegrationAsync(Upsert(teamId, branch: "codespace/agent/integration", changedFileCount: 5) with { WorkflowRunId = workflowRunId }, CancellationToken.None);

        // A second upsert for the SAME workflow run must update, not duplicate — the ux_publish_manifest_integration index.
        using (var scope = _fixture.BeginScope())
            await Svc(scope).UpsertForIntegrationAsync(Upsert(teamId, branch: "codespace/agent/integration", changedFileCount: 7) with { WorkflowRunId = workflowRunId }, CancellationToken.None);

        using var read = _fixture.BeginScope();
        var rows = await Svc(read).ListForWorkflowRunAsync(workflowRunId, teamId, CancellationToken.None);

        rows.Count.ShouldBe(1, "the run-level integration lock keys on (workflow_run_id, repository_alias), independent of any agent_run_id");
        rows[0].ChangedFileCount.ShouldBe(7);
        rows[0].AgentRunId.ShouldBeNull("an Integration row belongs to the whole run, not one subtask");
    }

    [Fact]
    public async Task A_row_written_for_a_failed_runs_diff_is_still_readable_I1()
    {
        // I1: "did this leave a trace" must never depend on how the run ended — the manifest itself carries no
        // run-status column at all, so recording it for a Failed/TimedOut run is byte-identical to a Succeeded one.
        var teamId = await SeedTeamAsync();
        var agentRunId = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
            await Svc(scope).UpsertForAgentRunAsync(agentRunId, Upsert(teamId, branch: null, changedFileCount: 2) with { PublishError = "push failed: connection refused" }, CancellationToken.None);

        using var read = _fixture.BeginScope();
        var row = (await Svc(read).ListForAgentRunAsync(agentRunId, teamId, CancellationToken.None)).ShouldHaveSingleItem();

        row.PublishStateValue.ShouldBe(PublishState.PatchOnly);
        row.PublishError.ShouldBe("push failed: connection refused");
        row.ChangedFileCount.ShouldBe(2, "the captured diff's fact is recorded regardless of the owning run's eventual status");
    }

    [Fact]
    public async Task Batch_agent_run_read_is_one_team_scoped_query_with_deduplicated_owners_and_repository_order()
    {
        var teamId = await SeedTeamAsync();
        var foreignTeamId = await SeedTeamAsync();
        var firstOwner = Guid.NewGuid();
        var secondOwner = Guid.NewGuid();
        var foreignOwner = Guid.NewGuid();
        var missingOwner = Guid.NewGuid();

        await SeedManifestsAsync(
            Manifest(teamId, firstOwner, Guid.NewGuid(), "web", "first-web"),
            Manifest(teamId, firstOwner, Guid.NewGuid(), "api", "first-api"),
            Manifest(teamId, secondOwner, Guid.NewGuid(), "primary", "second"),
            Manifest(foreignTeamId, foreignOwner, Guid.NewGuid(), "primary", "foreign"));

        var recorder = new ReadCommandRecorder();
        using var read = ReadScope(recorder);
        var rows = await Svc(read).ListForAgentRunsAsync(new[] { secondOwner, firstOwner, firstOwner, foreignOwner, missingOwner }, teamId, maxAgentRunIds: 4, CancellationToken.None);

        recorder.Commands.Count.ShouldBe(1, "duplicate producer ids are folded before one SQL read, never one command per owner");
        rows.Keys.ShouldBe(new[] { firstOwner, secondOwner }, ignoreOrder: true, customMessage: "foreign-team and absent owners have no dictionary entry");
        rows[firstOwner].Select(row => row.RepositoryAlias).ShouldBe(new[] { "api", "web" }, "each owner's manifests retain ListForAgentRunAsync's repository-alias order");
        rows[firstOwner].Select(row => row.Branch).ShouldBe(new[] { "first-api", "first-web" });
        rows[firstOwner].Select(row => row.Summary).ShouldBe(new[] { "summary-first-api", "summary-first-web" }, "batching changes query count, never the manifest fields downstream folds consume");
        rows[secondOwner].ShouldHaveSingleItem().Branch.ShouldBe("second");

        var sql = recorder.Commands.Single();
        sql.ShouldContain("publish_manifest", Case.Insensitive);
        sql.ShouldContain("team_id", Case.Insensitive, customMessage: "tenancy must be in the SQL predicate, not post-filtered");
        sql.ShouldContain("agent_run_id", Case.Insensitive, customMessage: "the owner set must be in the SQL predicate");
        sql.ShouldNotContain("COUNT(", Case.Insensitive);
        sql.ShouldNotContain("OFFSET", Case.Insensitive);
    }

    [Fact]
    public async Task Empty_agent_run_batch_returns_without_a_database_query()
    {
        var recorder = new ReadCommandRecorder();
        using var read = ReadScope(recorder);

        var rows = await Svc(read).ListForAgentRunsAsync(Array.Empty<Guid>(), Guid.NewGuid(), maxAgentRunIds: 1, CancellationToken.None);

        rows.ShouldBeEmpty();
        recorder.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task Agent_run_batch_refuses_more_distinct_owners_than_the_callers_plan_bound_before_querying()
    {
        var recorder = new ReadCommandRecorder();
        using var read = ReadScope(recorder);

        var exception = await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            Svc(read).ListForAgentRunsAsync(new[] { Guid.NewGuid(), Guid.NewGuid() }, Guid.NewGuid(), maxAgentRunIds: 1, CancellationToken.None));

        exception.ParamName.ShouldBe("agentRunIds");
        recorder.Commands.ShouldBeEmpty("the active plan bound is an admission guard, not a post-query check");
    }

    [Fact]
    public async Task Agent_run_batch_database_fault_propagates_without_single_read_fallback()
    {
        var teamId = await SeedTeamAsync();
        var ownerId = Guid.NewGuid();
        await SeedManifestsAsync(Manifest(teamId, ownerId, Guid.NewGuid(), "primary", "branch"));

        var fault = new ThrowingReadInterceptor();
        using var read = ReadScope(fault);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            Svc(read).ListForAgentRunsAsync(new[] { ownerId }, teamId, maxAgentRunIds: 1, CancellationToken.None));

        exception.Message.ShouldBe(ThrowingReadInterceptor.Message);
        fault.AttemptCount.ShouldBe(1, "a failed batch read is surfaced once; it never degrades to per-owner reads");
    }

    // ─── helpers ───────────────────────────────────────────────────────────────

    private static IPublishManifestStore Svc(ILifetimeScope scope) => scope.Resolve<IPublishManifestStore>();

    private ILifetimeScope ReadScope(DbCommandInterceptor interceptor) => _fixture.BeginScope(builder =>
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention().AddInterceptors(interceptor).Options;
        builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
    });

    private static PublishManifestUpsert Upsert(Guid teamId, string? branch, int changedFileCount) => new()
    {
        TeamId = teamId,
        RepositoryAlias = "primary",
        BaseSha = "abc123",
        ChangedFileCount = changedFileCount,
        ChangedFilesJson = changedFileCount > 0 ? "[\"a.cs\"]" : null,
        PublishStateValue = branch is { Length: > 0 } ? PublishState.Pushed : PublishState.PatchOnly,
        Branch = branch,
    };

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"manifest-{userId:N}@test.local", Name = $"manifest-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"manifest-{teamId:N}", Name = "Manifest Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    private async Task SeedManifestsAsync(params PublishManifest[] manifests)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.PublishManifest.AddRange(manifests);
        await db.SaveChangesAsync();
    }

    private static PublishManifest Manifest(Guid teamId, Guid agentRunId, Guid repositoryId, string alias, string branch) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, Kind = PublishManifestKind.Agent, AgentRunId = agentRunId, RepositoryId = repositoryId,
        RepositoryAlias = alias, Branch = branch, Summary = $"summary-{branch}", PublishStateValue = PublishState.Pushed,
    };

    private sealed class ReadCommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowingReadInterceptor : DbCommandInterceptor
    {
        public const string Message = "manifest-batch-read-fault";

        public int AttemptCount { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            AttemptCount++;
            throw new InvalidOperationException(Message);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            AttemptCount++;
            throw new InvalidOperationException(Message);
        }
    }
}
