using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Resilience;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;
using NGitLab;
using NGitLab.Models;
using GitLabMergeRequestState = NGitLab.Models.MergeRequestState;

namespace CodeSpace.Core.Services.Providers.GitLab;

public sealed class GitLabRepositoryProvider : IRepositoryCatalogCapability, IPullRequestCatalogCapability, IPullRequestCommentCapability, IPullRequestReviewCapability, IRepositoryAccessCapability, ICredentialProbeCapability, IWebhookRegistrationCapability, IWebhookSignatureVerifier, IWebhookEventNormalizer
{
    private readonly IProviderAuthResolver _authResolver;
    private readonly IExternalCallResilience _resilience;
    private readonly GitLabSignatureVerifier _signatureVerifier;
    private readonly GitLabEventNormalizer _eventNormalizer;

    public GitLabRepositoryProvider(IProviderAuthResolver authResolver, IExternalCallResilience resilience, GitLabSignatureVerifier signatureVerifier, GitLabEventNormalizer eventNormalizer)
    {
        _authResolver = authResolver;
        _resilience = resilience;
        _signatureVerifier = signatureVerifier;
        _eventNormalizer = eventNormalizer;
    }

    public ProviderKind Kind => ProviderKind.GitLab;

    public async Task<RemoteRepository> GetByExternalIdAsync(ProviderContext context, string externalId, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(GetByExternalIdAsync), _ =>
        {
            var project = client.Projects.GetById(int.Parse(externalId), new SingleProjectQuery());
            return Task.FromResult(ToRemoteRepository(project));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteRepository> ResolveByPathAsync(ProviderContext context, string namespacePath, string name, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(ResolveByPathAsync), _ =>
        {
            var fullPath = $"{namespacePath}/{name}";
            var project = client.Projects[fullPath];
            return Task.FromResult(ToRemoteRepository(project));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteRepositoryPage> PagedListAccessibleAsync(ProviderContext context, string? search, int page, int perPage, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(PagedListAccessibleAsync), async _ =>
        {
            var trimmedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

            // GitLab's /projects endpoint accepts both `?search=` and `?per_page=` natively,
            // so server-side filter+page work in one call. Scope=Accessible maps to
            // `membership=true` on the wire — exactly the visibility the picker wants.
            //
            // Heads-up for any future caller passing `search`: GitLab silently rejects
            // search queries shorter than 3 characters on self-hosted instances unless the
            // `project_searchable_with_short_query` feature flag is enabled (off by default).
            // Verified empirically: `search=a` returns 0 even when the membership list has
            // hundreds of "a"-containing projects, while `search=abc` returns the expected
            // hits. The SPA's picker therefore eager-fetches and filters client-side, but if
            // you call this directly with a 1–2 char term you'll get an empty page — that's
            // GitLab, not us.
            //
            // Same Skip+Take trade-off as the MR list — NGitLab doesn't expose a `?page=` knob
            // on ProjectQuery, so paging to page N still walks pages 1..N internally. Fine for
            // "Load more once or twice", a known limitation for jump-to-last navigation.
            var query = new ProjectQuery
            {
                Scope = ProjectQueryScope.Accessible,
                Search = trimmedSearch,
                PerPage = perPage,
                OrderBy = "last_activity_at"
            };

            var skip = (page - 1) * perPage;
            var itemsTask = Task.Run(() => client.Projects.Get(query)
                .Skip(skip)
                .Take(perPage)
                .Select(ToRemoteRepository)
                .ToList(), _);

            // GraphQL gives us a real total in one round-trip — `projects(membership: true)`
            // matches the REST `scope=accessible` visibility. Run in parallel with the REST
            // page fetch; a GraphQL failure (older self-hosted instance, missing scope)
            // degrades to null so the SPA's open-ended pager still works.
            var countTask = TryCountAccessibleAsync(client, trimmedSearch, _);

            await Task.WhenAll(itemsTask, countTask).ConfigureAwait(false);

            return new RemoteRepositoryPage
            {
                Items = itemsTask.Result,
                TotalCount = countTask.Result
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// GraphQL count for projects the current user can access, optionally filtered by name.
    /// Mirrors the REST <c>scope=accessible</c> visibility via <c>membership: true</c>.
    /// Returns null on any GraphQL failure — the SPA falls back to the open-ended pager.
    /// </summary>
    private static async Task<int?> TryCountAccessibleAsync(GitLabClient client, string? search, CancellationToken cancellationToken)
    {
        try
        {
            var query = new GraphQLQuery
            {
                Query = """
                    query AccessibleCount($search: String) {
                      projects(search: $search, membership: true) {
                        count
                      }
                    }
                """
            };
            query.Variables["search"] = search ?? string.Empty;

            var response = await client.GraphQL.ExecuteAsync<GitLabProjectsCountResponse>(query, cancellationToken).ConfigureAwait(false);
            return response?.Projects?.Count;
        }
        catch
        {
            return null;
        }
    }

    private sealed record GitLabProjectsCountResponse(GitLabProjectsCountConnection? Projects);
    private sealed record GitLabProjectsCountConnection(int Count);

    public async Task<IReadOnlyList<RemotePullRequest>> ListPullRequestsAsync(ProviderContext context, RemoteRepository repository, PullRequestState? stateFilter, int page, int perPage, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(ListPullRequestsAsync), _ =>
        {
            var projectId = int.Parse(repository.ExternalId);

            // GitLab labels are bare names; colour lives on the project-labels endpoint.
            // Best-effort lookup (see method doc) shared across every page request.
            var labelColors = TryFetchProjectLabelColors(client, projectId);

            // GitLab's `state=closed` is NOT the same as GitHub's `Closed` — it excludes
            // merged MRs, which on GitLab are a distinct state (`merged`), not closed-with-flag.
            // Our two-bucket UI (Open / Closed) treats both closed and merged as "no longer
            // open" to match GitHub's convention and to align with our own counts call (which
            // sums closed + merged). So when the caller asks for `Closed`, we fan out to two
            // parallel REST calls (state=closed, state=merged), merge by UpdatedAt desc, and
            // paginate locally. Open / Draft / Merged go through the single-state fast path.
            //
            // Cost: page N of Closed costs ~2× the row volume of page N of Open because we
            // walk both buckets. Acceptable for the typical "scan a few pages" use case;
            // would need a smarter strategy if a repo had tens of thousands of closed MRs.
            var mrs = stateFilter == PullRequestState.Closed
                ? FetchCombinedClosedMergedPage(client, projectId, page, perPage)
                : FetchSingleStatePage(client, projectId, MapStateFilterToGitLab(stateFilter), page, perPage);

            return Task.FromResult((IReadOnlyList<RemotePullRequest>)mrs.Select(mr => ToRemotePullRequest(mr, labelColors)).ToList());
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Single-state page fetch (Open, Draft, Merged, or All). NGitLab's iterator pages
    /// internally; <c>Skip+Take</c> walks pages 1..N which is wasteful at deep pagination
    /// but matches what the MR-list code did before.
    /// </summary>
    private static List<MergeRequest> FetchSingleStatePage(GitLabClient client, int projectId, GitLabMergeRequestState? state, int page, int perPage)
    {
        var query = new MergeRequestQuery
        {
            State = state,
            OrderBy = "updated_at",
            Sort = "desc",
            PerPage = perPage
        };
        var skip = (page - 1) * perPage;
        return client.GetMergeRequest(projectId).Get(query).Skip(skip).Take(perPage).ToList();
    }

    /// <summary>
    /// Combined Closed+Merged page — fetches both states up to <paramref name="page"/>×<paramref name="perPage"/>
    /// items each (sorted desc by activity), merges, re-sorts, slices the requested page.
    /// Pulls enough headroom to fill the page even when one bucket dominates the other.
    /// </summary>
    private static List<MergeRequest> FetchCombinedClosedMergedPage(GitLabClient client, int projectId, int page, int perPage)
    {
        // Pull `page * perPage` from each state — this guarantees that even if every
        // row on the requested page is from a single bucket, we have enough to fill it.
        // 200-row cap so a deep-page request can't pull tens of thousands of rows.
        var headroom = Math.Min(page * perPage, 200);

        var closedQuery = new MergeRequestQuery { State = GitLabMergeRequestState.closed, OrderBy = "updated_at", Sort = "desc", PerPage = perPage };
        var mergedQuery = new MergeRequestQuery { State = GitLabMergeRequestState.merged, OrderBy = "updated_at", Sort = "desc", PerPage = perPage };

        var closedItems = client.GetMergeRequest(projectId).Get(closedQuery).Take(headroom).ToList();
        var mergedItems = client.GetMergeRequest(projectId).Get(mergedQuery).Take(headroom).ToList();

        var skip = (page - 1) * perPage;
        return closedItems.Concat(mergedItems)
            .OrderByDescending(mr => mr.UpdatedAt)
            .Skip(skip)
            .Take(perPage)
            .ToList();
    }

    public async Task<RemotePullRequest> GetPullRequestAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(GetPullRequestAsync), async _ =>
        {
            var projectId = int.Parse(repository.ExternalId);
            var labelColors = TryFetchProjectLabelColors(client, projectId);
            var mr = await client.GetMergeRequest(projectId).GetByIidAsync(number, new SingleMergeRequestQuery(), _).ConfigureAwait(false);
            return ToRemotePullRequestDetail(mr, labelColors);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort lookup of project + ancestor-group labels keyed by name → colour
    /// (hex without <c>#</c>). NGitLab's <c>Labels.ForProject</c> uses the
    /// <c>?include_ancestor_groups=true</c> default, so labels defined on the parent
    /// group apply too — important because most GitLab workflows define labels once
    /// at the group level and reuse them across projects.
    /// Returns an empty dictionary on any failure; callers degrade gracefully.
    /// </summary>
    private static Dictionary<string, string?> TryFetchProjectLabelColors(GitLabClient client, int projectId)
    {
        try
        {
            var labels = client.Labels.ForProject(projectId).ToList();
            var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in labels)
            {
                if (string.IsNullOrEmpty(l.Name)) continue;
                // GitLab returns "#ED9121" with the leading hash; strip it so the wire
                // contract matches GitHub's (Octokit returns the hex without #).
                var colour = l.Color;
                if (!string.IsNullOrEmpty(colour) && colour[0] == '#') colour = colour[1..];
                map[l.Name] = string.IsNullOrWhiteSpace(colour) ? null : colour;
            }
            return map;
        }
        catch
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task<IReadOnlyList<RemotePullRequestCommit>> ListPullRequestCommitsAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(ListPullRequestCommitsAsync), _ =>
        {
            var projectId = int.Parse(repository.ExternalId);
            // GitLab returns commits newest-first by default; reverse so the UI ordering
            // (oldest-first, top-to-bottom = chronological history) matches GitHub.
            var commits = client.GetMergeRequest(projectId).Commits(number).All.Reverse().Select(ToRemotePullRequestCommit).ToList();
            return Task.FromResult((IReadOnlyList<RemotePullRequestCommit>)commits);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemotePullRequestFile>> ListPullRequestFilesAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(ListPullRequestFilesAsync), _ =>
        {
            var projectId = int.Parse(repository.ExternalId);
            var changes = client.GetMergeRequest(projectId).Changes(number).MergeRequestChange.Changes;
            var files = (changes ?? Array.Empty<NGitLab.Models.Change>()).Select(ToRemotePullRequestFile).ToList();
            return Task.FromResult((IReadOnlyList<RemotePullRequestFile>)files);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Singleton HttpClient for the X-Total-header fallback path. .NET best practice — one
    /// HttpClient per process (or per host) to avoid socket exhaustion. Per-call auth headers
    /// are attached to each HttpRequestMessage individually so the same client can serve every
    /// credential safely.
    /// </summary>
    private static readonly HttpClient _countsHttpClient = new();

    public async Task<RemotePullRequestCounts> CountPullRequestsAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(CountPullRequestsAsync), async _ =>
        {
            // NGitLab's REST `Get(query)` returns IEnumerable and hides X-Total
            // headers — but GitLab's GraphQL exposes mergeRequests(state){count}
            // directly. One round-trip returns all three buckets:
            //
            //   project(fullPath) { open: mergeRequests(state: opened) { count }
            //                       closed: mergeRequests(state: closed) { count }
            //                       merged: mergeRequests(state: merged) { count } }
            //
            // We then sum closed + merged to match the SPA's two-bucket model
            // (Open includes Draft; Closed includes Merged).
            var query = new GraphQLQuery
            {
                Query = """
                    query CountMrs($fullPath: ID!) {
                      project(fullPath: $fullPath) {
                        opened: mergeRequests(state: opened) { count }
                        closed: mergeRequests(state: closed) { count }
                        merged: mergeRequests(state: merged) { count }
                      }
                    }
                """
            };
            // GraphQLQuery.Variables exposes a get-only dictionary that NGitLab
            // pre-allocates; populate it in place rather than reassigning.
            query.Variables["fullPath"] = repository.FullPath;

            var response = await client.GraphQL.ExecuteAsync<GitLabCountsResponse>(query, _).ConfigureAwait(false);
            var project = response?.Project;

            // GraphQL on GitLab requires the `read_api` (or `api`) OAuth scope. Tokens
            // limited to `read_repository` can list/read merge requests via REST but
            // hit a permissions wall on GraphQL — NGitLab surfaces that as a successful
            // response with `project: null` rather than an exception. When we see the
            // null, fall back to a REST-based count using the same endpoint the PR list
            // already uses, which only needs `read_repository`. Slower (walks the
            // iterator + Count()), but works on any token scope where the list works.
            if (project != null)
            {
                return new RemotePullRequestCounts
                {
                    Open = project.Opened?.Count ?? 0,
                    Closed = (project.Closed?.Count ?? 0) + (project.Merged?.Count ?? 0)
                };
            }

            return await CountViaXTotalAsync(context, int.Parse(repository.ExternalId), _).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private sealed record GitLabCountsResponse(GitLabCountsProject? Project);
    private sealed record GitLabCountsProject(GitLabCountsBucket? Opened, GitLabCountsBucket? Closed, GitLabCountsBucket? Merged);
    private sealed record GitLabCountsBucket(int Count);

    /// <summary>
    /// Counts via GitLab's REST <c>X-Total</c> response header — one round-trip per state,
    /// no iterator walking. Used when GraphQL is unavailable (token lacks read_api).
    /// <para>
    /// Calls <c>GET /api/v4/projects/:id/merge_requests?state=&amp;per_page=1</c> three times
    /// in parallel, reads the <c>X-Total</c> header from each. GitLab omits this header
    /// when the unpaginated total would exceed 10,000 rows (documented performance cap) —
    /// for that pathological case we degrade to <c>10000+</c> conceptually by reporting
    /// the cap.
    /// </para>
    /// <para>
    /// Same scope footprint as the working PR list — <c>read_repository</c> alone suffices
    /// for the REST endpoint, which is the whole point of having this fallback at all.
    /// </para>
    /// </summary>
    private async Task<RemotePullRequestCounts> CountViaXTotalAsync(ProviderContext context, int projectId, CancellationToken cancellationToken)
    {
        var auth = await _authResolver.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
        var host = string.IsNullOrWhiteSpace(context.Instance.ApiUrl) ? context.Instance.BaseUrl : context.Instance.ApiUrl;

        var openTask = FetchStateCountAsync(host, projectId, "opened", auth, cancellationToken);
        var closedTask = FetchStateCountAsync(host, projectId, "closed", auth, cancellationToken);
        var mergedTask = FetchStateCountAsync(host, projectId, "merged", auth, cancellationToken);

        await Task.WhenAll(openTask, closedTask, mergedTask).ConfigureAwait(false);

        Serilog.Log.Information(
            "[GitLabRepoProvider] CountViaXTotalAsync projectId={ProjectId}: opened={Open} closed={Closed} merged={Merged}",
            projectId, openTask.Result, closedTask.Result, mergedTask.Result);

        return new RemotePullRequestCounts
        {
            Open = openTask.Result,
            Closed = closedTask.Result + mergedTask.Result
        };
    }

    /// <summary>
    /// Hits the merge-requests REST endpoint with PerPage=1, parses the <c>X-Total</c>
    /// header. Returns 0 when the header is missing (GitLab's >10k pagination cutoff) or
    /// the response is non-success — counts should never error out the whole call.
    /// </summary>
    private static async Task<int> FetchStateCountAsync(string host, int projectId, string state, ResolvedAuth auth, CancellationToken cancellationToken)
    {
        var url = $"{host.TrimEnd('/')}/api/v4/projects/{projectId}/merge_requests?state={state}&per_page=1";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // GitLab accepts the OAuth Bearer token via the standard Authorization header
        // and PATs via PRIVATE-TOKEN — both work for /merge_requests. Bearer is the
        // common case (OAuth credentials are most users), PAT is the legacy fallback.
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {auth.Token}");
        request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", auth.Token);

        try
        {
            using var response = await _countsHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return 0;

            if (response.Headers.TryGetValues("X-Total", out var values) && int.TryParse(values.FirstOrDefault(), out var total))
            {
                return total;
            }
            // No X-Total → GitLab dropped it because the unpaginated set is >10k rows.
            // We can't get an exact number without walking, so report 10000 as the
            // floor; the SPA shows it as "10,000" which honestly conveys "lots".
            return 10000;
        }
        catch
        {
            return 0;
        }
    }

    public async Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(ListChecksAsync), _ =>
        {
            try
            {
                var projectId = int.Parse(repository.ExternalId);
                var mrClient = client.GetMergeRequest(projectId);

                // MR pipelines are returned newest-first. We only render checks from the
                // LATEST pipeline — older pipelines belong in a "history" view the SPA
                // doesn't have yet, and showing all of them at once would be noise.
                var pipelines = mrClient.GetPipelines(number).ToList();
                if (pipelines.Count == 0) return Task.FromResult<IReadOnlyList<RemotePullRequestCheck>>(Array.Empty<RemotePullRequestCheck>());

                var latest = pipelines[0];

                // IPipelineClient.GetJobs(pipelineId) is the canonical "list jobs in this
                // pipeline" endpoint — one round-trip, returns the typed Job[] directly.
                var jobs = client.GetPipelines(projectId).GetJobs(latest.Id);

                var checks = jobs.Select(ToRemoteCheck).ToList();
                return Task.FromResult<IReadOnlyList<RemotePullRequestCheck>>(checks);
            }
            catch
            {
                // Token without read_api / pipelines scope, or pipelines simply disabled
                // on the project — render no checks rather than failing the PR detail view.
                return Task.FromResult<IReadOnlyList<RemotePullRequestCheck>>(Array.Empty<RemotePullRequestCheck>());
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemotePullRequestComment> PostCommentAsync(ProviderContext context, RemoteRepository repository, int number, string body, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(PostCommentAsync), _ =>
        {
            // NGitLab v11: IMergeRequestClient.Comments(iid) returns IMergeRequestCommentClient.
            // Add(MergeRequestCommentCreate) returns the persisted comment. The mrClient
            // is keyed by projectId; the comments client is then keyed by mrIid.
            var projectId = int.Parse(repository.ExternalId);
            var commentsClient = client.GetMergeRequest(projectId).Comments(number);
            var comment = commentsClient.Add(new NGitLab.Models.MergeRequestCommentCreate { Body = body });

            return Task.FromResult(new RemotePullRequestComment
            {
                ExternalId = comment.Id.ToString(),
                Body = comment.Body,
                AuthorName = comment.Author?.Username ?? "unknown",
                CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(comment.CreatedAt, DateTimeKind.Utc), TimeSpan.Zero),
                WebUrl = null
            });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemotePullRequestReview> SubmitReviewAsync(ProviderContext context, RemoteRepository repository, int number, PullRequestReviewVerdict verdict, string? body, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(SubmitReviewAsync), _ =>
        {
            // GitLab has no native review verdict — post it as a labeled MR note (see GitLabReviewPlan).
            var projectId = int.Parse(repository.ExternalId);
            var note = GitLabReviewPlan.NoteFor(verdict, body);
            var comment = client.GetMergeRequest(projectId).Comments(number).Add(new NGitLab.Models.MergeRequestCommentCreate { Body = note });

            return Task.FromResult(new RemotePullRequestReview
            {
                Verdict = verdict,
                ExternalId = comment.Id.ToString(),
                WebUrl = null
            });
        }, cancellationToken).ConfigureAwait(false);
    }

    private static RemotePullRequestCheck ToRemoteCheck(NGitLab.Models.Job job)
    {
        var status = MapJobStatus(job.Status);

        // NGitLab's StartedAt / FinishedAt are non-nullable DateTime — default(DateTime)
        // is the only signal that the field was absent on the wire. Treat that as
        // "no timestamp" rather than rendering "0001-01-01" in the UI.
        var startedAt = job.StartedAt == default ? (DateTimeOffset?)null : new DateTimeOffset(DateTime.SpecifyKind(job.StartedAt, DateTimeKind.Utc), TimeSpan.Zero);
        var completedAt = job.FinishedAt == default ? (DateTimeOffset?)null : new DateTimeOffset(DateTime.SpecifyKind(job.FinishedAt, DateTimeKind.Utc), TimeSpan.Zero);

        int? duration = null;
        if (startedAt.HasValue && completedAt.HasValue)
        {
            duration = (int)(completedAt.Value - startedAt.Value).TotalSeconds;
        }

        return new RemotePullRequestCheck
        {
            // GitLab jobs name a stage AND a job — combining gives the same "stage / job"
            // shape GitHub's check_run names tend to follow (e.g. "build / test").
            Name = string.IsNullOrEmpty(job.Stage) ? job.Name : $"{job.Stage} / {job.Name}",
            Status = status,
            Conclusion = job.Status.ToString().ToLowerInvariant(),
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationSeconds = duration,
            DetailsUrl = job.WebUrl
        };
    }

    // GitLab JobStatus: Unknown, Running, Pending, Failed, Success, Created, Canceled, Skipped,
    // Manual, NoBuild, Preparing, WaitingForResource, Scheduled, Canceling. Note `Canceled` —
    // single L — not `Cancelled`.
    private static PullRequestCheckStatus MapJobStatus(JobStatus status) => status switch
    {
        JobStatus.Success => PullRequestCheckStatus.Success,
        JobStatus.Failed => PullRequestCheckStatus.Failure,
        JobStatus.Canceled or JobStatus.Canceling => PullRequestCheckStatus.Cancelled,
        JobStatus.Skipped or JobStatus.Manual or JobStatus.Scheduled => PullRequestCheckStatus.Skipped,
        JobStatus.Running or JobStatus.Pending or JobStatus.Created or JobStatus.WaitingForResource or JobStatus.Preparing => PullRequestCheckStatus.Pending,
        // NoBuild + Unknown + anything NGitLab adds later — neutral so an unrecognised
        // status never crashes the PR detail view.
        _ => PullRequestCheckStatus.Neutral
    };

    public async Task<CredentialProbeResult> ProbeCredentialAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        try
        {
            var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

            var identity = await _resilience.ExecuteAsync(context.Instance, nameof(ProbeCredentialAsync), _ =>
            {
                var current = client.Users.Current;
                return Task.FromResult((Id: current.Id.ToString(), Name: current.Username));
            }, cancellationToken).ConfigureAwait(false);

            var scopes = await TryFetchTokenScopesAsync(context, cancellationToken).ConfigureAwait(false);

            return new CredentialProbeResult
            {
                IsValid = true,
                AuthenticatedUserExternalId = identity.Id,
                AuthenticatedUserName = identity.Name,
                GrantedScopes = scopes
            };
        }
        catch (Exception ex)
        {
            return new CredentialProbeResult { IsValid = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Reads the token's OWN granted scopes via <c>GET /api/v4/personal_access_tokens/self</c> so
    /// capability warnings reflect the real PAT (GitLab returns the exact scope list the user ticked).
    /// Best-effort: the endpoint is PAT-only, so OAuth / group / impersonation tokens get 401/403 — any
    /// non-success or failure returns null, which the capability check reads as "scopes unknown" (no
    /// false warnings) rather than "no scopes". Mirrors the raw-HTTP pattern used by CountViaXTotalAsync.
    /// </summary>
    private async Task<IReadOnlyList<string>?> TryFetchTokenScopesAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var auth = await _authResolver.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
        var host = string.IsNullOrWhiteSpace(context.Instance.ApiUrl) ? context.Instance.BaseUrl : context.Instance.ApiUrl;
        var url = $"{host.TrimEnd('/')}/api/v4/personal_access_tokens/self";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {auth.Token}");
        request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", auth.Token);

        try
        {
            using var response = await _countsHttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseTokenScopes(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Pure parse of GitLab's <c>personal_access_tokens/self</c> body → the <c>scopes</c> array.
    /// Returns null (not empty) when the field is absent, empty, or the JSON is malformed, so callers
    /// treat the result as "unknown" rather than "zero scopes".
    /// </summary>
    internal static IReadOnlyList<string>? ParseTokenScopes(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("scopes", out var scopesEl) || scopesEl.ValueKind != JsonValueKind.Array) return null;

            var scopes = scopesEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            return scopes.Count > 0 ? scopes : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<RepositoryActorAccess> GetActorAccessAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken)
    {
        // A read-only token can't make an attributable write (post a review note / approve) — those need
        // the `api` scope. Checked FIRST (no round-trip) when the token's scopes are known (captured at
        // link time, PR #177); unknown scopes (null/empty) fall through to the membership probe. This is
        // the dimension a Reporter-with-read-token AND an Owner-with-read_api-token both fail on.
        if (LacksApiScope(context.Credential.Scopes))
            return RepositoryActorAccess.Denied("Your GitLab identity's token is read-only — it's missing the 'api' scope needed to submit a review. Reconnect it with an api-scoped token.");

        var auth = await _authResolver.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
        var host = string.IsNullOrWhiteSpace(context.Instance.ApiUrl) ? context.Instance.BaseUrl : context.Instance.ApiUrl;
        var url = $"{host.TrimEnd('/')}/api/v4/projects/{Uri.EscapeDataString(repository.ExternalId)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {auth.Token}");
        request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", auth.Token);

        try
        {
            using var response = await _countsHttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            Serilog.Log.Information("[preflight-gitlab] GET projects/{Id} → HTTP {Status}", repository.ExternalId, (int)response.StatusCode);

            // Can't even see the project → not a member / no access.
            if ((int)response.StatusCode is 403 or 404)
                return RepositoryActorAccess.Denied("You're not a member of this GitLab project, or can't access it. Ask a maintainer to add you, then try again.");

            // Inconclusive (transient / unexpected) — never block a legitimate click on a flaky probe; the write stays the backstop.
            if (!response.IsSuccessStatusCode) return RepositoryActorAccess.Allowed;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var level = ParseProjectAccessLevel(json);
            Serilog.Log.Information("[preflight-gitlab] project {Id} access_level={Level}", repository.ExternalId, level?.ToString() ?? "(null)");

            // GitLab levels: Guest 10, Reporter 20, Developer 30, Maintainer 40, Owner 50. Approving / posting
            // an MR note needs Developer+. Null = visible project but no membership grant → below Developer.
            const int developer = 30;
            return level >= developer ? RepositoryActorAccess.Allowed : RepositoryActorAccess.Denied(ReasonForLevel(level));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Network blip / DNS / unexpected — don't block a legitimate click on an inconclusive probe.
            Serilog.Log.Warning(ex, "[preflight-gitlab] access probe for project {Id} failed — degrading to allowed", repository.ExternalId);
            return RepositoryActorAccess.Allowed;
        }
    }

    /// <summary>
    /// Pure parse of GitLab's <c>GET /projects/:id</c> body → the caller's effective access level: the max
    /// of <c>permissions.project_access.access_level</c> and <c>permissions.group_access.access_level</c>.
    /// Null when neither is present (a visible project the user holds no membership grant on) or the JSON
    /// is malformed — callers treat null as "below Developer".
    /// </summary>
    internal static int? ParseProjectAccessLevel(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("permissions", out var perms) || perms.ValueKind != JsonValueKind.Object) return null;

            int? Read(string key) =>
                perms.TryGetProperty(key, out var access) && access.ValueKind == JsonValueKind.Object
                && access.TryGetProperty("access_level", out var lvl) && lvl.ValueKind == JsonValueKind.Number
                    ? lvl.GetInt32() : null;

            var project = Read("project_access");
            var group = Read("group_access");

            return project == null && group == null ? null : Math.Max(project ?? 0, group ?? 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReasonForLevel(int? level)
    {
        var role = level switch { 10 => "Guest", 20 => "Reporter", _ => "below Developer" };
        return $"Your GitLab role on this project is {role} — reviewing needs Developer or higher. Ask a maintainer to raise your access, then try again.";
    }

    /// <summary>True when the token's scopes are KNOWN and lack <c>api</c> — the umbrella scope every
    /// attributable GitLab write (review note / approve / webhook) needs. Null/empty = unknown → don't
    /// deny on it (the membership probe + the wire 403 stay the backstop).</summary>
    internal static bool LacksApiScope(IReadOnlyList<string>? scopes) =>
        scopes is { Count: > 0 } && !scopes.Any(s => string.Equals(s, "api", StringComparison.OrdinalIgnoreCase));

    public async Task<RemoteWebhook?> FindWebhookByCallbackUrlAsync(ProviderContext context, RemoteRepository repository, string callbackUrl, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(FindWebhookByCallbackUrlAsync), _ =>
        {
            // GitLab's IProjectHooksClient exposes All as IEnumerable<ProjectHook>; we
            // scan once and pick the entry whose Url matches the callback we set at
            // registration time. The wire URL is set via Uri so we compare strings via
            // OrdinalIgnoreCase (the host component is case-insensitive; we tolerate
            // trailing-slash drift the same way).
            var projectId = int.Parse(repository.ExternalId);
            var hooks = client.GetRepository(projectId).ProjectHooks.All;

            foreach (var hook in hooks)
            {
                var hookUrl = hook.Url?.ToString();
                if (!string.IsNullOrEmpty(hookUrl) && string.Equals(hookUrl, callbackUrl, StringComparison.OrdinalIgnoreCase))
                {
                    // GitLab ProjectHook doesn't expose the flag-style "subscribed_events"
                    // string array we model — events are individual booleans on the hook
                    // record. Surface the boolean → string mapping that mirrors what
                    // RegisterWebhookAsync set up, so a later code path can decide whether
                    // to re-register with a wider subscription if the set has grown.
                    var subscribed = new List<string>();
                    if (hook.PushEvents) subscribed.Add("push");
                    if (hook.MergeRequestsEvents) subscribed.Add("merge_request");
                    if (hook.IssuesEvents) subscribed.Add("issue");

                    return Task.FromResult<RemoteWebhook?>(new RemoteWebhook
                    {
                        ExternalId = hook.Id.ToString(),
                        CallbackUrl = hookUrl,
                        SubscribedEvents = subscribed,
                        Active = true
                    });
                }
            }

            return Task.FromResult<RemoteWebhook?>(null);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteWebhook> RegisterWebhookAsync(ProviderContext context, RemoteRepository repository, WebhookRegistration request, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(context.Instance, nameof(RegisterWebhookAsync), _ =>
        {
            var projectId = int.Parse(repository.ExternalId);
            var upsert = new ProjectHookUpsert
            {
                Url = new Uri(request.CallbackUrl),
                Token = request.Secret,
                PushEvents = request.SubscribedEvents.Any(e => e.Contains("push", StringComparison.OrdinalIgnoreCase)),
                MergeRequestsEvents = request.SubscribedEvents.Any(e => e.Contains("merge_request", StringComparison.OrdinalIgnoreCase)),
                IssuesEvents = request.SubscribedEvents.Any(e => e.Contains("issue", StringComparison.OrdinalIgnoreCase)),
                EnableSslVerification = true
            };

            var created = client.GetRepository(projectId).ProjectHooks.Create(upsert);

            return Task.FromResult(new RemoteWebhook
            {
                ExternalId = created.Id.ToString(),
                CallbackUrl = request.CallbackUrl,
                SubscribedEvents = request.SubscribedEvents.ToList(),
                Active = true
            });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteWebhookAsync(ProviderContext context, RemoteRepository repository, string externalWebhookId, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        await _resilience.ExecuteAsync(context.Instance, nameof(DeleteWebhookAsync), _ =>
        {
            var projectId = int.Parse(repository.ExternalId);
            client.GetRepository(projectId).ProjectHooks.Delete(int.Parse(externalWebhookId));
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
    }

    public bool VerifySignature(string body, IReadOnlyDictionary<string, string> headers, string secret) => _signatureVerifier.Verify(body, headers, secret);

    public NormalizedEvent? Normalize(Guid repositoryId, string body, IReadOnlyDictionary<string, string> headers) => _eventNormalizer.Normalize(repositoryId, body, headers);

    private async Task<GitLabClient> BuildClientAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var auth = await _authResolver.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
        var host = string.IsNullOrWhiteSpace(context.Instance.ApiUrl) ? context.Instance.BaseUrl : context.Instance.ApiUrl;

        return new GitLabClient(host, auth.Token);
    }

    private static RemoteRepository ToRemoteRepository(Project project) => new()
    {
        ExternalId = project.Id.ToString(),
        NamespacePath = project.Namespace?.FullPath ?? string.Empty,
        Name = project.Name,
        FullPath = project.PathWithNamespace,
        DefaultBranch = project.DefaultBranch ?? "main",
        Visibility = MapVisibilityFromString(project.VisibilityLevel.ToString()),
        Description = project.Description,
        WebUrl = project.WebUrl,
        CloneUrlHttps = project.HttpUrl,
        CloneUrlSsh = project.SshUrl,
        Archived = project.Archived
    };

    private static RepositoryVisibility MapVisibilityFromString(string? visibility) => visibility?.ToLowerInvariant() switch
    {
        "public" => RepositoryVisibility.Public,
        "internal" => RepositoryVisibility.Internal,
        _ => RepositoryVisibility.Private
    };

    // GitLab's filter is single-valued; Draft is orthogonal so we pass through to `opened`
    // and let ToRemotePullRequest reapply the Draft distinction. Merged uses GitLab's
    // dedicated `merged` filter so we don't have to refetch and post-filter.
    private static GitLabMergeRequestState? MapStateFilterToGitLab(PullRequestState? state) => state switch
    {
        null => null,
        PullRequestState.Open or PullRequestState.Draft => GitLabMergeRequestState.opened,
        PullRequestState.Merged => GitLabMergeRequestState.merged,
        PullRequestState.Closed => GitLabMergeRequestState.closed,
        _ => null
    };

    private static RemotePullRequest ToRemotePullRequest(MergeRequest mr, IReadOnlyDictionary<string, string?> labelColors)
    {
        // NGitLab populates Description on list responses too, so we can compute task
        // counts here without needing the detail fetch. Body itself stays absent from
        // the list payload (omitted below) to keep wire size minimal — only the small
        // int counts make it through.
        var (tasksDone, tasksTotal) = CodeSpace.Core.Services.Providers.Markdown.TaskListCounter.Count(mr.Description);
        return new RemotePullRequest
        {
            ExternalId = mr.Id.ToString(),
            Number = (int)mr.Iid,
            Title = mr.Title,
            State = ToPullRequestState(mr),
            SourceBranch = mr.SourceBranch ?? string.Empty,
            TargetBranch = mr.TargetBranch ?? string.Empty,
            AuthorLogin = mr.Author?.Username,
            AuthorAvatarUrl = mr.Author?.AvatarURL,
            CommentsCount = mr.UserNotesCount,
            CreatedDate = new DateTimeOffset(DateTime.SpecifyKind(mr.CreatedAt, DateTimeKind.Utc), TimeSpan.Zero),
            UpdatedDate = new DateTimeOffset(DateTime.SpecifyKind(mr.UpdatedAt, DateTimeKind.Utc), TimeSpan.Zero),
            MergedDate = mr.MergedAt.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(mr.MergedAt.Value, DateTimeKind.Utc), TimeSpan.Zero) : null,
            ClosedDate = mr.ClosedAt.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(mr.ClosedAt.Value, DateTimeKind.Utc), TimeSpan.Zero) : null,
            WebUrl = mr.WebUrl,
            Labels = ToLabelRefs(mr.Labels, labelColors),
            TasksCompleted = tasksDone,
            TasksTotal = tasksTotal
        };
    }

    /// <summary>
    /// Detail variant — populates Body + assignees + reviewers + milestone. GitLab returns
    /// ChangesCount as a string ("12 files") rather than int additions/deletions, so we leave
    /// the +/- stat fields null and let the SPA degrade gracefully (no "+N -M" badge for
    /// GitLab) instead of shipping a string parser.
    /// </summary>
    private static RemotePullRequest ToRemotePullRequestDetail(MergeRequest mr, IReadOnlyDictionary<string, string?> labelColors)
    {
        var baseline = ToRemotePullRequest(mr, labelColors);
        return baseline with
        {
            Body = mr.Description,
            Assignees = mr.Assignees?.Select(a => a.Username).Where(u => !string.IsNullOrEmpty(u)).ToList() ?? new List<string>(),
            RequestedReviewers = mr.Reviewers?.Select(r => r.Username).Where(u => !string.IsNullOrEmpty(u)).ToList() ?? new List<string>(),
            MilestoneTitle = mr.Milestone?.Title
        };
    }

    /// <summary>
    /// Maps GitLab's bare label-name array onto our colour-aware <see cref="LabelRef"/>
    /// shape, looking each name up in the per-project colour map. Missing entries return
    /// a colour-less LabelRef — the SPA falls back to a deterministic name-hash palette.
    /// </summary>
    private static List<LabelRef> ToLabelRefs(IEnumerable<string>? names, IReadOnlyDictionary<string, string?> labelColors)
    {
        if (names == null) return new List<LabelRef>();
        var result = new List<LabelRef>();
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name)) continue;
            labelColors.TryGetValue(name, out var colour);
            result.Add(new LabelRef { Name = name, Color = colour });
        }
        return result;
    }

    private static RemotePullRequestCommit ToRemotePullRequestCommit(NGitLab.Models.Commit c)
    {
        var fullSha = c.Id.ToString();
        return new RemotePullRequestCommit
        {
            Sha = fullSha,
            ShortSha = string.IsNullOrEmpty(c.ShortId) ? (fullSha.Length >= 7 ? fullSha[..7] : fullSha) : c.ShortId,
            Title = c.Title ?? c.Message?.Split('\n', 2)[0] ?? string.Empty,
            // GitLab returns Title separately; Message is the full multi-line text. If Message
            // is longer than Title, the rest IS the body.
            Body = c.Message != null && c.Title != null && c.Message.Length > c.Title.Length
                ? c.Message[c.Title.Length..].TrimStart('\n').Trim()
                : null,
            AuthorLogin = null,    // GitLab Commit doesn't expose the author's GitLab login,
            AuthorAvatarUrl = null, // only the git author Name + Email. UI shows those instead.
            AuthorName = c.AuthorName,
            AuthorEmail = c.AuthorEmail,
            AuthoredDate = new DateTimeOffset(DateTime.SpecifyKind(c.AuthoredDate, DateTimeKind.Utc), TimeSpan.Zero),
            WebUrl = c.WebUrl
        };
    }

    private static RemotePullRequestFile ToRemotePullRequestFile(NGitLab.Models.Change ch)
    {
        var status = ch.NewFile ? FileChangeStatus.Added
            : ch.DeletedFile ? FileChangeStatus.Removed
            : ch.RenamedFile ? FileChangeStatus.Renamed
            : FileChangeStatus.Modified;

        // GitLab's Diff is the unified-diff body (same shape as GitHub's Patch). Counts aren't
        // surfaced per-file, so we count `+`/`-` lines from the diff text itself. Cheap and
        // matches what GitHub's API returns — for a binary file the diff is null, counts 0.
        var (additions, deletions) = CountDiffLines(ch.Diff);

        return new RemotePullRequestFile
        {
            FileName = ch.NewPath ?? ch.OldPath ?? string.Empty,
            PreviousFileName = ch.RenamedFile ? ch.OldPath : null,
            Status = status,
            Additions = additions,
            Deletions = deletions,
            Patch = string.IsNullOrEmpty(ch.Diff) ? null : ch.Diff
        };
    }

    private static (int additions, int deletions) CountDiffLines(string? diff)
    {
        if (string.IsNullOrEmpty(diff)) return (0, 0);

        var additions = 0;
        var deletions = 0;

        foreach (var line in diff.Split('\n'))
        {
            // Skip the patch headers: hunk anchors `@@` and full-line markers `+++`/`---`.
            if (line.StartsWith("@@", StringComparison.Ordinal)) continue;
            if (line.StartsWith("+++", StringComparison.Ordinal)) continue;
            if (line.StartsWith("---", StringComparison.Ordinal)) continue;

            if (line.StartsWith('+')) additions++;
            else if (line.StartsWith('-')) deletions++;
        }

        return (additions, deletions);
    }

    private static PullRequestState ToPullRequestState(MergeRequest mr)
    {
        return mr.State?.ToLowerInvariant() switch
        {
            "merged" => PullRequestState.Merged,
            "closed" => PullRequestState.Closed,
            "locked" => PullRequestState.Closed,
            "opened" when mr.Draft => PullRequestState.Draft,
            _ => PullRequestState.Open
        };
    }
}
