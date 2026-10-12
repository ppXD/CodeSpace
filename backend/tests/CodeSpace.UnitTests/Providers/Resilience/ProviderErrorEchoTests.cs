using System.Net;
using CodeSpace.Core.Failures;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Services.Providers.Errors;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Core.Services.Providers.Resilience;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Resilience;

/// <summary>
/// A git provider (or a proxy in front of it) that ECHOES the token it was presented must never get that token into the
/// typed failure a caller — a Viewer on a repository page, a git.* node's error — is shown. The SDK-to-
/// <see cref="ProviderApiException"/> translation strikes every token the auth resolver handed out for the instance,
/// and drops the SDK's "Original call: &lt;url&gt;" suffix from the message the client reads.
/// </summary>
[Trait("Category", "Unit")]
public class ProviderErrorEchoTests
{
    private const string Token = "glpat-ECHO-CANARY-5e6f7a8b9c";

    public enum Sdk { DuckTyped, GitLab, GitHub }

    [Theory]
    [InlineData(Sdk.DuckTyped)]
    [InlineData(Sdk.GitLab)]
    [InlineData(Sdk.GitHub)]
    public async Task An_echoed_token_never_reaches_the_translated_failure(Sdk sdk)
    {
        var (instance, resilience) = await ResolvedInstanceAsync(ProviderKind.GitLab, Token);

        var ex = await Should.ThrowAsync<ProviderApiException>(() => resilience.ExecuteAsync<int>(instance, "ListBranchesAsync", _ => throw Echo(sdk, HttpStatusCode.NotFound), CancellationToken.None));

        ex.StatusCode.ShouldBe(404);
        ShouldCarryNoToken(ex.Message);
        ShouldCarryNoToken(ex.ProviderMessage);
        ShouldCarryNoToken(FailureClassifier.Classify(ex).ClientMessage);
        ex.ProviderMessage.ShouldContain("Project Not Found", Case.Sensitive, "the provider's own words stay legible around the struck token");
    }

    [Fact]
    public async Task An_echoed_token_never_reaches_an_insufficient_scope_hint()
    {
        var (instance, resilience) = await ResolvedInstanceAsync(ProviderKind.GitLab, Token, new GitLabErrorMapper());
        var echo = new NGitLab.GitLabException($"GitLab server returned an error (Forbidden): insufficient_scope (PRIVATE-TOKEN={Token}). Original call: Post http://127.0.0.1/api/v4/projects/42/approve") { StatusCode = HttpStatusCode.Forbidden };

        var ex = await Should.ThrowAsync<ProviderInsufficientScopeException>(() => resilience.ExecuteAsync<int>(instance, "SubmitReviewAsync", _ => throw echo, CancellationToken.None));

        ex.MissingScopes.ShouldBe(["api"], "the scope classification reads the raw SDK failure and is unchanged");
        ShouldCarryNoToken(ex.ProviderHint);
        ShouldCarryNoToken(FailureClassifier.Classify(ex).Details!["providerHint"] as string);
    }

    [Fact]
    public async Task An_instance_whose_token_was_never_resolved_keeps_the_providers_words_without_the_original_call()
    {
        var resilience = Resilience();
        var instance = Instance(ProviderKind.GitHub);

        var ex = await Should.ThrowAsync<ProviderApiException>(() => resilience.ExecuteAsync<int>(instance, "GetStatsAsync", _ => throw new Octokit.ApiException("Not Found. Original call: Get https://api.github.com/repos/acme/web", HttpStatusCode.NotFound), CancellationToken.None));

        ex.ProviderMessage.ShouldBe("Not Found.");
    }

    [Fact]
    public async Task A_token_resolved_for_one_instance_object_does_not_mask_another()
    {
        var (_, resilience) = await ResolvedInstanceAsync(ProviderKind.GitLab, Token);
        var other = Instance(ProviderKind.GitLab);

        var ex = await Should.ThrowAsync<ProviderApiException>(() => resilience.ExecuteAsync<int>(other, "ListBranchesAsync", _ => throw Echo(Sdk.DuckTyped, HttpStatusCode.NotFound), CancellationToken.None));

        ex.ProviderMessage.ShouldContain(Token, Case.Sensitive, "fixture check: nothing was resolved for this instance, so nothing is struck — the rule is per presentation, not a global word list");
    }

    private static void ShouldCarryNoToken(string? text)
    {
        text.ShouldNotBeNull();
        text.ShouldNotContain(Token);
        text.ShouldNotContain("Original call");
    }

    /// <summary>The real auth resolver hands the instance its token — the one step every provider call takes before it reaches the resilience layer.</summary>
    private static async Task<(ProviderInstance Instance, ExternalCallResilience Resilience)> ResolvedInstanceAsync(ProviderKind kind, string token, IProviderErrorMapper? mapper = null)
    {
        var instance = Instance(kind);
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = instance.TeamId, ProviderInstanceId = instance.Id, AuthType = AuthType.Pat, DisplayName = "pat", EncryptedPayload = "", Status = CredentialStatus.Active };
        var resolver = new ProviderAuthResolver([new FixedTokenStrategy(kind, token)]);

        (await resolver.ResolveAsync(new ProviderContext(instance, credential), CancellationToken.None)).Token.ShouldBe(token, "fixture check: the resolver handed out the token");

        return (instance, Resilience(mapper));
    }

    private static ExternalCallResilience Resilience(IProviderErrorMapper? mapper = null) => new(new SingleMapperRegistry(mapper), NullLogger<ExternalCallResilience>.Instance);

    private static ProviderInstance Instance(ProviderKind kind) => new() { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Provider = kind, DisplayName = "echo", BaseUrl = "http://127.0.0.1" };

    private static Exception Echo(Sdk sdk, HttpStatusCode status)
    {
        var message = $"GitLab server returned an error ({status}): 404 Project Not Found (PRIVATE-TOKEN=; Authorization=Bearer {Token}). Original call: Get http://127.0.0.1/api/v4/projects/42/repository/branches?private_token={Token}";

        return sdk switch
        {
            Sdk.GitLab => new NGitLab.GitLabException(message) { StatusCode = status },
            Sdk.GitHub => new Octokit.ApiException(message, status),
            _ => new DuckTypedSdkException(message, (int)status),
        };
    }

    private sealed class DuckTypedSdkException(string message, int statusCode) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
    }

    private sealed class FixedTokenStrategy(ProviderKind kind, string token) : IProviderAuthStrategy
    {
        public ProviderKind Kind => kind;

        public AuthType AuthType => AuthType.Pat;

        public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new ResolvedAuth { Token = token });
    }

    private sealed class SingleMapperRegistry(IProviderErrorMapper? mapper) : IProviderErrorMapperRegistry
    {
        public IProviderErrorMapper? Get(ProviderKind kind) => mapper?.Kind == kind ? mapper : null;
    }
}
