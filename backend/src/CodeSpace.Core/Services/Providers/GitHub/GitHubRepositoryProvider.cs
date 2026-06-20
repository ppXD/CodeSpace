using System.Runtime.CompilerServices;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Resilience;
using CodeSpace.Core.Services.Providers.Source;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Exceptions;
using Octokit;
using FileChangeStatus = CodeSpace.Messages.Enums.FileChangeStatus;
using PullRequestState = CodeSpace.Messages.Enums.PullRequestState;
using IssueState = CodeSpace.Messages.Enums.IssueState;
using PullRequestReviewVerdict = CodeSpace.Messages.Enums.PullRequestReviewVerdict;
using PullRequestCheckStatus = CodeSpace.Messages.Enums.PullRequestCheckStatus;
using RepositoryVisibility = CodeSpace.Messages.Enums.RepositoryVisibility;
using ProviderKind = CodeSpace.Messages.Enums.ProviderKind;
using RepositoryRole = CodeSpace.Messages.Enums.RepositoryRole;

namespace CodeSpace.Core.Services.Providers.GitHub;

public sealed class GitHubRepositoryProvider : IRepositoryCatalogCapability, IPullRequestCatalogCapability, IPullRequestCommentCapability, IPullRequestReviewCapability, IPullRequestWriteCapability, IIssueCatalogCapability, IIssueWriteCapability, IReleaseCatalogCapability, IRepositoryAccessCapability, IRepositorySourceCapability, IRepositoryInsightsCapability, IRepositoryHistoryCapability, IRepositoryMarkdownRenderCapability, ICredentialProbeCapability, IWebhookRegistrationCapability, IWebhookSignatureVerifier, IWebhookEventNormalizer
{
    private readonly IProviderAuthResolver _authResolver;
    private readonly IExternalCallResilience _resilience;
    private readonly GitHubSignatureVerifier _signatureVerifier;
    private readonly GitHubEventNormalizer _eventNormalizer;

    public GitHubRepositoryProvider(IProviderAuthResolver authResolver, IExternalCallResilience resilience, GitHubSignatureVerifier signatureVerifier, GitHubEventNormalizer eventNormalizer)
    {
        _authResolver = authResolver;
        _resilience = resilience;
        _signatureVerifier = signatureVerifier;
        _eventNormalizer = eventNormalizer;
    }

    public ProviderKind Kind => ProviderKind.GitHub;

    public async Task<RemoteRepository> GetByExternalIdAsync(ProviderContext context, string externalId, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(GetByExternalIdAsync), async _ =>
        {
            var repo = await client.Repository.Get(long.Parse(externalId)).ConfigureAwait(false);
            return ToRemoteRepository(repo);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteRepository> ResolveByPathAsync(ProviderContext context, string namespacePath, string name, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(ResolveByPathAsync), async _ =>
        {
            var repo = await client.Repository.Get(namespacePath, name).ConfigureAwait(false);
            return ToRemoteRepository(repo);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteRepositoryPage> PagedListAccessibleAsync(ProviderContext context, string? search, int page, int perPage, CancellationToken cancellationToken)
    {
        // The `search` parameter is intentionally ignored on GitHub. There is NO GitHub API
        // surface that combines full visibility (own + collaborator + organization-member)
        // with name search:
        //   - REST /user/repos has the visibility but no `q`/`search`/`name` query param.
        //   - REST /search/repositories has search but no `affiliation:` / `collaborator:`
        //     qualifier — `user:LOGIN` and explicit `org:NAME` are the only ownership
        //     qualifiers, and boolean operators (OR) don't compose them in practice.
        //   - GraphQL viewer.repositories(affiliations: [...]) matches /user/repos visibility
        //     but the `repositories` connection has no `query`/`search` argument (unlike the
        //     `projects` connection on ProjectOwner).
        //
        // Frontends that need search must fetch the full paginated list and filter in memory.
        // The SPA's `useAllAccessibleRepositories` hook does exactly that — eager-fetches all
        // pages once per credential, then filters/paginates client-side. Splitting the result
        // between /user/repos and /search/repositories would silently hide org-member repos
        // from the search result, which is what the prior implementation got bitten by.
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        _ = search;

        return await _resilience.ExecuteAsync(context.Instance, nameof(PagedListAccessibleAsync), async _ =>
        {
            var options = new ApiOptions { PageCount = 1, PageSize = perPage, StartPage = page };
            var repos = await client.Repository.GetAllForCurrent(options).ConfigureAwait(false);

            return new RemoteRepositoryPage
            {
                Items = repos.Select(ToRemoteRepository).ToList(),
                // /user/repos doesn't return a count in the response body. Getting one would
                // require parsing the Link header for the last-page URL — Octokit's typed
                // overload doesn't expose response headers, so we leave it null and the SPA
                // degrades to the open-ended pager.
                TotalCount = null
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemotePullRequest>> ListPullRequestsAsync(ProviderContext context, RemoteRepository repository, PullRequestState? stateFilter, int page, int perPage, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(ListPullRequestsAsync), async _ =>
        {
            var request = new PullRequestRequest
            {
                State = MapStateFilterToItemState(stateFilter),
                SortProperty = PullRequestSort.Updated,
                SortDirection = SortDirection.Descending
            };

            // ApiOptions = "give me exactly one page" — without this, Octokit
            // auto-paginates and silently fetches every page (500+ closed PRs
            // becomes 18+ sequential API calls, ~30s wall-clock, what triggered
            // the slowness report).
            var options = new ApiOptions { PageCount = 1, PageSize = perPage, StartPage = page };

            // Use the documented `/repos/{owner}/{repo}/pulls` route — the by-ID
            // overload (`/repositories/{id}/pulls`) is undocumented and returns 404
            // for some org-owned + OAuth-app combinations even when the credential
            // can read the repo via the path-based route.
            var prs = await client.PullRequest.GetAllForRepository(repository.NamespacePath, repository.Name, request, options).ConfigureAwait(false);

            return (IReadOnlyList<RemotePullRequest>)prs.Select(ToRemotePullRequest).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemotePullRequest> GetPullRequestAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(GetPullRequestAsync), async _ =>
        {
            // Same rationale as ListPullRequestsAsync — use the documented owner/name route,
            // not the by-ID variant. Single-PR Get populates Body + diff stats that the list
            // endpoint deliberately omits.
            var pr = await client.PullRequest.Get(repository.NamespacePath, repository.Name, number).ConfigureAwait(false);
            return ToRemotePullRequestDetail(pr);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemotePullRequestCommit>> ListPullRequestCommitsAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(ListPullRequestCommitsAsync), async _ =>
        {
            var commits = await client.PullRequest.Commits(repository.NamespacePath, repository.Name, number).ConfigureAwait(false);
            return (IReadOnlyList<RemotePullRequestCommit>)commits.Select(ToRemotePullRequestCommit).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemotePullRequestFile>> ListPullRequestFilesAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(ListPullRequestFilesAsync), async _ =>
        {
            var files = await client.PullRequest.Files(repository.NamespacePath, repository.Name, number).ConfigureAwait(false);
            return (IReadOnlyList<RemotePullRequestFile>)files.Select(ToRemotePullRequestFile).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemotePullRequestCounts> CountPullRequestsAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(CountPullRequestsAsync), async _ =>
        {
            // GitHub doesn't expose a "count PRs by state" REST endpoint; the canonical
            // way (used by the GitHub UI itself) is the Search API filtered by repo +
            // is:pr + state. PerPage=1 keeps the response body tiny — we only read
            // TotalCount. Two parallel calls cost ~one round-trip wall-clock.
            var openRequest = BuildCountSearch(repository, ItemState.Open);
            var closedRequest = BuildCountSearch(repository, ItemState.Closed);

            var openTask = client.Search.SearchIssues(openRequest);
            var closedTask = client.Search.SearchIssues(closedRequest);

            await Task.WhenAll(openTask, closedTask).ConfigureAwait(false);

            // DIAGNOSTIC: log the Search API responses so we can see whether GitHub returned
            // real numbers vs. silently filtered to 0. IncompleteResults=true on a Search
            // response is GitHub's signal that the search timed out before counting fully.
            Serilog.Log.Information(
                "[GitHubRepoProvider] CountPullRequestsAsync result for {Owner}/{Name}: openTotal={Open} (incomplete={OpenIncomplete}) closedTotal={Closed} (incomplete={ClosedIncomplete})",
                repository.NamespacePath, repository.Name,
                openTask.Result.TotalCount, openTask.Result.IncompleteResults,
                closedTask.Result.TotalCount, closedTask.Result.IncompleteResults);

            return new RemotePullRequestCounts
            {
                Open = openTask.Result.TotalCount,
                Closed = closedTask.Result.TotalCount
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(ListChecksAsync), async _ =>
        {
            // Need the PR's head SHA to call Check.Run.GetAllForReference. We fetch the
            // PR once — cheap, the response is small. Alternative would be to have the
            // caller pass the SHA in, but that leaks Octokit detail into the service layer.
            var pr = await client.PullRequest.Get(repository.NamespacePath, repository.Name, number).ConfigureAwait(false);
            var headSha = pr.Head?.Sha;
            if (string.IsNullOrEmpty(headSha)) return (IReadOnlyList<RemotePullRequestCheck>)Array.Empty<RemotePullRequestCheck>();

            try
            {
                var response = await client.Check.Run.GetAllForReference(repository.NamespacePath, repository.Name, headSha).ConfigureAwait(false);
                return (IReadOnlyList<RemotePullRequestCheck>)response.CheckRuns.Select(ToRemoteCheck).ToList();
            }
            catch (AuthorizationException)
            {
                // Token lacks `repo` or Checks: Read — graceful empty rather than failing
                // the whole PR detail view.
                return (IReadOnlyList<RemotePullRequestCheck>)Array.Empty<RemotePullRequestCheck>();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemotePullRequestComment> PostCommentAsync(ProviderContext context, RemoteRepository repository, int number, string body, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(PostCommentAsync), async _ =>
        {
            // PR comments on GitHub are issue comments under the hood (the conversation tab
            // shares its identity with the PR's issue). Issue.Comment.Create works for both
            // PR and Issue numbers transparently.
            var created = await client.Issue.Comment.Create(repository.NamespacePath, repository.Name, number, body).ConfigureAwait(false);

            return new RemotePullRequestComment
            {
                ExternalId = created.Id.ToString(),
                Body = created.Body,
                AuthorName = created.User?.Login ?? "unknown",
                CreatedAt = created.CreatedAt,
                WebUrl = created.HtmlUrl
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemotePullRequestReview> SubmitReviewAsync(ProviderContext context, RemoteRepository repository, int number, PullRequestReviewVerdict verdict, string? body, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(SubmitReviewAsync), async _ =>
        {
            // GitHub has a native review verdict — one call submits approve / request-changes / comment.
            var review = new PullRequestReviewCreate { Body = body ?? "", Event = GitHubReviewMapping.ToEvent(verdict) };
            var created = await client.PullRequest.Review.Create(repository.NamespacePath, repository.Name, number, review).ConfigureAwait(false);

            return new RemotePullRequestReview
            {
                Verdict = verdict,
                ExternalId = created.Id.ToString(),
                WebUrl = created.HtmlUrl
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemotePullRequest> OpenPullRequestAsync(ProviderContext context, RemoteRepository repository, OpenPullRequestInput input, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(OpenPullRequestAsync), async _ =>
        {
            // GitHub: head = source branch, base = target branch. Draft is honoured when the repo plan allows it.
            var newPr = new NewPullRequest(input.Title, input.SourceBranch, input.TargetBranch) { Body = input.Body, Draft = input.Draft };
            var created = await client.PullRequest.Create(repository.NamespacePath, repository.Name, newPr).ConfigureAwait(false);

            return ToRemotePullRequestDetail(created);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemotePullRequestMergeResult> MergePullRequestAsync(ProviderContext context, RemoteRepository repository, int number, MergePullRequestInput input, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(MergePullRequestAsync), async _ =>
        {
            var merge = new MergePullRequest
            {
                MergeMethod = input.Method switch
                {
                    CodeSpace.Messages.Dtos.Providers.PullRequestMergeMethod.Squash => Octokit.PullRequestMergeMethod.Squash,
                    CodeSpace.Messages.Dtos.Providers.PullRequestMergeMethod.Rebase => Octokit.PullRequestMergeMethod.Rebase,
                    _ => Octokit.PullRequestMergeMethod.Merge,
                },
                CommitTitle = input.CommitTitle,
                CommitMessage = input.CommitMessage,
            };

            var result = await client.PullRequest.Merge(repository.NamespacePath, repository.Name, number, merge).ConfigureAwait(false);

            // GitHub doesn't delete the head branch on merge — do it as a follow-up when asked. Need the PR's
            // head ref, so fetch it; a delete failure (already gone / protected) is swallowed so it never fails
            // an otherwise-successful merge.
            if (input.DeleteSourceBranch && result.Merged)
            {
                var pr = await client.PullRequest.Get(repository.NamespacePath, repository.Name, number).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(pr.Head?.Ref))
                    try { await client.Git.Reference.Delete(repository.NamespacePath, repository.Name, $"heads/{pr.Head.Ref}").ConfigureAwait(false); }
                    catch (ApiException) { /* branch already deleted / protected — the merge still succeeded */ }
            }

            return new RemotePullRequestMergeResult { Merged = result.Merged, Sha = result.Sha, Message = result.Message };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteIssue>> ListIssuesAsync(ProviderContext context, RemoteRepository repository, IssueState? stateFilter, int page, int perPage, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(ListIssuesAsync), async _ =>
        {
            // Use the Search API (`is:issue`) — NOT the REST /issues list. GitHub's REST issues endpoint
            // returns pull requests as issues too; filtering them client-side would leave variable-density
            // pages and break count-based pagination (some issues become unreachable). Search excludes PRs
            // server-side, so page N holds exactly the Nth page of real issues — and its TotalCount is the
            // same number CountIssuesAsync reports, keeping the pager honest.
            var request = new SearchIssuesRequest
            {
                Repos = SingleRepoCollection(repository),
                Type = IssueTypeQualifier.Issue,
                SortField = IssueSearchSort.Created,
                Order = SortDirection.Descending,
                Page = page,
                PerPage = perPage
            };
            if (stateFilter is { } s) request.State = s == IssueState.Closed ? ItemState.Closed : ItemState.Open;

            var result = await client.Search.SearchIssues(request).ConfigureAwait(false);
            return (IReadOnlyList<RemoteIssue>)result.Items.Select(ToRemoteIssue).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteIssueCounts> CountIssuesAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(CountIssuesAsync), async _ =>
        {
            // Same Search-API approach as CountPullRequestsAsync, but Type=Issue so PRs are excluded
            // from the totals. PerPage=1 keeps the body tiny — we only read TotalCount.
            var openTask = client.Search.SearchIssues(BuildIssueCountSearch(repository, ItemState.Open));
            var closedTask = client.Search.SearchIssues(BuildIssueCountSearch(repository, ItemState.Closed));

            await Task.WhenAll(openTask, closedTask).ConfigureAwait(false);

            return new RemoteIssueCounts
            {
                Open = openTask.Result.TotalCount,
                Closed = closedTask.Result.TotalCount
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    private static SearchIssuesRequest BuildIssueCountSearch(RemoteRepository repository, ItemState state) => new()
    {
        Repos = SingleRepoCollection(repository),
        Type = IssueTypeQualifier.Issue,
        State = state,
        PerPage = 1
    };

    private static RepositoryCollection SingleRepoCollection(RemoteRepository repository)
    {
        // RepositoryCollection has only a default ctor + Add(owner, name) — the string-init shape isn't
        // supported, so build it imperatively. Produces the `repo:owner/name` search qualifier.
        var repos = new RepositoryCollection();
        repos.Add(repository.NamespacePath, repository.Name);
        return repos;
    }

    public async Task<RemoteIssue> CreateIssueAsync(ProviderContext context, RemoteRepository repository, CreateIssueInput input, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(CreateIssueAsync), async _ =>
        {
            var newIssue = new NewIssue(input.Title) { Body = input.Body };
            foreach (var label in input.Labels) newIssue.Labels.Add(label);

            var created = await client.Issue.Create(repository.NamespacePath, repository.Name, newIssue).ConfigureAwait(false);
            return ToRemoteIssue(created);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static RemoteIssue ToRemoteIssue(Issue issue) => new()
    {
        ExternalId = issue.Id.ToString(),
        Number = issue.Number,
        Title = issue.Title,
        State = issue.State.Value == ItemState.Closed ? IssueState.Closed : IssueState.Open,
        Body = issue.Body,
        AuthorLogin = issue.User?.Login,
        Labels = ToLabelRefs(issue.Labels),
        Assignees = issue.Assignees?.Select(a => a.Login).ToList() ?? new List<string>(),
        CommentsCount = issue.Comments,
        MilestoneTitle = issue.Milestone?.Title,
        CreatedDate = issue.CreatedAt,
        ClosedDate = issue.ClosedAt,
        WebUrl = issue.HtmlUrl
    };

    public async Task<RemoteIssue> GetIssueAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(GetIssueAsync), async _ =>
        {
            var issue = await client.Issue.Get(repository.NamespacePath, repository.Name, number).ConfigureAwait(false);
            return ToRemoteIssue(issue);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteIssueComment>> ListIssueCommentsAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(ListIssueCommentsAsync), async _ =>
        {
            var comments = await client.Issue.Comment.GetAllForIssue(repository.NamespacePath, repository.Name, number).ConfigureAwait(false);
            return (IReadOnlyList<RemoteIssueComment>)comments.Select(c => new RemoteIssueComment
            {
                ExternalId = c.Id.ToString(),
                Body = c.Body,
                AuthorName = c.User?.Login ?? "unknown",
                CreatedAt = c.CreatedAt,
                WebUrl = c.HtmlUrl
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteIssueEvent>> ListIssueEventsAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(ListIssueEventsAsync), async _ =>
        {
            try
            {
                var events = await client.Issue.Events.GetAllForIssue(repository.NamespacePath, repository.Name, number).ConfigureAwait(false);
                // Only the events that carry meaning for the activity timeline; unmapped ones (subscribed,
                // pinned, …) drop to null and are filtered out.
                return (IReadOnlyList<RemoteIssueEvent>)events.Select(ToRemoteIssueEvent).OfType<RemoteIssueEvent>().ToList();
            }
            catch (AuthorizationException)
            {
                return (IReadOnlyList<RemoteIssueEvent>)Array.Empty<RemoteIssueEvent>();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static RemoteIssueEvent? ToRemoteIssueEvent(IssueEvent e)
    {
        // Synthesise GitHub's structured events into the same human lines GitLab's system notes already
        // carry, so both providers render identically. Unmapped events return null (filtered by the caller).
        var (kind, summary) = e.Event.Value switch
        {
            EventInfoState.Assigned => ("assigned", $"assigned to {e.Assignee?.Login ?? "someone"}"),
            EventInfoState.Unassigned => ("unassigned", $"unassigned {e.Assignee?.Login ?? "someone"}"),
            EventInfoState.Labeled => ("labeled", $"added the {e.Label?.Name} label"),
            EventInfoState.Unlabeled => ("unlabeled", $"removed the {e.Label?.Name} label"),
            EventInfoState.Milestoned => ("milestoned", $"added this to the {e.Milestone?.Title} milestone"),
            EventInfoState.Demilestoned => ("demilestoned", $"removed this from the {e.Milestone?.Title} milestone"),
            EventInfoState.Closed => ("closed", "closed this"),
            EventInfoState.Reopened => ("reopened", "reopened this"),
            EventInfoState.Renamed => ("renamed", "renamed this"),
            EventInfoState.Referenced => ("referenced", "referenced this in a commit"),
            EventInfoState.Crossreferenced => ("mentioned", "mentioned this elsewhere"),
            EventInfoState.Mentioned => ("mentioned", "was mentioned"),
            _ => (null, null)
        };
        if (kind == null) return null;

        return new RemoteIssueEvent
        {
            ExternalId = e.Id.ToString(),
            Kind = kind,
            Summary = summary!,
            ActorLogin = e.Actor?.Login,
            CreatedDate = e.CreatedAt
        };
    }

    public async Task<RemoteIssueComment> CommentIssueAsync(ProviderContext context, RemoteRepository repository, int number, string body, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(CommentIssueAsync), async _ =>
        {
            // GitHub issue comments and PR comments share one API (Issue.Comment) — an issue number works here.
            var created = await client.Issue.Comment.Create(repository.NamespacePath, repository.Name, number, body).ConfigureAwait(false);

            return new RemoteIssueComment
            {
                ExternalId = created.Id.ToString(),
                Body = created.Body,
                AuthorName = created.User?.Login ?? "unknown",
                CreatedAt = created.CreatedAt,
                WebUrl = created.HtmlUrl
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteIssue> CloseIssueAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(CloseIssueAsync), async _ =>
        {
            var updated = await client.Issue.Update(repository.NamespacePath, repository.Name, number, new IssueUpdate { State = ItemState.Closed }).ConfigureAwait(false);
            return ToRemoteIssue(updated);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static RemotePullRequestCheck ToRemoteCheck(CheckRun run)
    {
        var status = MapCheckRunStatus(run.Status.Value, run.Conclusion?.Value);

        // Octokit exposes StartedAt as a non-nullable DateTimeOffset — default(DateTimeOffset)
        // is the only signal Octokit gives us that the field was absent on the wire. Treat
        // that as "no start timestamp" rather than rendering "1/1/0001" in the UI.
        var startedAt = run.StartedAt == default ? (DateTimeOffset?)null : run.StartedAt;
        var completedAt = run.CompletedAt;

        int? duration = null;
        if (startedAt.HasValue && completedAt.HasValue)
        {
            duration = (int)(completedAt.Value - startedAt.Value).TotalSeconds;
        }

        return new RemotePullRequestCheck
        {
            Name = run.Name,
            Status = status,
            Conclusion = run.Conclusion?.Value.ToString().ToLowerInvariant(),
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationSeconds = duration,
            DetailsUrl = run.DetailsUrl
        };
    }

    // GitHub: status = queued|in_progress|completed; conclusion = success|failure|neutral|cancelled|skipped|timed_out|action_required|stale
    // (only present when status = completed). Map both into the normalised 6-bucket enum.
    private static PullRequestCheckStatus MapCheckRunStatus(CheckStatus status, CheckConclusion? conclusion)
    {
        if (status != CheckStatus.Completed) return PullRequestCheckStatus.Pending;
        return conclusion switch
        {
            CheckConclusion.Success => PullRequestCheckStatus.Success,
            CheckConclusion.Failure or CheckConclusion.TimedOut => PullRequestCheckStatus.Failure,
            CheckConclusion.Cancelled => PullRequestCheckStatus.Cancelled,
            CheckConclusion.Skipped => PullRequestCheckStatus.Skipped,
            CheckConclusion.Neutral or CheckConclusion.ActionRequired => PullRequestCheckStatus.Neutral,
            // Stale + anything Octokit adds later — treat as Neutral so we never crash on an
            // unrecognised conclusion. The raw Conclusion field still carries the detail.
            _ => PullRequestCheckStatus.Neutral
        };
    }

    private static SearchIssuesRequest BuildCountSearch(RemoteRepository repository, ItemState state)
    {
        // RepositoryCollection has only a default ctor + Add(owner, name); the
        // string-init shape isn't supported. Octokit fluent properties + PerPage=1
        // make the q= param the search uses (`repo:owner/name is:pr is:open` etc.)
        // and keep the response body tiny — we only care about TotalCount.
        var repos = new RepositoryCollection();
        repos.Add(repository.NamespacePath, repository.Name);

        return new SearchIssuesRequest
        {
            Repos = repos,
            Type = IssueTypeQualifier.PullRequest,
            State = state,
            PerPage = 1
        };
    }

    public async Task<CredentialProbeResult> ProbeCredentialAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        try
        {
            var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

            return await _resilience.ExecuteAsync(context.Instance, nameof(ProbeCredentialAsync), async _ =>
            {
                var user = await client.User.Current().ConfigureAwait(false);

                // Octokit parses the X-OAuth-Scopes response header into ApiInfo.OauthScopes — the exact
                // scopes a classic PAT / OAuth token carries. Fine-grained PATs and GitHub App tokens
                // don't send the header (empty list) → null, which the capability check treats as
                // "unknown" (no false warnings) rather than "zero scopes".
                var oauthScopes = client.GetLastApiInfo()?.OauthScopes;
                var scopes = oauthScopes is { Count: > 0 } ? oauthScopes.ToList() : null;

                return new CredentialProbeResult
                {
                    IsValid = true,
                    AuthenticatedUserExternalId = user.Id.ToString(),
                    AuthenticatedUserName = user.Login,
                    GrantedScopes = scopes
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new CredentialProbeResult { IsValid = false, Error = ex.Message };
        }
    }

    public async Task<RepositoryActorAccess> GetActorAccessAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken)
    {
        // MEMBERSHIP / access only: report the actor's effective ROLE on this repo. Whether that role is
        // ENOUGH is decided by the gate against the node's declared capability — not here. The token's
        // SCOPE (repo/public_repo) is a separate axis the gate checks too.
        try
        {
            var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

            // A private repo the actor isn't a collaborator on returns 404 → caught below as "no access".
            var repo = await _resilience.ExecuteAsync(context.Instance, nameof(GetActorAccessAsync), async _ =>
                await client.Repository.Get(long.Parse(repository.ExternalId)).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

            // Get succeeding means the actor can at least SEE the repo → Read floor even if GitHub omitted the
            // permissions block; otherwise map the highest granted permission onto the neutral ladder.
            var p = repo.Permissions;
            var role = p == null ? RepositoryRole.Read : MapPermissions(p.Admin, p.Maintain, p.Push, p.Triage, p.Pull);

            return RepositoryActorAccess.Of(role);
        }
        catch (ProviderApiException ex) when (ex.StatusCode is 403 or 404)
        {
            return RepositoryActorAccess.Of(RepositoryRole.None);
        }
        catch
        {
            // Inconclusive (network blip / transient / a scope gap on the read) — never block a
            // legitimate click on a flaky probe. The write path stays the backstop.
            return RepositoryActorAccess.Inconclusive;
        }
    }

    /// <summary>
    /// Maps GitHub's repository permission flags to the neutral <see cref="RepositoryRole"/> ladder,
    /// highest-granted first (admin → Admin … pull → Read). All false → None.
    /// </summary>
    internal static RepositoryRole MapPermissions(bool admin, bool maintain, bool push, bool triage, bool pull) =>
        admin ? RepositoryRole.Admin
        : maintain ? RepositoryRole.Maintain
        : push ? RepositoryRole.Write
        : triage ? RepositoryRole.Triage
        : pull ? RepositoryRole.Read
        : RepositoryRole.None;

    public async Task<RemoteWebhook?> FindWebhookByCallbackUrlAsync(ProviderContext context, RemoteRepository repository, string callbackUrl, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(FindWebhookByCallbackUrlAsync), async _ =>
        {
            // GET /repos/:owner/:repo/hooks — Octokit fetches all hook configs via the
            // owner+name route. The hook's `config["url"]` carries the callback we set
            // at register time, so a case-insensitive exact match against our generated
            // callback URL identifies a prior registration uniquely.
            var hooks = await client.Repository.Hooks.GetAll(long.Parse(repository.ExternalId)).ConfigureAwait(false);

            foreach (var hook in hooks)
            {
                if (hook.Config != null && hook.Config.TryGetValue("url", out var url) && string.Equals(url, callbackUrl, StringComparison.OrdinalIgnoreCase))
                {
                    return new RemoteWebhook
                    {
                        ExternalId = hook.Id.ToString(),
                        CallbackUrl = url,
                        SubscribedEvents = hook.Events.ToList(),
                        Active = hook.Active
                    };
                }
            }

            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteWebhook> RegisterWebhookAsync(ProviderContext context, RemoteRepository repository, WebhookRegistration request, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(RegisterWebhookAsync), async _ =>
        {
            var config = new Dictionary<string, string>
            {
                ["url"] = request.CallbackUrl,
                ["content_type"] = "json",
                ["secret"] = request.Secret
            };

            var newHook = new NewRepositoryHook("web", config)
            {
                Active = true,
                Events = request.SubscribedEvents.ToArray()
            };

            var created = await client.Repository.Hooks.Create(long.Parse(repository.ExternalId), newHook).ConfigureAwait(false);

            return new RemoteWebhook
            {
                ExternalId = created.Id.ToString(),
                CallbackUrl = request.CallbackUrl,
                SubscribedEvents = created.Events.ToList(),
                Active = created.Active
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteWebhookAsync(ProviderContext context, RemoteRepository repository, string externalWebhookId, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        await _resilience.ExecuteAsync(context.Instance, nameof(DeleteWebhookAsync),
            _ => client.Repository.Hooks.Delete(long.Parse(repository.ExternalId), int.Parse(externalWebhookId)),
            cancellationToken).ConfigureAwait(false);
    }

    public bool VerifySignature(string body, IReadOnlyDictionary<string, string> headers, string secret) => _signatureVerifier.Verify(body, headers, secret);

    public NormalizedEvent? Normalize(Guid repositoryId, string body, IReadOnlyDictionary<string, string> headers) => _eventNormalizer.Normalize(repositoryId, body, headers);

    // ── Repository source (Code browser): branches, one tree level, single file ──

    public async Task<IReadOnlyList<RemoteBranch>> ListBranchesAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(ListBranchesAsync), async _ =>
        {
            var branches = await client.Repository.Branch.GetAll(repository.NamespacePath, repository.Name).ConfigureAwait(false);
            return (IReadOnlyList<RemoteBranch>)branches.Select(b => ToRemoteBranch(b, repository.DefaultBranch)).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteTreeEntry>> ListTreeAsync(ProviderContext context, RemoteRepository repository, string? path, string? reference, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        var gitRef = string.IsNullOrWhiteSpace(reference) ? repository.DefaultBranch : reference;
        var folder = (path ?? string.Empty).Trim('/');

        return await _resilience.ExecuteAsync(context.Instance, nameof(ListTreeAsync), async _ =>
        {
            // GetAllContentsByRef lists one directory level (non-recursive) — the browser drills in lazily.
            var contents = string.IsNullOrEmpty(folder)
                ? await client.Repository.Content.GetAllContentsByRef(repository.NamespacePath, repository.Name, gitRef).ConfigureAwait(false)
                : await client.Repository.Content.GetAllContentsByRef(repository.NamespacePath, repository.Name, folder, gitRef).ConfigureAwait(false);
            return (IReadOnlyList<RemoteTreeEntry>)contents.Select(ToRemoteTreeEntry).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteFileContent> GetFileAsync(ProviderContext context, RemoteRepository repository, string path, string? reference, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        var gitRef = string.IsNullOrWhiteSpace(reference) ? repository.DefaultBranch : reference;
        var filePath = path.Trim('/');

        return await _resilience.ExecuteAsync(context.Instance, nameof(GetFileAsync), async _ =>
        {
            // On a file path this returns a single entry whose Path equals the request; on a directory it
            // returns the directory's children (different paths) — reject that as "not a file".
            var contents = await client.Repository.Content.GetAllContentsByRef(repository.NamespacePath, repository.Name, filePath, gitRef).ConfigureAwait(false);
            var file = contents.Count == 1 ? contents[0] : null;

            if (file == null || !string.Equals(file.Path, filePath, StringComparison.Ordinal))
                throw new InvalidOperationException($"'{filePath}' is not a file on {gitRef}.");

            var name = string.IsNullOrEmpty(file.Name) ? filePath : file.Name;
            return FileContentDecoder.Build(filePath, name, DecodeContent(file), file.Sha, file.Size);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Base64 → bytes. Null when GitHub omitted the content (files above its ~1 MB inline cap) so the decoder marks it truncated.</summary>
    private static byte[]? DecodeContent(RepositoryContent file) =>
        string.IsNullOrEmpty(file.EncodedContent) ? null : Convert.FromBase64String(file.EncodedContent);

    private static RemoteBranch ToRemoteBranch(Branch branch, string defaultBranch) => new()
    {
        Name = branch.Name,
        CommitSha = branch.Commit?.Sha,
        IsDefault = string.Equals(branch.Name, defaultBranch, StringComparison.Ordinal),
        Protected = branch.Protected
    };

    private static RemoteTreeEntry ToRemoteTreeEntry(RepositoryContent content) => new()
    {
        Name = content.Name,
        Path = content.Path,
        Type = RepositoryTreeEntryTypeMap.From(content.Type.StringValue),
        Size = content.Size,
        Sha = content.Sha
    };

    // ── Repository insights (Code tab right rail): stats + languages ──

    private static readonly HttpClient _statsHttpClient = new();

    public async Task<RemoteRepositoryStats> GetStatsAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);
        var auth = await _authResolver.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
        var apiBase = ResolveApiBaseAddress(context.Instance);
        var owner = repository.NamespacePath;
        var name = repository.Name;

        return await _resilience.ExecuteAsync(context.Instance, nameof(GetStatsAsync), async _ =>
        {
            var repo = await client.Repository.Get(owner, name).ConfigureAwait(false);

            // Counts come from the pagination Link header (per_page=1 → the rel="last" page number == total).
            // Every count is best-effort: a failure returns null and the panel just omits that row.
            var commits = await CountViaLinkAsync(auth.Token, apiBase, $"repos/{owner}/{name}/commits", $"sha={Uri.EscapeDataString(repository.DefaultBranch)}", cancellationToken).ConfigureAwait(false);
            var branches = await CountViaLinkAsync(auth.Token, apiBase, $"repos/{owner}/{name}/branches", null, cancellationToken).ConfigureAwait(false);
            var tags = await CountViaLinkAsync(auth.Token, apiBase, $"repos/{owner}/{name}/tags", null, cancellationToken).ConfigureAwait(false);
            var releases = await CountViaLinkAsync(auth.Token, apiBase, $"repos/{owner}/{name}/releases", null, cancellationToken).ConfigureAwait(false);

            return new RemoteRepositoryStats
            {
                Stars = repo.StargazersCount,
                Forks = repo.ForksCount,
                StorageBytes = repo.Size * 1024L,   // GitHub reports repo size in KB
                CommitCount = commits,
                BranchCount = branches,
                TagCount = tags,
                ReleaseCount = releases
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteRelease?> GetLatestReleaseAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(GetLatestReleaseAsync), async _ =>
        {
            try
            {
                // GetLatest = GitHub's "Latest"-badged release (newest published, non-draft, non-prerelease).
                // It 404s when the repo has no such release (none at all, or only drafts/prereleases) — which
                // we map to null so the card just shows the count + link rather than erroring the Code view.
                var release = await client.Repository.Release.GetLatest(repository.NamespacePath, repository.Name).ConfigureAwait(false);
                return ToRemoteRelease(release);
            }
            catch (NotFoundException)
            {
                return null;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static RemoteRelease ToRemoteRelease(Release release) => new()
    {
        TagName = release.TagName,
        Name = string.IsNullOrWhiteSpace(release.Name) ? null : release.Name,
        PublishedDate = release.PublishedAt ?? release.CreatedAt,
        WebUrl = release.HtmlUrl,
        IsPrerelease = release.Prerelease
    };

    // ── IReleaseCatalogCapability ──

    public async Task<IReadOnlyList<RemoteRelease>> ListReleasesAsync(ProviderContext context, RemoteRepository repository, int page, int perPage, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(ListReleasesAsync), async _ =>
        {
            var options = new ApiOptions { PageCount = 1, PageSize = perPage, StartPage = page };
            var releases = await client.Repository.Release.GetAll(repository.NamespacePath, repository.Name, options).ConfigureAwait(false);

            // The "Latest" badge marks ONE release (GitHub's GetLatest = newest published non-prerelease).
            // Only resolve it on page 1 where it can appear; skip the extra call on deeper pages.
            string? latestTag = null;
            if (page == 1)
            {
                try { latestTag = (await client.Repository.Release.GetLatest(repository.NamespacePath, repository.Name).ConfigureAwait(false)).TagName; }
                catch (NotFoundException) { /* no published-stable release → no badge */ }
            }

            return (IReadOnlyList<RemoteRelease>)releases.Where(r => !r.Draft).Select(r => ToRemoteReleaseDetail(r, r.TagName == latestTag)).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteTag>> ListTagsAsync(ProviderContext context, RemoteRepository repository, int page, int perPage, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(ListTagsAsync), async _ =>
        {
            var options = new ApiOptions { PageCount = 1, PageSize = perPage, StartPage = page };
            var tags = await client.Repository.GetAllTags(repository.NamespacePath, repository.Name, options).ConfigureAwait(false);

            // RepositoryTag carries only name + commit ref — no annotation message (that needs a second
            // git/tags fetch per tag, not worth it for the list). The tag page is /releases/tag/{name}.
            return (IReadOnlyList<RemoteTag>)tags.Select(t => new RemoteTag
            {
                Name = t.Name,
                CommitSha = t.Commit?.Sha ?? string.Empty,
                Message = null,
                WebUrl = $"{repository.WebUrl}/releases/tag/{Uri.EscapeDataString(t.Name)}"
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    private static RemoteRelease ToRemoteReleaseDetail(Release r, bool isLatest)
    {
        var assets = new List<RemoteReleaseAsset>();
        foreach (var a in r.Assets ?? (IReadOnlyList<ReleaseAsset>)Array.Empty<ReleaseAsset>())
            assets.Add(new RemoteReleaseAsset { Name = a.Name, DownloadUrl = a.BrowserDownloadUrl, SizeBytes = a.Size });

        // GitHub auto-generates the source archives — surfaced under Assets in its UI, so we mirror that.
        if (!string.IsNullOrEmpty(r.ZipballUrl)) assets.Add(new RemoteReleaseAsset { Name = "Source code (zip)", DownloadUrl = r.ZipballUrl, SizeBytes = null });
        if (!string.IsNullOrEmpty(r.TarballUrl)) assets.Add(new RemoteReleaseAsset { Name = "Source code (tar.gz)", DownloadUrl = r.TarballUrl, SizeBytes = null });

        return new RemoteRelease
        {
            TagName = r.TagName,
            Name = string.IsNullOrWhiteSpace(r.Name) ? null : r.Name,
            Body = string.IsNullOrWhiteSpace(r.Body) ? null : r.Body,
            AuthorLogin = r.Author?.Login,
            PublishedDate = r.PublishedAt ?? r.CreatedAt,
            WebUrl = r.HtmlUrl,
            IsPrerelease = r.Prerelease,
            IsLatest = isLatest,
            Assets = assets
        };
    }

    public async Task<IReadOnlyList<RemoteLanguage>> GetLanguagesAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(GetLanguagesAsync), async _ =>
        {
            var languages = await client.Repository.GetAllLanguages(repository.NamespacePath, repository.Name).ConfigureAwait(false);
            return LanguageBreakdown.FromBytes(languages.ToDictionary(l => l.Name, l => l.NumberOfBytes));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Total item count for a paginated GitHub list resource via the <c>Link</c> header: request one item
    /// and read the <c>rel="last"</c> page number (== total when per_page=1). No Link header ⇒ the whole
    /// list fits on one page (0 or 1). Best-effort — any failure returns null so a missing count never
    /// breaks the stats panel.
    /// </summary>
    private static async Task<int?> CountViaLinkAsync(string token, Uri apiBase, string path, string? query, CancellationToken cancellationToken)
    {
        try
        {
            var url = new Uri(apiBase, $"{path}?per_page=1{(query is null ? "" : "&" + query)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("User-Agent", "CodeSpace");

            using var response = await _statsHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            if (response.Headers.TryGetValues("Link", out var links))
            {
                var last = ParseLastPage(string.Join(",", links));
                if (last.HasValue) return last.Value;
            }

            // No Link header → ≤ 1 page of size 1: empty array body ⇒ 0, otherwise 1.
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.AsSpan().TrimStart().StartsWith("[]") ? 0 : 1;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extract N from a GitHub Link header's <c>rel="last"</c> entry: <c>&lt;...?page=N&amp;per_page=1&gt;; rel="last"</c>.</summary>
    private static int? ParseLastPage(string linkHeader)
    {
        foreach (var part in linkHeader.Split(','))
        {
            if (!part.Contains("rel=\"last\"", StringComparison.Ordinal)) continue;

            var match = System.Text.RegularExpressions.Regex.Match(part, @"[?&]page=(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var n)) return n;
        }

        return null;
    }

    // ── Repository history (Code tab): latest commit (header bar) + per-entry last commit (file rows) ──

    public async Task<RemoteCommitSummary?> GetLatestCommitAsync(ProviderContext context, RemoteRepository repository, string? path, string? reference, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);
        var gitRef = string.IsNullOrWhiteSpace(reference) ? repository.DefaultBranch : reference;
        var folder = (path ?? string.Empty).Trim('/');

        return await _resilience.ExecuteAsync(context.Instance, nameof(GetLatestCommitAsync), async _ =>
        {
            var commit = await FetchLatestCommitAsync(client, repository, gitRef, folder).ConfigureAwait(false);
            return commit is null ? null : ToCommitSummary(commit);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, RemoteCommitSummary>> ListPathCommitsAsync(ProviderContext context, RemoteRepository repository, IReadOnlyList<string> paths, string? reference, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);
        var gitRef = string.IsNullOrWhiteSpace(reference) ? repository.DefaultBranch : reference;

        // One "latest commit for this path" call per entry — Octokit is genuinely async, so the bounded
        // map runs them concurrently (cap 8). Best-effort: a path that fails is omitted.
        return await BoundedParallelMap.RunAsync<RemoteCommitSummary>(paths, 8, async (entryPath, _) =>
        {
            var commit = await FetchLatestCommitAsync(client, repository, gitRef, entryPath.Trim('/')).ConfigureAwait(false);
            return commit is null ? null : ToCommitSummary(commit);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GitHubCommit?> FetchLatestCommitAsync(GitHubClient client, RemoteRepository repository, string gitRef, string path)
    {
        var request = new CommitRequest { Sha = gitRef };
        if (!string.IsNullOrEmpty(path)) request.Path = path;

        var commits = await client.Repository.Commit.GetAll(repository.NamespacePath, repository.Name, request, new ApiOptions { PageSize = 1, PageCount = 1 }).ConfigureAwait(false);
        return commits.FirstOrDefault();
    }

    private static RemoteCommitSummary ToCommitSummary(GitHubCommit commit) => new()
    {
        Sha = commit.Sha,
        ShortSha = CommitSummaryText.ShortSha(commit.Sha),
        Message = CommitSummaryText.FirstLine(commit.Commit?.Message),
        AuthorName = commit.Commit?.Author?.Name,
        AuthorAvatarUrl = commit.Author?.AvatarUrl,
        CommittedDate = commit.Commit?.Author?.Date,
        WebUrl = commit.HtmlUrl
    };

    // ── Repository markdown render (Code tab README): GitHub's own /markdown renderer ──

    public async Task<RemoteRenderedMarkdown> RenderMarkdownAsync(ProviderContext context, RemoteRepository repository, string markdown, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(RenderMarkdownAsync), async _ =>
        {
            // `context` = owner/repo so #issues, @mentions, and relative links resolve like on github.com.
            var html = await client.Miscellaneous.RenderArbitraryMarkdown(new NewArbitraryMarkdown(markdown, "gfm", repository.FullPath)).ConfigureAwait(false);
            return new RemoteRenderedMarkdown { Html = html ?? string.Empty };
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitHubClient> BuildClientAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var auth = await _authResolver.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
        var baseAddress = ResolveApiBaseAddress(context.Instance);

        return new GitHubClient(new ProductHeaderValue("CodeSpace"), baseAddress) { Credentials = new Octokit.Credentials(auth.Token) };
    }

    private static Uri ResolveApiBaseAddress(ProviderInstance instance)
    {
        if (!string.IsNullOrWhiteSpace(instance.ApiUrl)) return new Uri(instance.ApiUrl);
        if (string.Equals(instance.BaseUrl?.TrimEnd('/'), "https://github.com", StringComparison.OrdinalIgnoreCase)) return new Uri("https://api.github.com");
        return new Uri(instance.BaseUrl.TrimEnd('/') + "/api/v3/");
    }

    private static RemoteRepository ToRemoteRepository(Octokit.Repository repo) => new()
    {
        ExternalId = repo.Id.ToString(),
        NamespacePath = repo.Owner.Login,
        Name = repo.Name,
        FullPath = repo.FullName,
        DefaultBranch = repo.DefaultBranch ?? "main",
        Visibility = repo.Private ? RepositoryVisibility.Private : RepositoryVisibility.Public,
        Description = repo.Description,
        WebUrl = repo.HtmlUrl,
        CloneUrlHttps = repo.CloneUrl,
        CloneUrlSsh = repo.SshUrl,
        Archived = repo.Archived
    };

    // Caller-side filters: Open|Draft both mean "still open on GitHub" so we ask for State=Open
    // (drafts are open PRs with a flag); Closed|Merged both live under State=Closed (merged PRs
    // are a subset of closed). The four-state distinction is reapplied in ToRemotePullRequest.
    private static ItemStateFilter MapStateFilterToItemState(PullRequestState? state) => state switch
    {
        null => ItemStateFilter.All,
        PullRequestState.Open or PullRequestState.Draft => ItemStateFilter.Open,
        PullRequestState.Merged or PullRequestState.Closed => ItemStateFilter.Closed,
        _ => ItemStateFilter.All
    };

    private static RemotePullRequest ToRemotePullRequest(PullRequest pr)
    {
        // Counts derived from pr.Body even on the list path — Octokit includes Body in
        // list responses, and computing here lets the SPA show "N of M tasks" without us
        // shipping the full markdown body for every row. Body itself stays out of the
        // list payload (omitted below) to keep wire size small.
        var (tasksDone, tasksTotal) = CodeSpace.Core.Services.Providers.Markdown.TaskListCounter.Count(pr.Body);
        return new RemotePullRequest
        {
            ExternalId = pr.Id.ToString(),
            Number = pr.Number,
            Title = pr.Title,
            State = ToPullRequestState(pr),
            SourceBranch = pr.Head?.Ref ?? string.Empty,
            TargetBranch = pr.Base?.Ref ?? string.Empty,
            AuthorLogin = pr.User?.Login,
            AuthorAvatarUrl = pr.User?.AvatarUrl,
            CommentsCount = pr.Comments,
            CreatedDate = pr.CreatedAt,
            UpdatedDate = pr.UpdatedAt,
            MergedDate = pr.MergedAt,
            ClosedDate = pr.ClosedAt,
            WebUrl = pr.HtmlUrl,
            Labels = ToLabelRefs(pr.Labels),
            MilestoneTitle = pr.Milestone?.Title,
            TasksCompleted = tasksDone,
            TasksTotal = tasksTotal
        };
    }

    /// <summary>
    /// Detail variant — includes Body + diff stats + assignees/reviewers/milestone that
    /// the list endpoint omits. Keep the list converter slim; this is only used by
    /// GetPullRequestAsync.
    /// </summary>
    private static RemotePullRequest ToRemotePullRequestDetail(PullRequest pr)
    {
        var (tasksDone, tasksTotal) = CodeSpace.Core.Services.Providers.Markdown.TaskListCounter.Count(pr.Body);
        return new RemotePullRequest
        {
            ExternalId = pr.Id.ToString(),
            Number = pr.Number,
            Title = pr.Title,
            State = ToPullRequestState(pr),
            SourceBranch = pr.Head?.Ref ?? string.Empty,
            TargetBranch = pr.Base?.Ref ?? string.Empty,
            AuthorLogin = pr.User?.Login,
            AuthorAvatarUrl = pr.User?.AvatarUrl,
            CommentsCount = pr.Comments,
            CreatedDate = pr.CreatedAt,
            UpdatedDate = pr.UpdatedAt,
            MergedDate = pr.MergedAt,
            ClosedDate = pr.ClosedAt,
            WebUrl = pr.HtmlUrl,
            Labels = ToLabelRefs(pr.Labels),
            Body = pr.Body,
            CommitsCount = pr.Commits,
            Additions = pr.Additions,
            Deletions = pr.Deletions,
            ChangedFilesCount = pr.ChangedFiles,
            Assignees = pr.Assignees?.Select(a => a.Login).ToList() ?? new List<string>(),
            RequestedReviewers = pr.RequestedReviewers?.Select(r => r.Login).ToList() ?? new List<string>(),
            MilestoneTitle = pr.Milestone?.Title,
            TasksCompleted = tasksDone,
            TasksTotal = tasksTotal
        };
    }

    /// <summary>
    /// Maps Octokit labels into our colour-aware <see cref="LabelRef"/> shape. Octokit's
    /// <c>Label.Color</c> is already the hex string GitHub returns — 6 chars, no leading
    /// <c>#</c> — which is the exact wire format <see cref="LabelRef.Color"/> expects, so
    /// no normalisation is needed. Defaults to <c>null</c> only if the label predates
    /// GitHub's mandatory-colour migration (effectively never).
    /// </summary>
    private static List<LabelRef> ToLabelRefs(IReadOnlyList<Label>? labels) =>
        labels?.Select(l => new LabelRef { Name = l.Name, Color = string.IsNullOrWhiteSpace(l.Color) ? null : l.Color }).ToList()
        ?? new List<LabelRef>();

    private static RemotePullRequestCommit ToRemotePullRequestCommit(PullRequestCommit c)
    {
        // GitHub doesn't formally split subject from body — they're separated by a blank
        // line in the same Message string. Splitting on the first \n\n keeps the title
        // tight in the UI while preserving any longer explanation underneath.
        var message = c.Commit?.Message ?? string.Empty;
        var split = message.Split(new[] { "\n\n" }, 2, StringSplitOptions.None);
        var title = split[0].Trim();
        var body = split.Length > 1 ? split[1].Trim() : null;

        return new RemotePullRequestCommit
        {
            Sha = c.Sha,
            ShortSha = c.Sha.Length >= 7 ? c.Sha[..7] : c.Sha,
            Title = title,
            Body = string.IsNullOrWhiteSpace(body) ? null : body,
            AuthorLogin = c.Author?.Login,
            AuthorAvatarUrl = c.Author?.AvatarUrl,
            AuthorName = c.Commit?.Author?.Name,
            AuthorEmail = c.Commit?.Author?.Email,
            AuthoredDate = c.Commit?.Author?.Date ?? DateTimeOffset.UnixEpoch,
            WebUrl = c.HtmlUrl
        };
    }

    private static RemotePullRequestFile ToRemotePullRequestFile(PullRequestFile f) => new()
    {
        FileName = f.FileName,
        PreviousFileName = f.PreviousFileName,
        Status = MapFileChangeStatus(f.Status),
        Additions = f.Additions,
        Deletions = f.Deletions,
        Patch = f.Patch    // Null when GitHub suppressed the diff (binary / too large).
    };

    private static FileChangeStatus MapFileChangeStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "added" => FileChangeStatus.Added,
        "removed" => FileChangeStatus.Removed,
        "renamed" => FileChangeStatus.Renamed,
        // GitHub also returns "copied" / "changed" / "unchanged" — collapse them all
        // into Modified rather than over-modelling. The diff text carries the nuance.
        _ => FileChangeStatus.Modified
    };

    private static PullRequestState ToPullRequestState(PullRequest pr)
    {
        if (pr.Merged) return PullRequestState.Merged;
        if (pr.State.Value == ItemState.Closed) return PullRequestState.Closed;
        if (pr.Draft) return PullRequestState.Draft;
        return PullRequestState.Open;
    }
}
