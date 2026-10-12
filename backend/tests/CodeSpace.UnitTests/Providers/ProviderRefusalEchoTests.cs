using System.Text.Json;
using CodeSpace.Core.Failures;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Services.Providers.Errors;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Core.Services.Providers.Resilience;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;

namespace CodeSpace.UnitTests.Providers;

/// <summary>
/// The provider calls that read a refusal's body THEMSELVES — GitLab's raw <c>/unapprove</c>, the project and group hook
/// creates, the hook-list probes, GitHub's hook creates — must strike the token they presented before that body becomes
/// an exception message or a webhook-attempt diagnostic. Those land in <c>external_call.failed</c>,
/// <c>repository_webhook.last_error</c> and <c>*_webhook_attempt.response_body</c>, which every member of the team can
/// read; a provider (or a proxy in front of it) that quotes the <c>PRIVATE-TOKEN</c> / <c>Authorization</c> it rejected
/// would otherwise hand the token to all of them. The resilience translation never sees these bodies, so it cannot be
/// the one to strike them.
///
/// <para>Driven through the real providers, the real auth resolver and the real resilience layer against a loopback
/// provider that echoes the presented credential headers into every refusal.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class ProviderRefusalEchoTests : IDisposable
{
    private const string GitLabToken = "glpat-REFUSAL-ECHO-CANARY-1a2b3c4d";
    private const string GitHubToken = "ghp_REFUSALECHOCANARY5e6f7a8b9c";

    private readonly StubProviderHost _forge = new();

    public void Dispose() => _forge.Dispose();

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task A_refused_unapprove_never_carries_the_presented_token(int status)
    {
        _forge.Answer("POST", "/merge_requests/7/unapprove", request => Echo(status, request));

        var refusal = await Should.ThrowAsync<ProviderApiException>(() => GitLab().SubmitReviewAsync(Context(ProviderKind.GitLab, GitLabToken), Repository, 7, RequestChanges, CancellationToken.None));

        PresentedTheToken(GitLabToken);
        refusal.StatusCode.ShouldBe(status);
        refusal.ProviderMessage.ShouldContain($"{status} refused", Case.Sensitive, "GitLab's own words stay legible around the struck token");
        ShouldCarryNoToken(refusal, GitLabToken);
    }

    [Fact]
    public async Task A_scope_refused_unapprove_never_carries_the_presented_token_in_its_hint()
    {
        _forge.Answer("POST", "/merge_requests/7/unapprove", request => new StubReply(403, JsonSerializer.Serialize(new { error = "insufficient_scope", scope = "api", error_description = $"refused PRIVATE-TOKEN={request.PrivateTokenHeader}" })));

        var refusal = await Should.ThrowAsync<ProviderInsufficientScopeException>(() => GitLab().SubmitReviewAsync(Context(ProviderKind.GitLab, GitLabToken), Repository, 7, RequestChanges, CancellationToken.None));

        PresentedTheToken(GitLabToken);
        refusal.MissingScopes.ShouldBe(["api"], "the scope is still read off the provider's answer");
        refusal.ProviderHint.ShouldNotBeNull().ShouldNotContain(GitLabToken);
        ShouldCarryNoToken(refusal, GitLabToken);
    }

    [Fact]
    public async Task A_refused_gitlab_project_hook_create_never_carries_the_presented_token()
    {
        _forge.Answer("GET", "/api/v4/projects/4242/hooks", 200, "[]").Answer("POST", "/api/v4/projects/4242/hooks", request => Echo(403, request));

        var refusal = await Should.ThrowAsync<ProviderWebhookRegistrationException>(() => GitLab().RegisterWebhookAsync(Context(ProviderKind.GitLab, GitLabToken), Repository, Hook, CancellationToken.None));

        PresentedTheToken(GitLabToken);
        refusal.Message.ShouldContain("403", Case.Sensitive, "the status the provider answered stays in the error");
        ShouldCarryNoToken(refusal, GitLabToken);
    }

    [Fact]
    public async Task A_refused_gitlab_project_hook_list_never_carries_the_presented_token()
    {
        _forge.Answer("GET", "/api/v4/projects/4242/hooks", request => Echo(403, request));

        var refusal = await Should.ThrowAsync<ProviderWebhookRegistrationException>(() => GitLab().FindWebhookByCallbackUrlAsync(Context(ProviderKind.GitLab, GitLabToken), Repository, Hook.CallbackUrl, CancellationToken.None));

        PresentedTheToken(GitLabToken);
        refusal.Diagnostic.StatusCode.ShouldBe(403);
        refusal.Diagnostic.ResponseBody.ShouldNotBeNull().ShouldContain("403 refused", Case.Sensitive, "GitLab's answer is still the evidence");
        ShouldCarryNoToken(refusal, GitLabToken);
    }

    [Theory]
    [InlineData(false, 403)]
    [InlineData(false, 404)]
    [InlineData(true, 401)]
    public async Task A_refused_gitlab_group_hook_call_never_carries_the_presented_token(bool list, int status)
    {
        _forge.Answer(list ? "GET" : "POST", "/api/v4/groups/acme%2Fplatform/hooks", request => Echo(status, request)).Answer("GET", "/api/v4/groups/acme%2Fplatform/hooks", 200, "[]");

        var context = Context(ProviderKind.GitLab, GitLabToken);
        Task Call() => list ? GitLab().FindConnectionWebhookByCallbackUrlAsync(context, "acme/platform", Hook.CallbackUrl, CancellationToken.None) : (Task)GitLab().RegisterConnectionWebhookAsync(context, "acme/platform", Hook, CancellationToken.None);
        var refusal = await Should.ThrowAsync<Exception>(Call);

        PresentedTheToken(GitLabToken);
        var evidence = Diagnosed(refusal).ShouldNotBeNull("a group-hook refusal carries its evidence down the chain");
        evidence.Diagnostic.StatusCode.ShouldBe(status);
        evidence.Diagnostic.ResponseBody.ShouldNotBeNull().ShouldContain($"{status} refused", Case.Sensitive, "GitLab's own words are the evidence for the refusal");
        ShouldCarryNoToken(refusal, GitLabToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_refused_github_hook_create_never_carries_the_presented_token(bool organization)
    {
        var hooks = organization ? "/orgs/acme/hooks" : "/repositories/4242/hooks";
        _forge.Answer("POST", hooks, request => Echo(403, request)).Answer("GET", hooks, request => Echo(403, request));

        var context = Context(ProviderKind.GitHub, GitHubToken);
        var refusal = await Should.ThrowAsync<ProviderWebhookRegistrationException>(() => organization ? GitHub().RegisterConnectionWebhookAsync(context, "acme", Hook, CancellationToken.None) : GitHub().RegisterWebhookAsync(context, Repository, Hook, CancellationToken.None));

        PresentedTheToken(GitHubToken);
        refusal.Diagnostic.StatusCode.ShouldBe(403);
        refusal.Diagnostic.ResponseBody.ShouldNotBeNull().ShouldContain("403 refused", Case.Sensitive, "GitHub's answer is still the evidence");
        ShouldCarryNoToken(refusal, GitHubToken);
    }

    /// <summary>Fixture check: the provider really was presented the token, so its refusal really quoted it.</summary>
    private void PresentedTheToken(string token) =>
        _forge.Requests.ShouldContain(request => (request.AuthorizationHeader ?? "").Contains(token) || (request.PrivateTokenHeader ?? "").Contains(token), "fixture check: the token was presented, so the refusal echoed it");

    /// <summary>
    /// Every surface the failure reaches a reader through: the thrown message (what <c>last_error</c>, the attempt's
    /// <c>error</c> and <c>external_call.failed</c> persist), each typed failure down the chain, the classified client
    /// message and details, and the webhook-attempt diagnostic. The SDK's own exception stays the innermost one, raw, for
    /// the operator log — it is never rendered to a reader, so it is not a surface here.
    /// </summary>
    private static void ShouldCarryNoToken(Exception refusal, string token)
    {
        refusal.Message.ShouldNotContain(token, Case.Sensitive, $"{refusal.GetType().Name}.Message");

        for (var current = refusal.InnerException; current != null; current = current.InnerException)
        {
            if (current is IFailure) current.Message.ShouldNotContain(token, Case.Sensitive, $"{current.GetType().Name}.Message");
        }

        var classified = FailureClassifier.Classify(refusal);
        classified.ClientMessage.ShouldNotContain(token, Case.Sensitive);
        (classified.Details?.Values ?? []).ShouldAllBe(value => value == null || !value.ToString()!.Contains(token));

        if (Diagnosed(refusal) is not { } evidence) return;

        evidence.Diagnostic.ResponseBody?.ShouldNotContain(token, Case.Sensitive);
        evidence.Diagnostic.Request?.Url.ShouldNotContain(token, Case.Sensitive);
        JsonSerializer.Serialize(evidence.Diagnostic.Request?.Headers).ShouldNotContain(token, Case.Sensitive);
    }

    private static ProviderWebhookRegistrationException? Diagnosed(Exception refusal)
    {
        for (var current = refusal; current != null; current = current.InnerException)
        {
            if (current is ProviderWebhookRegistrationException diagnosed) return diagnosed;
        }

        return null;
    }

    private static StubReply Echo(int status, RecordedRequest request) => new(status, JsonSerializer.Serialize(new { message = $"{status} refused (PRIVATE-TOKEN={request.PrivateTokenHeader}; Authorization={request.AuthorizationHeader})" }));

    private ProviderContext Context(ProviderKind kind, string token)
    {
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Provider = kind, DisplayName = "loopback", BaseUrl = _forge.BaseUrl, ApiUrl = kind == ProviderKind.GitHub ? _forge.BaseUrl : null };
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = instance.TeamId, ProviderInstanceId = instance.Id, AuthType = AuthType.Pat, DisplayName = "pat", EncryptedPayload = "unused", Status = CredentialStatus.Active };

        _tokens[kind] = token;

        return new ProviderContext(instance, credential);
    }

    private readonly Dictionary<ProviderKind, string> _tokens = new();

    private ProviderAuthResolver Resolver() => new([new FixedToken(ProviderKind.GitLab, () => _tokens[ProviderKind.GitLab]), new FixedToken(ProviderKind.GitHub, () => _tokens[ProviderKind.GitHub])]);

    private GitLabRepositoryProvider GitLab()
    {
        var resilience = new ExternalCallResilience(new ProviderErrorMapperRegistry(new IProviderErrorMapper[] { new GitLabErrorMapper() }), NullLogger<ExternalCallResilience>.Instance);

        return new GitLabRepositoryProvider(Resolver(), resilience, new GitLabSignatureVerifier(), new GitLabEventNormalizer(new ProviderEventSubscriptionRegistry(Array.Empty<IProviderEventSubscription>())), new GitLabWebhookRepositoryIdentifier());
    }

    private GitHubRepositoryProvider GitHub()
    {
        var resilience = new ExternalCallResilience(new ProviderErrorMapperRegistry(new IProviderErrorMapper[] { new GitHubErrorMapper() }), NullLogger<ExternalCallResilience>.Instance);

        return new GitHubRepositoryProvider(Resolver(), resilience, new GitHubSignatureVerifier(), new GitHubEventNormalizer(new ProviderEventSubscriptionRegistry(Array.Empty<IProviderEventSubscription>())), new GitHubWebhookRepositoryIdentifier());
    }

    private static readonly SubmitPullRequestReviewInput RequestChanges = new() { Verdict = PullRequestReviewVerdict.RequestChanges, Body = "needs work" };

    private static readonly WebhookRegistration Hook = new() { CallbackUrl = "https://codespace.test/api/webhooks/0f5e2c7a-9b1d-4e3f-8a6b-2c4d6e8f0a1b", Secret = "whsec-loopback-0000", SubscribedEvents = ["push"] };

    private static readonly RemoteRepository Repository = new() { ExternalId = "4242", NamespacePath = "acme", Name = "api", FullPath = "acme/api", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://forge.test/acme/api" };

    private sealed class FixedToken(ProviderKind kind, Func<string> token) : IProviderAuthStrategy
    {
        public ProviderKind Kind => kind;

        public AuthType AuthType => AuthType.Pat;

        public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new ResolvedAuth { Token = token() });
    }
}
