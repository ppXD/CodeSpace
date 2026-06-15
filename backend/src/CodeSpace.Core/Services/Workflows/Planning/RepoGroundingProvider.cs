using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Planning;

/// <summary>
/// Builds the planner's repo-grounding string. Resolves the repo TEAM-SCOPED the same way the Code tab does
/// (repo → provider context + remote repository), then does ONE non-recursive root listing
/// (<see cref="IRepositorySourceCapability.ListTreeAsync"/>) and renders an honest top-level summary.
///
/// <para>Every exit other than a real listing is <c>null</c>: <c>repositoryId</c> null, repo not in the team or
/// missing, no bound credential, or ANY provider/scope failure. The whole read is wrapped so a grounding failure
/// degrades the plan to task-text-only — it never fails the planning call.</para>
/// </summary>
public sealed class RepoGroundingProvider : IRepoGroundingProvider, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IScopeChecker _scopeChecker;
    private readonly ILogger<RepoGroundingProvider> _logger;

    public RepoGroundingProvider(CodeSpaceDbContext db, IProviderRegistry registry, IScopeChecker scopeChecker, ILogger<RepoGroundingProvider> logger)
    {
        _db = db;
        _registry = registry;
        _scopeChecker = scopeChecker;
        _logger = logger;
    }

    public async Task<string?> BuildGroundingAsync(Guid? repositoryId, Guid teamId, CancellationToken cancellationToken)
    {
        if (repositoryId == null) return null;

        try
        {
            var repo = await LoadTeamScopedAsync(repositoryId.Value, teamId, cancellationToken).ConfigureAwait(false);

            if (repo == null) return null;

            var entries = await ListRootAsync(repo, cancellationToken).ConfigureAwait(false);

            return BuildSummary(repo, entries);
        }
        catch (Exception ex)
        {
            // Grounding is best-effort: any failure (provider 4xx/5xx, insufficient scope, transport) degrades the
            // plan to task-text-only. Never surface out of the planning call.
            _logger.LogDebug(ex, "Repo grounding skipped for repository {RepositoryId} (team {TeamId}); planning continues task-only", repositoryId, teamId);
            return null;
        }
    }

    /// <summary>Loads the repo ONLY when it belongs to <paramref name="teamId"/> — a repo in another team yields null (fail-closed, no cross-team read), indistinguishable from missing.</summary>
    private async Task<Repository?> LoadTeamScopedAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.Repository
            .Include(r => r.ProviderInstance)
            .Include(r => r.Credential)
            .SingleOrDefaultAsync(r => r.Id == repositoryId && r.TeamId == teamId && r.DeletedDate == null, cancellationToken).ConfigureAwait(false);

    /// <summary>Mirrors RepositorySourceService.ResolveAsync: credential check → source-read scope → capability + context → one root listing.</summary>
    private async Task<IReadOnlyList<RemoteTreeEntry>> ListRootAsync(Repository repo, CancellationToken cancellationToken)
    {
        if (repo.Credential == null) return Array.Empty<RemoteTreeEntry>();

        _scopeChecker.EnsureCapability(repo.Credential, repo.ProviderInstance.Provider, typeof(IRepositorySourceCapability));

        var source = _registry.Require<IRepositorySourceCapability>(repo.ProviderInstance.Provider);
        var context = new ProviderContext(repo.ProviderInstance, repo.Credential);

        return await source.ListTreeAsync(context, repo.ToRemoteRepository(), path: null, reference: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Honest framing: a top-level layout only, never "I analyzed your codebase". Lists up to <see cref="MaxEntries"/> root entries with their dir/file kind. Internal so the pure string assembly is unit-pinned directly (InternalsVisibleTo) — not only through integration coverage.</summary>
    internal static string BuildSummary(Repository repo, IReadOnlyList<RemoteTreeEntry> entries)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"Repository top-level layout for {repo.FullPath} (provider: {repo.ProviderInstance.Provider}, default branch: {repo.DefaultBranch}).");
        builder.AppendLine("This is a top-level listing only, not a full code analysis.");

        if (entries.Count == 0)
        {
            builder.Append("The repository root is empty.");
            return builder.ToString();
        }

        builder.AppendLine("Top-level entries:");
        foreach (var entry in entries.Take(MaxEntries))
            builder.AppendLine($"- {entry.Name} ({Describe(entry.Type)})");

        if (entries.Count > MaxEntries)
            builder.Append($"...and {entries.Count - MaxEntries} more.");

        return builder.ToString().TrimEnd();
    }

    private static string Describe(RemoteTreeEntryType type) => type == RemoteTreeEntryType.Directory ? "directory" : "file";

    /// <summary>Cap the listing so the prompt stays bounded for a wide root; the planner only needs the shape, not every node.</summary>
    private const int MaxEntries = 40;
}
