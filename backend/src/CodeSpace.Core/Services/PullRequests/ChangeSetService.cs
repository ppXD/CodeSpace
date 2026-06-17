using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Exceptions;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.PullRequests;

/// <summary>
/// Opens one PR per repo in a Change Set, isolating each repo's failure. Each open delegates to
/// <see cref="IPullRequestService.OpenPullRequestAsync"/>, which does ALL per-repo resolution (team-scoped fail-closed
/// repo load, credential bind, capability + role check, optional act-as-user). This service adds only the loop, the
/// no-source-branch skip, and the per-repo failure → disposition policy.
/// </summary>
public sealed class ChangeSetService : IChangeSetService, IScopedDependency
{
    private readonly IPullRequestService _pullRequests;
    private readonly ILogger<ChangeSetService> _logger;

    public ChangeSetService(IPullRequestService pullRequests, ILogger<ChangeSetService> logger)
    {
        _pullRequests = pullRequests;
        _logger = logger;
    }

    public async Task<ChangeSetResult> OpenPullRequestsAsync(Guid teamId, ChangeSetPullRequestSpec spec, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var outcomes = new List<ChangeSetPullRequestOutcome>(spec.Repositories.Count);

        foreach (var repo in spec.Repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcomes.Add(await OpenOneAsync(teamId, repo, spec, actorUserId, cancellationToken).ConfigureAwait(false));
        }

        return new ChangeSetResult
        {
            PullRequests = outcomes,
            OpenedCount = outcomes.Count(o => o.Disposition == ChangeSetPullRequestDisposition.Opened),
            SkippedCount = outcomes.Count(o => o.Disposition == ChangeSetPullRequestDisposition.Skipped),
            FailedCount = outcomes.Count(o => o.Disposition == ChangeSetPullRequestDisposition.Failed),
        };
    }

    /// <summary>Open one repo's PR, mapping a no-source-branch repo to Skipped and a typed provider rejection to Failed (the rest of the set is unaffected). A non-provider exception (e.g. cancellation) still propagates.</summary>
    private async Task<ChangeSetPullRequestOutcome> OpenOneAsync(Guid teamId, ChangeSetPullRequest repo, ChangeSetPullRequestSpec spec, Guid? actorUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repo.SourceBranch))
            return new ChangeSetPullRequestOutcome { RepositoryId = repo.RepositoryId, Disposition = ChangeSetPullRequestDisposition.Skipped, Error = "no source branch — the repository produced no changes" };

        var input = new OpenPullRequestInput { Title = spec.Title, SourceBranch = repo.SourceBranch, TargetBranch = repo.TargetBranch, Body = spec.Body, Draft = spec.Draft };

        try
        {
            var pr = await _pullRequests.OpenPullRequestAsync(repo.RepositoryId, teamId, input, actorUserId, cancellationToken).ConfigureAwait(false);

            return new ChangeSetPullRequestOutcome { RepositoryId = repo.RepositoryId, Disposition = ChangeSetPullRequestDisposition.Opened, Number = pr.Number, Url = pr.WebUrl, State = pr.State.ToString() };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ProviderInsufficientScopeException or ProviderApiException)
        {
            _logger.LogWarning(ex, "Change set: failed to open PR for repo {RepoId}; the rest of the set is unaffected", repo.RepositoryId);

            return new ChangeSetPullRequestOutcome { RepositoryId = repo.RepositoryId, Disposition = ChangeSetPullRequestDisposition.Failed, Error = DescribeFailure(ex) };
        }
    }

    /// <summary>A concise, redacted per-repo failure reason (a richer remediation lives on the single-PR node; here it's one line in a Failed disposition).</summary>
    private static string DescribeFailure(Exception ex) => ex switch
    {
        ProviderInsufficientScopeException scope => $"{scope.ProviderKind} token is missing the {string.Join(", ", scope.MissingScopes)} scope.",
        ProviderApiException { StatusCode: 403 } api => $"{api.ProviderKind} refused it — the identity lacks write permission on this repository.",
        ProviderApiException { StatusCode: 404 } api => $"{api.ProviderKind} couldn't find the repository or a branch.",
        ProviderApiException { StatusCode: 422 } api => $"{api.ProviderKind} rejected it — the branches may be identical or a PR may already exist for them.",
        ProviderApiException api => $"{api.ProviderKind} returned HTTP {api.StatusCode}.",
        _ => ex.Message,
    };
}
