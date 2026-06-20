using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;

namespace CodeSpace.IntegrationTests.Binding;

/// <summary>
/// In-memory remote-hook store backing the <see cref="TestRepositoryProvider"/>. Registered
/// as a singleton in the test container so every fixture sees a fresh, isolated store — no
/// cross-test bleed through process-static fields.
///
/// <para>Records every call <c>RegisterWebhookAsync</c> makes and lets <c>FindWebhookByCallbackUrlAsync</c>
/// return the matching row. The registrar's idempotency check (find-by-URL → reuse instead of
/// re-register) flows through both methods, so test assertions like "registration ran exactly
/// once" can read <see cref="RegisterCallCount"/>.</para>
/// </summary>
public sealed class TestRemoteHookStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, RemoteWebhook> _byCallbackUrl = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Total times <c>RegisterWebhookAsync</c> created a fresh hook (not counting find-by-URL reuse).</summary>
    public int RegisterCallCount { get; private set; }

    public RemoteWebhook? Find(string callbackUrl)
    {
        lock (_lock)
        {
            return _byCallbackUrl.TryGetValue(callbackUrl, out var hook) ? hook : null;
        }
    }

    public RemoteWebhook Register(WebhookRegistration request)
    {
        var hook = new RemoteWebhook
        {
            ExternalId = $"test-hook-{Guid.NewGuid():N}",
            CallbackUrl = request.CallbackUrl,
            SubscribedEvents = request.SubscribedEvents.ToList(),
            Active = true
        };

        lock (_lock)
        {
            _byCallbackUrl[request.CallbackUrl] = hook;
            RegisterCallCount++;
        }

        return hook;
    }

    public void Reset()
    {
        lock (_lock)
        {
            _byCallbackUrl.Clear();
            RegisterCallCount = 0;
        }
    }
}

public sealed class TestRepositoryProvider : IRepositoryCatalogCapability, ICredentialProbeCapability, IPullRequestReviewCapability, IPullRequestWriteCapability, IIssueCatalogCapability, IIssueWriteCapability, IReleaseCatalogCapability, IRepositoryInsightsCapability, IRepositoryAccessCapability, IRepositorySourceCapability, IWebhookRegistrationCapability, IWebhookSignatureVerifier, IWebhookEventNormalizer
{
    /// <summary>The deterministic root-tree entries the source capability returns — grounding tests assert these surface in the planner's grounding string.</summary>
    public static readonly IReadOnlyList<string> RootEntryNames = new[] { "src", "README.md" };

    private readonly TestRemoteHookStore _hookStore;

    public TestRepositoryProvider(TestRemoteHookStore hookStore) { _hookStore = hookStore; }

    public ProviderKind Kind => ProviderKind.Git;

    // ── IRepositorySourceCapability (the Code-tab read path the planner grounds against) ──

    public Task<IReadOnlyList<RemoteBranch>> ListBranchesAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RemoteBranch>>(new[] { new RemoteBranch { Name = repository.DefaultBranch, IsDefault = true } });

    // One non-recursive root listing: a directory + a file, deterministic so a grounding test asserts they surface.
    public Task<IReadOnlyList<RemoteTreeEntry>> ListTreeAsync(ProviderContext context, RemoteRepository repository, string? path, string? reference, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RemoteTreeEntry>>(new[]
        {
            new RemoteTreeEntry { Name = "src", Path = "src", Type = RemoteTreeEntryType.Directory },
            new RemoteTreeEntry { Name = "README.md", Path = "README.md", Type = RemoteTreeEntryType.File, Size = 12 },
        });

    public Task<RemoteFileContent> GetFileAsync(ProviderContext context, RemoteRepository repository, string path, string? reference, CancellationToken cancellationToken) =>
        Task.FromResult(new RemoteFileContent { Path = path, Name = path, Size = 0, Text = "" });

    public Task<RemoteRepository> GetByExternalIdAsync(ProviderContext context, string externalId, CancellationToken cancellationToken) => Task.FromResult(BuildRemoteRepo(externalId, externalId));

    public Task<RemoteRepository> ResolveByPathAsync(ProviderContext context, string namespacePath, string name, CancellationToken cancellationToken) => Task.FromResult(BuildRemoteRepo($"id-{namespacePath}-{name}", $"{namespacePath}/{name}"));

    public Task<RemoteRepositoryPage> PagedListAccessibleAsync(ProviderContext context, string? search, int page, int perPage, CancellationToken cancellationToken)
    {
        // Three deterministic fixtures, optional search-by-substring + slice into one page.
        IEnumerable<RemoteRepository> all = new[]
        {
            BuildRemoteRepo("id-acme-api", "acme/api"),
            BuildRemoteRepo("id-acme-web", "acme/web"),
            BuildRemoteRepo("id-acme-cli", "acme/cli"),
        };
        if (!string.IsNullOrWhiteSpace(search)) all = all.Where(r => r.FullPath.Contains(search!, StringComparison.OrdinalIgnoreCase));

        var items = all.Skip((page - 1) * perPage).Take(perPage).ToList();
        return Task.FromResult(new RemoteRepositoryPage { Items = items });
    }

    public Task<CredentialProbeResult> ProbeCredentialAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new CredentialProbeResult
    {
        IsValid = true,
        AuthenticatedUserExternalId = "test-user-id",
        AuthenticatedUserName = "Test User"
    });

    // Echoes the acting credential's id back as the review's ExternalId so a test can assert WHICH
    // credential made the write-back call (actor vs connection) without a shared recorder.
    public Task<RemotePullRequestReview> SubmitReviewAsync(ProviderContext context, RemoteRepository repository, int number, PullRequestReviewVerdict verdict, string? body, CancellationToken cancellationToken) =>
        Task.FromResult(new RemotePullRequestReview
        {
            Verdict = verdict,
            ExternalId = context.Credential.Id.ToString(),
            WebUrl = $"https://test.local/{repository.FullPath}/-/reviews/{number}"
        });

    // Echoes the acting credential's id back as the created PR's ExternalId (same trick as the review
    // echo) so a test can assert WHICH credential opened it (actor vs connection). Reflects the input.
    public Task<RemotePullRequest> OpenPullRequestAsync(ProviderContext context, RemoteRepository repository, OpenPullRequestInput input, CancellationToken cancellationToken) =>
        Task.FromResult(new RemotePullRequest
        {
            ExternalId = context.Credential.Id.ToString(),
            Number = 777,
            Title = input.Title,
            State = input.Draft ? PullRequestState.Draft : PullRequestState.Open,
            SourceBranch = input.SourceBranch,
            TargetBranch = input.TargetBranch,
            Body = input.Body,
            CommentsCount = 0,
            CreatedDate = DateTimeOffset.UnixEpoch,
            UpdatedDate = DateTimeOffset.UnixEpoch,
            WebUrl = $"https://test.local/{repository.FullPath}/-/merge_requests/777"
        });

    // Echoes the acting credential's id back as the merge result's Sha so a test can assert WHICH credential merged.
    public Task<RemotePullRequestMergeResult> MergePullRequestAsync(ProviderContext context, RemoteRepository repository, int number, MergePullRequestInput input, CancellationToken cancellationToken) =>
        Task.FromResult(new RemotePullRequestMergeResult { Merged = true, Sha = context.Credential.Id.ToString(), Message = $"merged via {input.Method}" });

    // Echoes the state filter + page + perPage into the single returned issue's ExternalId so a catalog
    // test can assert all three flowed through the service preflight to the provider unchanged.
    public Task<IReadOnlyList<RemoteIssue>> ListIssuesAsync(ProviderContext context, RemoteRepository repository, IssueState? stateFilter, int page, int perPage, CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<RemoteIssue>)new[]
        {
            new RemoteIssue
            {
                ExternalId = $"{stateFilter?.ToString() ?? "All"}-p{page}-pp{perPage}",
                Number = page,
                Title = $"{stateFilter?.ToString() ?? "All"} issue",
                State = stateFilter ?? IssueState.Open,
                CommentsCount = perPage,
                CreatedDate = DateTimeOffset.UnixEpoch,
                WebUrl = $"https://test.local/{repository.FullPath}/-/issues/{page}"
            }
        }.ToList());

    // Fixed totals so a test asserts the counts flow through verbatim (3 open, 5 closed).
    public Task<RemoteIssueCounts> CountIssuesAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken) =>
        Task.FromResult(new RemoteIssueCounts { Open = 3, Closed = 5 });

    // Echoes the number + repo path into the detail so a test asserts the lookup reached the provider.
    public Task<RemoteIssue> GetIssueAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken) =>
        Task.FromResult(new RemoteIssue
        {
            ExternalId = $"issue-{number}",
            Number = number,
            Title = $"Issue {number} in {repository.FullPath}",
            State = IssueState.Open,
            Body = "detail body",
            Assignees = new[] { "mindy" },
            CreatedDate = DateTimeOffset.UnixEpoch,
            WebUrl = $"https://test.local/{repository.FullPath}/-/issues/{number}"
        });

    public Task<IReadOnlyList<RemoteIssueComment>> ListIssueCommentsAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<RemoteIssueComment>)new[]
        {
            new RemoteIssueComment { ExternalId = "c1", Body = $"comment on {number}", AuthorName = "tester", CreatedAt = DateTimeOffset.UnixEpoch, WebUrl = null }
        }.ToList());

    public Task<IReadOnlyList<RemoteIssueEvent>> ListIssueEventsAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<RemoteIssueEvent>)new[]
        {
            new RemoteIssueEvent { ExternalId = "e1", Kind = "closed", Summary = "closed this", ActorLogin = "tester", CreatedDate = DateTimeOffset.UnixEpoch }
        }.ToList());

    // ── IRepositoryInsightsCapability (Code-tab right rail: stats · languages · latest release) ──

    public Task<RemoteRepositoryStats> GetStatsAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken) =>
        Task.FromResult(new RemoteRepositoryStats { CommitCount = 304, BranchCount = 47, TagCount = 20, ReleaseCount = 19, StorageBytes = 491520 });

    public Task<IReadOnlyList<RemoteLanguage>> GetLanguagesAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<RemoteLanguage>)new[] { new RemoteLanguage { Name = "C#", Percent = 100 } }.ToList());

    // Echoes the repo's full path into the tag so a test asserts the release flowed through unchanged.
    public Task<RemoteRelease?> GetLatestReleaseAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken) =>
        Task.FromResult<RemoteRelease?>(new RemoteRelease
        {
            TagName = "3.0.5",
            Name = $"Release for {repository.FullPath}",
            PublishedDate = DateTimeOffset.UnixEpoch,
            WebUrl = $"https://test.local/{repository.FullPath}/-/releases/3.0.5",
            IsPrerelease = false
        });

    // ── IReleaseCatalogCapability (Releases page: releases + tags) — echoes page so a test asserts paging flows through ──

    public Task<IReadOnlyList<RemoteRelease>> ListReleasesAsync(ProviderContext context, RemoteRepository repository, int page, int perPage, CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<RemoteRelease>)new[]
        {
            new RemoteRelease
            {
                TagName = $"3.0.{page}", Name = "Release", Body = "notes", AuthorLogin = "vlvvh",
                PublishedDate = DateTimeOffset.UnixEpoch, WebUrl = $"https://test.local/{repository.FullPath}/-/releases/3.0.{page}",
                IsLatest = page == 1, IsPrerelease = false,
                Assets = new[] { new RemoteReleaseAsset { Name = "Source code (zip)", DownloadUrl = "https://test.local/x.zip", SizeBytes = null } }.ToList()
            }
        }.ToList());

    public Task<IReadOnlyList<RemoteTag>> ListTagsAsync(ProviderContext context, RemoteRepository repository, int page, int perPage, CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<RemoteTag>)new[]
        {
            new RemoteTag { Name = $"3.0.{page}", CommitSha = "abc1234", Message = null, WebUrl = $"https://test.local/{repository.FullPath}/-/tags/3.0.{page}" }
        }.ToList());

    // Echoes the acting credential's id back as the created issue's ExternalId so a test can assert WHICH
    // credential created it (actor vs connection). Reflects the input title/body/labels.
    public Task<RemoteIssue> CreateIssueAsync(ProviderContext context, RemoteRepository repository, CreateIssueInput input, CancellationToken cancellationToken) =>
        Task.FromResult(new RemoteIssue
        {
            ExternalId = context.Credential.Id.ToString(),
            Number = 555,
            Title = input.Title,
            State = IssueState.Open,
            Body = input.Body,
            Labels = input.Labels.Select(n => new LabelRef { Name = n, Color = null }).ToList(),
            CreatedDate = DateTimeOffset.UnixEpoch,
            WebUrl = $"https://test.local/{repository.FullPath}/-/issues/555"
        });

    // Echoes the acting credential's id back as the comment's ExternalId so a test can assert WHICH
    // credential commented (actor vs connection). Reflects the input body.
    public Task<RemoteIssueComment> CommentIssueAsync(ProviderContext context, RemoteRepository repository, int number, string body, CancellationToken cancellationToken) =>
        Task.FromResult(new RemoteIssueComment
        {
            ExternalId = context.Credential.Id.ToString(),
            Body = body,
            AuthorName = "tester",
            CreatedAt = DateTimeOffset.UnixEpoch,
            WebUrl = $"https://test.local/{repository.FullPath}/-/issues/{number}#note_1"
        });

    // Echoes the acting credential's id as ExternalId so a test can assert WHICH credential closed it; State=Closed.
    public Task<RemoteIssue> CloseIssueAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken) =>
        Task.FromResult(new RemoteIssue
        {
            ExternalId = context.Credential.Id.ToString(),
            Number = number,
            Title = "closed issue",
            State = IssueState.Closed,
            CreatedDate = DateTimeOffset.UnixEpoch,
            WebUrl = $"https://test.local/{repository.FullPath}/-/issues/{number}"
        });

    // Deterministic, no shared state: the repo's external id encodes the actor's role, so a test drives any
    // pre-flight outcome by seeding a marked repo (no cross-test toggle). "noaccess" → None (denied at the
    // Read floor); "role-<name>" → that role; "inconclusive" → null (probe degrades to allow); everything
    // else (incl. the happy-path fixtures) → Write, which clears the Read floor so existing tests stay green.
    public Task<RepositoryActorAccess> GetActorAccessAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken) =>
        Task.FromResult(repository.ExternalId.Contains("inconclusive", StringComparison.OrdinalIgnoreCase)
            ? RepositoryActorAccess.Inconclusive
            : RepositoryActorAccess.Of(RoleFor(repository.ExternalId)));

    private static RepositoryRole RoleFor(string externalId)
    {
        if (externalId.Contains("noaccess", StringComparison.OrdinalIgnoreCase)) return RepositoryRole.None;
        if (externalId.Contains("role-read", StringComparison.OrdinalIgnoreCase)) return RepositoryRole.Read;
        if (externalId.Contains("role-triage", StringComparison.OrdinalIgnoreCase)) return RepositoryRole.Triage;
        if (externalId.Contains("role-write", StringComparison.OrdinalIgnoreCase)) return RepositoryRole.Write;
        if (externalId.Contains("role-maintain", StringComparison.OrdinalIgnoreCase)) return RepositoryRole.Maintain;
        if (externalId.Contains("role-admin", StringComparison.OrdinalIgnoreCase)) return RepositoryRole.Admin;
        return RepositoryRole.Write;
    }

    public Task<RemoteWebhook?> FindWebhookByCallbackUrlAsync(ProviderContext context, RemoteRepository repository, string callbackUrl, CancellationToken cancellationToken) =>
        Task.FromResult(_hookStore.Find(callbackUrl));

    public Task<RemoteWebhook> RegisterWebhookAsync(ProviderContext context, RemoteRepository repository, WebhookRegistration request, CancellationToken cancellationToken) =>
        Task.FromResult(_hookStore.Register(request));

    public Task DeleteWebhookAsync(ProviderContext context, RemoteRepository repository, string externalWebhookId, CancellationToken cancellationToken) => Task.CompletedTask;

    public bool VerifySignature(string body, IReadOnlyDictionary<string, string> headers, string secret) => true;

    public NormalizedEvent? Normalize(Guid repositoryId, string body, IReadOnlyDictionary<string, string> headers) => null;

    private static RemoteRepository BuildRemoteRepo(string externalId, string fullPath) => new()
    {
        ExternalId = externalId,
        NamespacePath = fullPath.Contains('/') ? fullPath.Substring(0, fullPath.LastIndexOf('/')) : "test-ns",
        Name = fullPath.Contains('/') ? fullPath.Substring(fullPath.LastIndexOf('/') + 1) : fullPath,
        FullPath = fullPath,
        DefaultBranch = "main",
        Visibility = RepositoryVisibility.Private,
        WebUrl = $"https://test.local/{fullPath}"
    };
}
