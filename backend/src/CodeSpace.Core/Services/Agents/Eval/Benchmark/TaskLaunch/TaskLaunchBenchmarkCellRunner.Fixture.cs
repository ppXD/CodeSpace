using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Exceptions;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.TaskLaunch;

public sealed partial class TaskLaunchBenchmarkCellRunner
{
    /// <summary>The ad-hoc repository + provider instance this cell launched against, and the team actor it launched as — all retired in <see cref="RetireFixtureResourcesAsync"/> regardless of outcome.</summary>
    private sealed record StagedFixture(Guid RepositoryId, Guid ProviderInstanceId, Guid ActorUserId);

    private const string FixtureDefaultBranch = "main";
    private const string GitAuthorEmail = "qualification@codespace.local";
    private const string GitAuthorName = "CodeSpace Qualification";
    internal const string FixtureCheckpointKind = "tasklaunch.fixture.v1";
    internal const string FixtureReadyCheckpointKind = "tasklaunch.fixture-ready.v1";

    /// <summary>
    /// Turn the ALREADY-STAGED fixture directory (<c>IBenchmarkFixtureStager</c> already ran before this instrument
    /// was reached — the corpus loop's own infra-fault handling covers an unknown fixture ref) into a git
    /// repository the real Launch entry can clone: one commit holding the fixture's pre-existing failing state.
    /// Then seeds a team-scoped, anonymous-clone <see cref="Repository"/> row pointing at it (a plain local path is
    /// a valid <c>CloneUrlHttps</c> — git clones a local path natively, no HTTPS host needed) so
    /// <c>RepositoryWorkspaceResolver</c> resolves it exactly like any real remote. Runs in its OWN scope: staging
    /// commits and fully completes before <see cref="LaunchAsync"/> opens its own separate one.
    /// </summary>
    private async Task<StagedFixture> StageFixtureRepositoryAsync(BenchmarkTask task, BenchmarkExecutionContext context, CancellationToken cancellationToken)
    {
        await InitFixtureGitRepoAsync(context.WorkspaceDirectory, cancellationToken).ConfigureAwait(false);

        if (context.Checkpoints.TryGetValue(FixtureCheckpointKind, out var existing))
        {
            var fixture = DeserializeCheckpoint<StagedFixture>(existing, FixtureCheckpointKind);
            await EnsureFixtureRepositoryRowsAsync(task, context, fixture, cancellationToken).ConfigureAwait(false);
            if (context.Checkpoints.TryGetValue(FixtureReadyCheckpointKind, out var ready))
            {
                if (DeserializeCheckpoint<StagedFixture>(ready, FixtureReadyCheckpointKind) != fixture) throw new DurableBenchmarkObservationException("TaskLaunch fixture-ready checkpoint does not match its fixture identity.");
            }
            else await PutCheckpointAsync(context, FixtureReadyCheckpointKind, fixture, CancellationToken.None).ConfigureAwait(false);
            return fixture;
        }

        if (context.Checkpoints.Count > 0) throw new DurableBenchmarkObservationException("TaskLaunch recovery checkpoints are missing the fixture identity.");

        var staged = await InFreshScopeAsync(async scope =>
        {
            var db = scope.Resolve<CodeSpaceDbContext>();

            var actorUserId = await ResolveTeamOwnerAsync(db, context.TeamId, cancellationToken).ConfigureAwait(false);
            return new StagedFixture(Guid.NewGuid(), Guid.NewGuid(), actorUserId);
        }).ConfigureAwait(false);

        if (context.CheckpointSink is not null) await PutCheckpointAsync(context, FixtureCheckpointKind, staged, CancellationToken.None).ConfigureAwait(false);
        await EnsureFixtureRepositoryRowsAsync(task, context, staged, cancellationToken).ConfigureAwait(false);
        if (context.CheckpointSink is not null) await PutCheckpointAsync(context, FixtureReadyCheckpointKind, staged, CancellationToken.None).ConfigureAwait(false);
        return staged;
    }

    private async Task InitFixtureGitRepoAsync(string directory, CancellationToken cancellationToken)
    {
        await RunGitAsync(new[] { "init", "-q", "-b", FixtureDefaultBranch }, directory, cancellationToken).ConfigureAwait(false);
        await RunGitAsync(new[] { "-c", $"user.email={GitAuthorEmail}", "-c", $"user.name={GitAuthorName}", "add", "-A" }, directory, cancellationToken).ConfigureAwait(false);
        await RunGitAsync(new[] { "-c", $"user.email={GitAuthorEmail}", "-c", $"user.name={GitAuthorName}", "commit", "-q", "--allow-empty", "-m", "fixture: pre-staged failing state" }, directory, cancellationToken).ConfigureAwait(false);
    }

    private Task EnsureFixtureRepositoryRowsAsync(BenchmarkTask task, BenchmarkExecutionContext context, StagedFixture fixture, CancellationToken cancellationToken) => InFreshScopeAsync(async scope =>
    {
        var db = scope.Resolve<CodeSpaceDbContext>();
        var provider = await db.ProviderInstance.IgnoreQueryFilters().SingleOrDefaultAsync(value => value.Id == fixture.ProviderInstanceId, cancellationToken).ConfigureAwait(false);
        var repository = await db.Repository.IgnoreQueryFilters().SingleOrDefaultAsync(value => value.Id == fixture.RepositoryId, cancellationToken).ConfigureAwait(false);
        if ((provider is null) != (repository is null)) throw new DurableBenchmarkObservationException("TaskLaunch fixture checkpoint resolves to a partial repository identity.");
        if (provider is not null && repository is not null)
        {
            if (provider.TeamId != context.TeamId || provider.DeletedDate is not null || repository.TeamId != context.TeamId || repository.ProviderInstanceId != fixture.ProviderInstanceId || repository.DeletedDate is not null || repository.Name != task.Id)
                throw new DurableBenchmarkObservationException("TaskLaunch fixture checkpoint does not match the durable repository identity.");
            if (repository.CloneUrlHttps != context.WorkspaceDirectory)
            {
                repository.CloneUrlHttps = context.WorkspaceDirectory;
                repository.LastModifiedBy = fixture.ActorUserId;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        var suffix = fixture.RepositoryId.ToString("N")[..8];

        db.ProviderInstance.Add(new ProviderInstance
        {
            Id = fixture.ProviderInstanceId, TeamId = context.TeamId, Provider = ProviderKind.GitHub,
            DisplayName = "codespace-qualification", BaseUrl = $"https://qualification-{suffix}.codespace.invalid",
            CreatedBy = fixture.ActorUserId, LastModifiedBy = fixture.ActorUserId,
        });

        db.Repository.Add(new Repository
        {
            Id = fixture.RepositoryId, TeamId = context.TeamId, ProviderInstanceId = fixture.ProviderInstanceId,
            ExternalId = $"qualification-{suffix}", NamespacePath = "codespace-qualification", Name = task.Id,
            FullPath = $"codespace-qualification/{task.Id}-{suffix}", WebUrl = $"https://qualification-{suffix}.codespace.invalid/{task.Id}",
            // The clone SOURCE: the already-staged, now git-committed fixture directory. No CredentialId — the
            // workspace resolver clones anonymously, exactly the local-path git operation the direct instrument's
            // own staging already proved works offline.
            CloneUrlHttps = context.WorkspaceDirectory, DefaultBranch = FixtureDefaultBranch,
            CreatedBy = fixture.ActorUserId, LastModifiedBy = fixture.ActorUserId,
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    });

    private static T DeserializeCheckpoint<T>(BenchmarkExecutionCheckpoint checkpoint, string expectedKind)
    {
        if (checkpoint.Kind != expectedKind) throw new DurableBenchmarkObservationException($"Unexpected TaskLaunch checkpoint kind '{checkpoint.Kind}'.");
        try { return JsonSerializer.Deserialize<T>(checkpoint.PayloadJson, Agents.AgentJson.Options) ?? throw new JsonException("null checkpoint"); }
        catch (JsonException exception) { throw new DurableBenchmarkObservationException($"TaskLaunch checkpoint '{expectedKind}' is invalid.", exception); }
    }

    private static Task PutCheckpointAsync<T>(BenchmarkExecutionContext context, string kind, T payload, CancellationToken cancellationToken) =>
        context.CheckpointSink?.PutAsync(kind, JsonSerializer.Serialize(payload, Agents.AgentJson.Options), cancellationToken)
        ?? throw new DurableBenchmarkObservationException("TaskLaunch durable recovery requires a checkpoint sink.");

    /// <summary>
    /// The team's active Owner — the actor a caller-supplied <c>teamId</c> (not an authenticated request) launches a
    /// qualification cell as. There is no real requesting user on this path (the qualification endpoint's caller
    /// supplies only a team), so this borrows the team's own Owner as the closest stand-in for "who launched this" —
    /// which is exactly why the launch's <c>WorkSession</c> (and its <c>Conversation</c> when one opens) must be
    /// retired afterward in <see cref="RetireFixtureResourcesAsync"/>, alongside the fixture repository + provider
    /// instance: none of it is real work the Owner did, and none of it may linger in that person's history.
    /// </summary>
    private static async Task<Guid> ResolveTeamOwnerAsync(CodeSpaceDbContext db, Guid teamId, CancellationToken cancellationToken)
    {
        var ownerId = await db.TeamMembership.AsNoTracking()
            .Where(m => m.TeamId == teamId && m.Role == TeamRole.Owner && !m.User.IsBot && m.User.DeletedDate == null && m.User.DeactivatedAt == null)
            .Select(m => (Guid?)m.UserId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return ownerId ?? throw new InvalidOperationException($"Team {teamId} has no active Owner to launch a TaskLaunch qualification cell as.");
    }

    /// <summary>
    /// Soft-delete/archive every ad-hoc resource this cell created — the fixture repository, its provider instance,
    /// and (once the launch got far enough to open one) the <c>WorkSession</c>/<c>Conversation</c> it launched under
    /// the team's real Owner — so NONE of it lingers as a real, visible team asset or a real person's history.
    /// <paramref name="sessionId"/> is null when the launch never reached <c>ITaskLaunchService.StageAsync</c> (no
    /// session was ever opened). Best-effort: a cleanup fault must not turn a graded cell into a corpus-wide throw.
    /// Its own fresh scope — a cleanup step with no reason to share a DbContext with anything above it.
    /// </summary>
    private async Task RetireFixtureResourcesAsync(StagedFixture fixture, Guid? sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await InFreshScopeAsync(async scope =>
            {
                var db = scope.Resolve<CodeSpaceDbContext>();

                await RetireFixtureRepositoryRowsAsync(db, fixture, cancellationToken).ConfigureAwait(false);

                if (sessionId is { } id) await RetireSessionArtifactsAsync(db, id, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TaskLaunchBenchmarkCellRunner: could not retire the ad-hoc fixture resources for repository {RepositoryId}", fixture.RepositoryId);
        }
    }

    /// <summary>Soft-delete the ad-hoc fixture repository + its provider instance — sequential on the ONE shared DbContext (EF Core forbids concurrent operations on one context), never lingering as a real, launchable team repo or a real Integrations-list connection.</summary>
    private static async Task RetireFixtureRepositoryRowsAsync(CodeSpaceDbContext db, StagedFixture fixture, CancellationToken cancellationToken)
    {
        await db.Repository.Where(r => r.Id == fixture.RepositoryId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DeletedDate, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        await db.ProviderInstance.Where(p => p.Id == fixture.ProviderInstanceId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedDate, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Archive the launch's <c>WorkSession</c> and SOFT-DELETE its <c>Conversation</c> (when a supervisor-tier
    /// launch opened one) — so the Owner the cell borrowed never sees this launch as a live thread in their own
    /// history. <c>WorkSession</c> has NO soft-delete column, so <see cref="WorkSessionStatus.Archived"/> is the
    /// strongest retirement available for it; <c>SessionReadService.ListAsync</c> (the team-wide sessions index)
    /// applies NO status filter, so an archived qualification session remains a row there — a disclosed residual
    /// (see this PR's Limitations), not something this method can close without a schema change. <c>Conversation</c>
    /// DOES have <see cref="Persistence.Entities.Conversation.DeletedDate"/>, which <c>ConversationService.ListForUserAsync</c>
    /// actually filters on (its own <c>Archived</c> flag is set too, for any reader that keys off it instead) — so the
    /// Conversation half of this retirement really does drop out of the team-wide conversations list.
    /// </summary>
    private static async Task RetireSessionArtifactsAsync(CodeSpaceDbContext db, Guid sessionId, CancellationToken cancellationToken)
    {
        var conversationId = await db.WorkSession.AsNoTracking().Where(s => s.Id == sessionId)
            .Select(s => s.ConversationId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        await db.WorkSession.Where(s => s.Id == sessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, WorkSessionStatus.Archived), cancellationToken).ConfigureAwait(false);

        if (conversationId is { } id)
            await db.Conversation.Where(c => c.Id == id)
                .ExecuteUpdateAsync(c => c.SetProperty(x => x.Archived, true).SetProperty(x => x.DeletedDate, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }
}
