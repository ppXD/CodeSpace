using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.TaskLaunch;

public sealed partial class TaskLaunchBenchmarkCellRunner
{
    /// <summary>The ad-hoc repository + provider instance this cell launched against, and the team actor it launched as — all retired in <see cref="RetireFixtureResourcesAsync"/> regardless of outcome.</summary>
    private sealed record StagedFixture(Guid RepositoryId, Guid ProviderInstanceId, Guid ActorUserId);

    private const string FixtureDefaultBranch = "main";
    private const string GitAuthorEmail = "qualification@codespace.local";
    private const string GitAuthorName = "CodeSpace Qualification";

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

        return await InFreshScopeAsync(async scope =>
        {
            var db = scope.Resolve<CodeSpaceDbContext>();

            var actorUserId = await ResolveTeamOwnerAsync(db, context.TeamId, cancellationToken).ConfigureAwait(false);
            var (repositoryId, providerInstanceId) = await SeedFixtureRepositoryRowAsync(db, task, context, actorUserId, cancellationToken).ConfigureAwait(false);

            return new StagedFixture(repositoryId, providerInstanceId, actorUserId);
        }).ConfigureAwait(false);
    }

    private async Task InitFixtureGitRepoAsync(string directory, CancellationToken cancellationToken)
    {
        await RunGitAsync(new[] { "init", "-q", "-b", FixtureDefaultBranch }, directory, cancellationToken).ConfigureAwait(false);
        await RunGitAsync(new[] { "-c", $"user.email={GitAuthorEmail}", "-c", $"user.name={GitAuthorName}", "add", "-A" }, directory, cancellationToken).ConfigureAwait(false);
        await RunGitAsync(new[] { "-c", $"user.email={GitAuthorEmail}", "-c", $"user.name={GitAuthorName}", "commit", "-q", "--allow-empty", "-m", "fixture: pre-staged failing state" }, directory, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(Guid RepositoryId, Guid ProviderInstanceId)> SeedFixtureRepositoryRowAsync(CodeSpaceDbContext db, BenchmarkTask task, BenchmarkExecutionContext context, Guid actorUserId, CancellationToken cancellationToken)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var providerInstanceId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();

        db.ProviderInstance.Add(new ProviderInstance
        {
            Id = providerInstanceId, TeamId = context.TeamId, Provider = ProviderKind.GitHub,
            DisplayName = "codespace-qualification", BaseUrl = $"https://qualification-{suffix}.codespace.invalid",
            CreatedBy = actorUserId, LastModifiedBy = actorUserId,
        });

        db.Repository.Add(new Repository
        {
            Id = repositoryId, TeamId = context.TeamId, ProviderInstanceId = providerInstanceId,
            ExternalId = $"qualification-{suffix}", NamespacePath = "codespace-qualification", Name = task.Id,
            FullPath = $"codespace-qualification/{task.Id}-{suffix}", WebUrl = $"https://qualification-{suffix}.codespace.invalid/{task.Id}",
            // The clone SOURCE: the already-staged, now git-committed fixture directory. No CredentialId — the
            // workspace resolver clones anonymously, exactly the local-path git operation the direct instrument's
            // own staging already proved works offline.
            CloneUrlHttps = context.WorkspaceDirectory, DefaultBranch = FixtureDefaultBranch,
            CreatedBy = actorUserId, LastModifiedBy = actorUserId,
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return (repositoryId, providerInstanceId);
    }

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
