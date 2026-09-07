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
    /// <summary>The ad-hoc repository this cell launched against, and the team actor it launched as — retired in <see cref="RetireFixtureRepositoryAsync"/> regardless of outcome.</summary>
    private sealed record StagedFixture(Guid RepositoryId, Guid ActorUserId);

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
            var repositoryId = await SeedFixtureRepositoryRowAsync(db, task, context, actorUserId, cancellationToken).ConfigureAwait(false);

            return new StagedFixture(repositoryId, actorUserId);
        }).ConfigureAwait(false);
    }

    private async Task InitFixtureGitRepoAsync(string directory, CancellationToken cancellationToken)
    {
        await RunGitAsync(new[] { "init", "-q", "-b", FixtureDefaultBranch }, directory, cancellationToken).ConfigureAwait(false);
        await RunGitAsync(new[] { "-c", $"user.email={GitAuthorEmail}", "-c", $"user.name={GitAuthorName}", "add", "-A" }, directory, cancellationToken).ConfigureAwait(false);
        await RunGitAsync(new[] { "-c", $"user.email={GitAuthorEmail}", "-c", $"user.name={GitAuthorName}", "commit", "-q", "--allow-empty", "-m", "fixture: pre-staged failing state" }, directory, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Guid> SeedFixtureRepositoryRowAsync(CodeSpaceDbContext db, BenchmarkTask task, BenchmarkExecutionContext context, Guid actorUserId, CancellationToken cancellationToken)
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

        return repositoryId;
    }

    /// <summary>The team's active Owner — the actor a caller-supplied <c>teamId</c> (not an authenticated request) launches a qualification cell as, mirroring how every other operator-triggered Q-ops entry resolves an actor for a team it wasn't handed a user for.</summary>
    private static async Task<Guid> ResolveTeamOwnerAsync(CodeSpaceDbContext db, Guid teamId, CancellationToken cancellationToken)
    {
        var ownerId = await db.TeamMembership.AsNoTracking()
            .Where(m => m.TeamId == teamId && m.Role == TeamRole.Owner && !m.User.IsBot && m.User.DeletedDate == null && m.User.DeactivatedAt == null)
            .Select(m => (Guid?)m.UserId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return ownerId ?? throw new InvalidOperationException($"Team {teamId} has no active Owner to launch a TaskLaunch qualification cell as.");
    }

    /// <summary>Soft-delete the ad-hoc fixture repository so it never lingers as a real, launchable team repo. Best-effort: a cleanup fault must not turn a graded cell into a corpus-wide throw. Its own fresh scope — a cleanup step with no reason to share a DbContext with anything above it.</summary>
    private async Task RetireFixtureRepositoryAsync(StagedFixture fixture, CancellationToken cancellationToken)
    {
        try
        {
            await InFreshScopeAsync(scope =>
                scope.Resolve<CodeSpaceDbContext>().Repository.Where(r => r.Id == fixture.RepositoryId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.DeletedDate, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken)
            ).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TaskLaunchBenchmarkCellRunner: could not retire the ad-hoc fixture repository {RepositoryId}", fixture.RepositoryId);
        }
    }
}
