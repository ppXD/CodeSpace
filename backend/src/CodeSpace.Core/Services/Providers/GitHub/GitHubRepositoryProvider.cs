using System.Runtime.CompilerServices;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Resilience;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Exceptions;
using Octokit;
using FileChangeStatus = CodeSpace.Messages.Enums.FileChangeStatus;
using PullRequestState = CodeSpace.Messages.Enums.PullRequestState;
using PullRequestReviewVerdict = CodeSpace.Messages.Enums.PullRequestReviewVerdict;
using PullRequestCheckStatus = CodeSpace.Messages.Enums.PullRequestCheckStatus;
using RepositoryVisibility = CodeSpace.Messages.Enums.RepositoryVisibility;
using ProviderKind = CodeSpace.Messages.Enums.ProviderKind;

namespace CodeSpace.Core.Services.Providers.GitHub;

public sealed class GitHubRepositoryProvider : IRepositoryCatalogCapability, IPullRequestCatalogCapability, IPullRequestCommentCapability, IPullRequestReviewCapability, IRepositoryAccessCapability, ICredentialProbeCapability, IWebhookRegistrationCapability, IWebhookSignatureVerifier, IWebhookEventNormalizer
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
        try
        {
            var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

            await _resilience.ExecuteAsync(context.Instance, nameof(GetActorAccessAsync), async _ =>
            {
                // GitHub reviews need only READ — if the actor can fetch the repo they can review it
                // (the "can't approve your own PR" case is a 422 we can't pre-empt here). A private repo
                // the actor isn't a collaborator on returns 404 → caught below as "no access".
                await client.Repository.Get(long.Parse(repository.ExternalId)).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);

            return RepositoryActorAccess.Allowed;
        }
        catch (ProviderApiException ex) when (ex.StatusCode is 403 or 404)
        {
            return RepositoryActorAccess.Denied("You don't have access to this repository on GitHub. Ask a maintainer to add you, then try again.");
        }
    }

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
