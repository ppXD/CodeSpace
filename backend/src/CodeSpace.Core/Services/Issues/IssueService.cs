using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Messages.Dtos.Providers;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Issues;

public sealed class IssueService : IIssueService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IScopeChecker _scopeChecker;
    private readonly IActorCredentialProvider _actorCredentials;

    public IssueService(CodeSpaceDbContext db, IProviderRegistry registry, IScopeChecker scopeChecker, IActorCredentialProvider actorCredentials)
    {
        _db = db;
        _registry = registry;
        _scopeChecker = scopeChecker;
        _actorCredentials = actorCredentials;
    }

    public async Task<RemoteIssue> CreateAsync(Guid repositoryId, CreateIssueInput input, Guid? actorUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Title)) throw new InvalidOperationException("An issue requires a title.");

        var repo = await LoadRepositoryAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        EnsureCredentialBound(repo);

        // Per-user attribution (Model B), same as PR open: create AS the actor's own linked identity when
        // wired, else the repo's connection credential. Scope + role are checked against whichever acts.
        var credential = await ResolveActingCredentialAsync(repo, actorUserId, cancellationToken).ConfigureAwait(false);
        _scopeChecker.EnsureCapability(credential, repo.ProviderInstance.Provider, typeof(IIssueWriteCapability));

        var writeCap = _registry.Require<IIssueWriteCapability>(repo.ProviderInstance.Provider);
        var context = new ProviderContext(repo.ProviderInstance, credential);
        var remote = repo.ToRemoteRepository();

        return await writeCap.CreateIssueAsync(context, remote, input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteIssueComment> CommentAsync(Guid repositoryId, int number, string body, Guid? actorUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new InvalidOperationException("A comment requires a body.");

        var repo = await LoadRepositoryAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        EnsureCredentialBound(repo);

        var credential = await ResolveActingCredentialAsync(repo, actorUserId, cancellationToken).ConfigureAwait(false);
        _scopeChecker.EnsureCapability(credential, repo.ProviderInstance.Provider, typeof(IIssueWriteCapability));

        var writeCap = _registry.Require<IIssueWriteCapability>(repo.ProviderInstance.Provider);
        var context = new ProviderContext(repo.ProviderInstance, credential);
        var remote = repo.ToRemoteRepository();

        return await writeCap.CommentIssueAsync(context, remote, number, body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Actor's own credential when <paramref name="actorUserId"/> is set (throws
    /// ActorIdentityRequiredException if they haven't linked one); otherwise the repo's connection credential.</summary>
    private async Task<Credential> ResolveActingCredentialAsync(Repository repo, Guid? actorUserId, CancellationToken cancellationToken) =>
        actorUserId is { } uid
            ? await _actorCredentials.RequireAsync(uid, repo.ProviderInstance, cancellationToken).ConfigureAwait(false)
            : repo.Credential!;

    private async Task<Repository> LoadRepositoryAsync(Guid repositoryId, CancellationToken cancellationToken) =>
        await _db.Repository
            .Include(r => r.ProviderInstance)
            .Include(r => r.Credential)
            .SingleOrDefaultAsync(r => r.Id == repositoryId && r.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found");

    private static void EnsureCredentialBound(Repository repo)
    {
        if (repo.Credential == null)
            throw new InvalidOperationException($"Repository {repo.Id} has no bound credential — relink first.");
    }
}
