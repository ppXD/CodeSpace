using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Tasks.SpecPreview;

/// <summary>Reads model-selected repository evidence at one observed commit. It never executes a proposed check, and unavailable content never becomes evidence of absence.</summary>
public sealed class TaskSpecEvidenceReader : ITaskSpecEvidenceReader, IScopedDependency
{
    private const int MaxFiles = 4;
    private const int MaxFileCharacters = 16000;
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _providers;
    private readonly IScopeChecker _scopeChecker;
    private readonly ILogger<TaskSpecEvidenceReader> _logger;

    public TaskSpecEvidenceReader(CodeSpaceDbContext db, IProviderRegistry providers, IScopeChecker scopeChecker, ILogger<TaskSpecEvidenceReader> logger)
    {
        _db = db;
        _providers = providers;
        _scopeChecker = scopeChecker;
        _logger = logger;
    }

    public async Task<TaskSpecEvidenceContext> CaptureAsync(TaskSpecEvidenceRequest request, CancellationToken cancellationToken)
    {
        if (request.RepositoryId is null) return TaskSpecEvidenceContext.TaskOnly(request, TaskSpecRepositoryState.NotRequested, "No repository was requested; the user goal is available as evidence.");
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var repo = await LoadAsync(request, bounded.Token).ConfigureAwait(false);
            if (!CanRead(repo, request.TeamId)) return Unavailable(request, "Repository source or a usable credential is unavailable; its contents are unknown.");
            var source = Resolve(repo!);
            var context = new ProviderContext(repo!.ProviderInstance, repo.Credential!);
            var branches = await source.ListBranchesAsync(context, repo.ToRemoteRepository(), bounded.Token).WaitAsync(bounded.Token).ConfigureAwait(false);
            var reference = branches.FirstOrDefault(b => b.Name == repo.DefaultBranch)?.CommitSha;
            if (string.IsNullOrWhiteSpace(reference)) return Unavailable(request, "An immutable repository reference could not be resolved; its contents are unknown.");
            var entries = await source.ListTreeAsync(context, repo.ToRemoteRepository(), path: null, reference, bounded.Token).WaitAsync(bounded.Token).ConfigureAwait(false);
            var observation = new TaskSpecRepositoryObservation { RepositoryId = repo.Id, Reference = reference, State = entries.Count == 0 ? TaskSpecRepositoryState.ObservedEmpty : TaskSpecRepositoryState.Observed, Detail = entries.Count == 0 ? "The root was read at this commit and is empty." : "The root was read at this commit; a listing alone does not establish an executable command." };
            return new TaskSpecEvidenceContext(request, observation, [TaskSpecSource.Create("goal", "user-goal", request.Goal), TaskSpecSource.Create("repository-layout", "repository-layout", RepoGroundingProvider.BuildSummary(repo, entries, reference), reference: reference)]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(request, "Repository observation timed out; its contents are unknown."); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spec evidence observation failed for team {TeamId}, repository {RepositoryId}", request.TeamId, request.RepositoryId);
            return Unavailable(request, "Repository observation failed; its contents are unknown.");
        }
    }

    public async Task<TaskSpecEvidenceContext> ReadFilesAsync(TaskSpecEvidenceContext context, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (paths.Count == 0) return context;
        if (!context.Grounded || context.Repository.Reference is null) return context with { ReadFailures = ["Requested repository evidence is unavailable."] };
        var sources = context.Sources.ToList();
        var failures = new List<string>();
        var selected = paths.Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length > MaxFiles) failures.Add($"The evidence read budget permits {MaxFiles} files; additional dependencies remain unknown.");
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var repo = await LoadAsync(context.Request, bounded.Token).ConfigureAwait(false);
            if (!CanRead(repo, context.Request.TeamId)) return context with { ReadFailures = ["Repository access is no longer available; requested file evidence was not read."] };
            var source = Resolve(repo!);
            var providerContext = new ProviderContext(repo!.ProviderInstance, repo.Credential!);
            foreach (var path in selected.Take(MaxFiles))
            {
                if (!IsRelativeFilePath(path)) { failures.Add("A proposed evidence path is not a bounded relative repository file."); continue; }
                try
                {
                    var file = await source.GetFileAsync(providerContext, repo.ToRemoteRepository(), path, context.Repository.Reference, bounded.Token).WaitAsync(bounded.Token).ConfigureAwait(false);
                    if (file.Path != path || file.IsBinary || file.IsTruncated || file.Text is null || file.Text.Length > MaxFileCharacters)
                    {
                        failures.Add($"{path}: complete text evidence was unavailable.");
                        continue;
                    }
                    sources.Add(TaskSpecSource.Create($"file:{path}", "repository-file", file.Text, path, context.Repository.Reference));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Spec evidence file read failed for repository {RepositoryId}, path {Path}", repo.Id, path);
                    failures.Add($"{path}: the evidence read failed.");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { failures.Add("The evidence read deadline expired; remaining dependencies are unknown."); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spec dependency evidence failed for team {TeamId}, repository {RepositoryId}", context.Request.TeamId, context.Request.RepositoryId);
            failures.Add("Repository dependency evidence is unavailable.");
        }
        return context with { Sources = sources, ReadFailures = failures };
    }

    private Task<Repository?> LoadAsync(TaskSpecEvidenceRequest request, CancellationToken cancellationToken) => _db.Repository.AsNoTracking().Include(r => r.ProviderInstance).Include(r => r.Credential).SingleOrDefaultAsync(r => r.Id == request.RepositoryId && r.TeamId == request.TeamId && r.DeletedDate == null, cancellationToken);
    private static bool CanRead(Repository? repo, Guid teamId) => repo?.Credential is { Status: CredentialStatus.Active, DeletedDate: null } credential && credential.TeamId == teamId && credential.ProviderInstanceId == repo.ProviderInstanceId && repo.ProviderInstance.TeamId == teamId && repo.ProviderInstance.DeletedDate is null;
    private IRepositorySourceCapability Resolve(Repository repo)
    {
        _scopeChecker.EnsureCapability(repo.Credential!, repo.ProviderInstance.Provider, typeof(IRepositorySourceCapability));
        return _providers.Require<IRepositorySourceCapability>(repo.ProviderInstance.Provider);
    }
    private static TaskSpecEvidenceContext Unavailable(TaskSpecEvidenceRequest request, string detail) => TaskSpecEvidenceContext.TaskOnly(request, TaskSpecRepositoryState.Unavailable, detail);
    internal static bool IsRelativeFilePath(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 1024 && !path.StartsWith('/') && !path.Contains('\\') && !path.Any(char.IsControl) && path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");
}
