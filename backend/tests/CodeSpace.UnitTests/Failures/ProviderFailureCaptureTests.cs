using CodeSpace.Core.Failures;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Failures;

/// <summary>
/// Which thrown exceptions become a persisted provider failure, and what they carry. Only a declared failure that a person can
/// repair outside the run is written down; everything else stays the sentence it was.
/// </summary>
[Trait("Category", "Unit")]
public class ProviderFailureCaptureTests
{
    private static readonly Guid Credential = Guid.NewGuid();
    private static readonly Guid Repository = Guid.NewGuid();

    [Fact]
    public void A_disconnected_credential_is_captured_with_the_credential_it_names()
    {
        var failure = ProviderFailureCapture.From(new CredentialDisconnectedException(Credential, "GitLab · nacho"), Repository);

        failure.ShouldNotBeNull();
        failure.Code.ShouldBe(FailureCodes.CredentialDisconnected);
        failure.DetailGuid("credentialId").ShouldBe(Credential);
        failure.RepositoryId.ShouldBe(Repository, "the repository the failing step was acting on");
    }

    [Fact]
    public void A_credential_its_provider_refused_is_captured_with_its_name_and_id()
    {
        var failure = ProviderFailureCapture.From(new CredentialNeedsReconnectException(Credential, "GitLab · nacho", "invalid_grant"), repositoryId: null);

        failure!.Code.ShouldBe(FailureCodes.CredentialNeedsReconnect);
        failure.DetailGuid("credentialId").ShouldBe(Credential);
        failure.Detail("credentialName").ShouldBe("GitLab · nacho");
        failure.RepositoryId.ShouldBeNull();
    }

    [Fact]
    public void A_provider_401_is_captured_without_a_credential_and_traceable_through_the_repository()
    {
        var failure = ProviderFailureCapture.From(new ProviderApiException(ProviderKind.GitLab, 401, "ListPullRequestsAsync", "401 Unauthorized", new Exception("inner")), Repository);

        failure!.Code.ShouldBe(FailureCodes.ProviderUnauthorized);
        failure.Detail("providerStatus").ShouldBe("401");
        failure.Detail("provider").ShouldBe("GitLab");
        failure.DetailGuid("credentialId").ShouldBeNull("a provider's answer names no credential; the repository does");
        failure.RepositoryId.ShouldBe(Repository);
    }

    [Fact]
    public void A_provider_answer_that_is_not_a_credential_refusal_is_not_captured()
    {
        ProviderFailureCapture.From(new ProviderApiException(ProviderKind.GitLab, 500, "ListPullRequestsAsync", "boom", new Exception("inner")), Repository).ShouldBeNull("a 5xx is the provider's outage, not a connection a person repairs");
    }

    [Fact]
    public void A_redirecting_provider_is_captured_with_where_it_pointed()
    {
        var failure = ProviderFailureCapture.From(new ProviderBaseUrlRedirectsException(ProviderKind.GitLab, new Uri("http://gitlab.example.com/api/v4/user"), new Uri("https://gitlab.example.com/api/v4/user")), repositoryId: null);

        failure!.Code.ShouldBe(FailureCodes.ProviderBaseUrlRedirects);
        failure.Detail("requestedOrigin").ShouldBe("http://gitlab.example.com");
        failure.Detail("finalOrigin").ShouldBe("https://gitlab.example.com");
        failure.Detail("finalUrl").ShouldBe("https://gitlab.example.com/api/v4/user");
    }

    [Fact]
    public void A_rejected_push_names_its_repository_itself_and_that_wins_over_the_step_it_was_raised_in()
    {
        var own = Guid.NewGuid();
        var failure = ProviderFailureCapture.From(new GitCredentialRejectedException("the remote refused it", own), Repository);

        failure!.Code.ShouldBe(FailureCodes.GitCredentialRejected);
        failure.RepositoryId.ShouldBe(own, "the more specific fact");
    }

    [Fact]
    public void A_rejected_push_caused_by_a_dead_credential_carries_that_credential()
    {
        var inner = new CredentialDisconnectedException(Credential, "GitLab · nacho");

        ProviderFailureCapture.From(new GitCredentialRejectedException("disconnected while the run worked", inner, Repository), null)!.DetailGuid("credentialId").ShouldBe(Credential);
    }

    [Fact]
    public void An_oauth_refresh_refusal_names_the_credential_whose_refresh_it_answered()
    {
        var refused = new OAuthExchangeException("http_503", "unavailable", null).ForCredential(Credential);
        var failure = ProviderFailureCapture.From(refused, null);

        failure!.Code.ShouldBe(FailureCodes.OAuthExchangeFailed);
        failure.DetailGuid("credentialId").ShouldBe(Credential);
        failure.Detail("oauthError").ShouldBe("http_503", "what the provider answered — an error code, never the description or the body — so the sentence can say it was an outage and not a refusal");
    }

    [Fact]
    public void An_exchange_before_any_credential_existed_is_captured_with_no_credential()
    {
        ProviderFailureCapture.From(new OAuthExchangeException("invalid_grant", null, null), null)!.Details.ShouldBeNull();
    }

    [Fact]
    public void A_failure_wrapped_in_another_exception_is_still_found()
    {
        var wrapped = new InvalidOperationException("node failed", new AggregateException(new Exception("x"), new CredentialDisconnectedException(Credential, "n")));

        ProviderFailureCapture.From(wrapped, null)!.Code.ShouldBe(FailureCodes.CredentialDisconnected);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(TimeoutException))]
    public void An_exception_that_declares_nothing_about_a_provider_is_not_captured(Type type)
    {
        ProviderFailureCapture.From((Exception)Activator.CreateInstance(type, "x")!, Repository).ShouldBeNull();
    }

    [Fact]
    public void A_declared_failure_that_is_not_a_provider_connection_is_left_as_the_sentence_it_was()
    {
        ProviderFailureCapture.From(new ActorIdentityRequiredException(ProviderKind.GitLab, Guid.NewGuid()), Repository).ShouldBeNull("it has its own surface — the identity gate");
    }

    [Fact]
    public void Nothing_thrown_is_nothing_captured()
    {
        ProviderFailureCapture.From(null, Repository).ShouldBeNull();
    }

    [Theory]
    [InlineData(FailureCodes.CredentialDisconnected)]
    [InlineData(FailureCodes.CredentialNeedsReconnect)]
    [InlineData(FailureCodes.ProviderUnauthorized)]
    [InlineData(FailureCodes.GitCredentialRejected)]
    [InlineData(FailureCodes.ProviderBaseUrlRedirects)]
    [InlineData(FailureCodes.OAuthExchangeFailed)]
    public void A_recorded_provider_connection_failure_is_kept_exactly_as_recorded(string code)
    {
        var recorded = new PersistedFailure { Code = code, RepositoryId = Repository, Origin = new FailureOrigin { RunId = Guid.NewGuid(), NodeId = "list" } };

        ProviderFailureCapture.OfRecord(recorded).ShouldBeSameAs(recorded);
    }

    [Theory]
    [InlineData(FailureCodes.Internal)]
    [InlineData(FailureCodes.Forbidden)]
    [InlineData("not_a_code")]
    public void A_recorded_failure_that_is_not_a_provider_connection_is_not_kept_and_nothing_is_nothing(string code)
    {
        ProviderFailureCapture.OfRecord(new PersistedFailure { Code = code }).ShouldBeNull("what is carried onward is never wider than what is captured");
        ProviderFailureCapture.OfRecord(null).ShouldBeNull();
    }
}
