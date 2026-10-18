using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Services.Providers.Errors;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Core.Services.Providers.Resilience;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;

namespace CodeSpace.UnitTests.Providers.GitLab;

/// <summary>
/// The real <see cref="GitLabRepositoryProvider"/> over a loopback GitLab that answers every route one call below can
/// reach, shared by the tests that read what GitLab is SENT (which header, which URL after a redirect) rather than what it
/// answers. The token is deliberately longer than the 41 characters GitLab 13.0 to 17.7 reads as an OAuth token alone.
/// </summary>
internal static class GitLabLoopback
{
    public const string Token = "glpat-LOOPBACK-MATRIX-0123456789abcdef0123456789abcdef0123456789";

    public const string ProjectPath = "/api/v4/projects/42";

    public static readonly RemoteRepository Repository = new() { ExternalId = "42", NamespacePath = "g", Name = "r", FullPath = "g/r", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "http://gitlab.test/g/r" };

    public static readonly string ProjectJson = """{"id":42,"name":"r","path":"r","path_with_namespace":"g/r","namespace":{"id":7,"name":"g","path":"g","kind":"group","full_path":"g"},"default_branch":"main","visibility":"private","web_url":"http://gitlab.test/g/r","http_url_to_repo":"http://gitlab.test/g/r.git","archived":false}""";

    /// <summary>A GitLab that answers each route a provider call below sends — and 501 for any other.</summary>
    public static StubProviderHost Start() => new StubProviderHost().Answer("GET", "/api/v4/", Answer).Answer("POST", "/api/v4/", Answer);

    public static StubReply Answer(RecordedRequest request)
    {
        var path = request.PathAndQuery.Split('?')[0];

        return (request.Method, path) switch
        {
            ("GET", ProjectPath) => new StubReply(200, ProjectJson),
            ("GET", ProjectPath + "/members/all/7") => new StubReply(200, """{"id":7,"access_level":30}"""),
            ("GET", ProjectPath + "/repository/commits") => new StubReply(200, "[]"),
            ("GET", ProjectPath + "/languages") => new StubReply(200, "{}"),
            ("GET", ProjectPath + "/issues") => new StubReply(200, "[]"),
            ("GET", "/api/v4/user") => new StubReply(200, """{"id":7,"username":"nacho"}"""),
            ("GET", "/api/v4/personal_access_tokens/self") => new StubReply(200, """{"scopes":["api"]}"""),
            ("GET", "/api/v4/groups/acme%2Fplatform/hooks") => new StubReply(200, "[]"),
            ("POST", ProjectPath + "/hooks") => new StubReply(201, """{"id":9}"""),
            ("POST", "/api/v4/markdown") => new StubReply(200, """{"html":"<p>x</p>"}"""),
            ("POST", ProjectPath + "/merge_requests/7/unapprove") => new StubReply(201, "{}"),
            _ => new StubReply(501, """{"message":"no stub configured for this route"}""")
        };
    }

    /// <summary>
    /// Every provider call that reaches GitLab, by the path it takes: NGitLab's typed client, NGitLab's raw paged reader
    /// (a second construction site), and each of the raw HTTP calls. The probe makes one of each.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Func<GitLabRepositoryProvider, ProviderContext, Task>> Operations = new Dictionary<string, Func<GitLabRepositoryProvider, ProviderContext, Task>>
    {
        ["ngitlab: project by id"] = (p, c) => p.GetByExternalIdAsync(c, "42", CancellationToken.None),
        ["ngitlab: paged issue list"] = (p, c) => p.ListIssuesAsync(c, Repository, null, 1, 20, CancellationToken.None),
        ["raw: member standing"] = (p, c) => p.GetMemberStandingAsync(c, Repository, "7", CancellationToken.None),
        ["raw: latest commit"] = (p, c) => p.GetLatestCommitAsync(c, Repository, null, null, CancellationToken.None),
        ["raw: languages"] = (p, c) => p.GetLanguagesAsync(c, Repository, CancellationToken.None),
        ["raw: markdown render"] = (p, c) => p.RenderMarkdownAsync(c, Repository, "# hi", CancellationToken.None),
        ["raw: actor access"] = (p, c) => p.GetActorAccessAsync(c, Repository, CancellationToken.None),
        ["raw: group hook list"] = (p, c) => p.FindConnectionWebhookByCallbackUrlAsync(c, "acme/platform", "https://codespace.test/hook", CancellationToken.None),
        ["raw: project hook create"] = (p, c) => p.RegisterWebhookAsync(c, Repository, new WebhookRegistration { CallbackUrl = "https://codespace.test/hook", Secret = "whsec", SubscribedEvents = ["Push Hook"] }, CancellationToken.None),
        ["raw: unapprove"] = (p, c) => p.SubmitReviewAsync(c, Repository, 7, new SubmitPullRequestReviewInput { Verdict = PullRequestReviewVerdict.RequestChanges, Body = "no" }, CancellationToken.None),
    };

    /// <summary>What GitLab was sent for <paramref name="type"/>: an OAuth token as a Bearer alone, any access token as PRIVATE-TOKEN alone.</summary>
    public static void ShouldCarryOnly(RecordedRequest request, AuthType type)
    {
        var call = $"{request.Method} {request.PathAndQuery}";

        if (type == AuthType.OAuth)
        {
            request.AuthorizationHeader.ShouldBe($"Bearer {Token}", $"{call}: an OAuth token travels as a Bearer");
            request.PrivateTokenHeader.ShouldBeNull($"{call}: an OAuth token is never also a PRIVATE-TOKEN");
            return;
        }

        request.PrivateTokenHeader.ShouldBe(Token, $"{call}: an access token travels as PRIVATE-TOKEN");
        request.AuthorizationHeader.ShouldBeNull($"{call}: an access token is never also a Bearer — GitLab 13.0 to 17.7 reads a long one as OAuth alone");
    }

    public static ProviderContext Context(string baseUrl, AuthType type) => new(new ProviderInstance { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Provider = ProviderKind.GitLab, DisplayName = "loopback", BaseUrl = baseUrl }, new Credential { Id = Guid.NewGuid(), AuthType = type, DisplayName = "token", EncryptedPayload = "unused" });

    public static GitLabRepositoryProvider Provider(IProviderAuthResolver? auth = null)
    {
        var resilience = new ExternalCallResilience(new ProviderErrorMapperRegistry(new IProviderErrorMapper[] { new GitLabErrorMapper() }), NullLogger<ExternalCallResilience>.Instance);
        var normalizer = new GitLabEventNormalizer(new ProviderEventSubscriptionRegistry(Array.Empty<IProviderEventSubscription>()));

        return new GitLabRepositoryProvider(auth ?? new StaticTokenAuth(), resilience, new GitLabSignatureVerifier(), normalizer, new GitLabWebhookRepositoryIdentifier());
    }

    private sealed class StaticTokenAuth : IProviderAuthResolver
    {
        public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new ResolvedAuth { Token = Token });
    }
}
