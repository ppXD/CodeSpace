using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;
using static CodeSpace.UnitTests.Providers.GitLab.GitLabLoopback;

namespace CodeSpace.UnitTests.Providers.GitLab;

/// <summary>
/// What a probe tells the caller about a credential: who it is and what it may do, now also when a static token expires and
/// whether GitLab calls it active — and, apart from "not valid", whether GitLab ANSWERED and refused it. Only a 401 is
/// evidence against a credential; a 5xx or a dropped connection says nothing about it, and the health sweep must not mark a
/// working token as dead because GitLab had a bad minute.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitLabProbeTests : IDisposable
{
    private const string User = """{"id":7,"username":"nacho"}""";

    private readonly List<IDisposable> _gitlabs = new();

    public void Dispose()
    {
        foreach (var gitlab in _gitlabs) gitlab.Dispose();
    }

    [Fact]
    public async Task A_live_token_reports_its_user_scopes_expiry_and_that_it_is_active()
    {
        var gitlab = Gitlab(user: new StubReply(200, User), self: new StubReply(200, """{"scopes":["api","read_user"],"expires_at":"2030-07-04","active":true,"revoked":false}"""));

        var probe = await Provider().ProbeCredentialAsync(Context(gitlab.BaseUrl, AuthType.Pat), CancellationToken.None);

        probe.IsValid.ShouldBeTrue();
        probe.IsRejected.ShouldBeFalse();
        probe.AuthenticatedUserExternalId.ShouldBe("7");
        probe.AuthenticatedUserName.ShouldBe("nacho");
        probe.GrantedScopes.ShouldBe(new[] { "api", "read_user" });
        probe.ExpiresAt.ShouldBe(new DateTimeOffset(2030, 7, 4, 0, 0, 0, TimeSpan.Zero));
        probe.Active.ShouldBe(true);
    }

    [Fact]
    public async Task A_token_that_never_expires_is_described_with_no_expiry()
    {
        var gitlab = Gitlab(user: new StubReply(200, User), self: new StubReply(200, """{"scopes":["api"],"expires_at":null,"active":true}"""));

        var probe = await Provider().ProbeCredentialAsync(Context(gitlab.BaseUrl, AuthType.Pat), CancellationToken.None);

        probe.IsValid.ShouldBeTrue();
        probe.ExpiresAt.ShouldBeNull();
        probe.Active.ShouldBe(true, "described, so null means none rather than unknown");
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(401)]
    public async Task A_token_the_self_endpoint_will_not_describe_is_valid_with_nothing_known_about_its_expiry(int selfStatus)
    {
        // OAuth and impersonation tokens, and GitLab versions without the endpoint for project and group tokens.
        var gitlab = Gitlab(user: new StubReply(200, User), self: new StubReply(selfStatus, """{"message":"no"}"""));

        var probe = await Provider().ProbeCredentialAsync(Context(gitlab.BaseUrl, AuthType.Pat), CancellationToken.None);

        probe.IsValid.ShouldBeTrue();
        probe.ExpiresAt.ShouldBeNull();
        probe.Active.ShouldBeNull("not described: the caller must not read this as 'never expires'");
        probe.GrantedScopes.ShouldBeNull();
    }

    [Theory]
    [InlineData(AuthType.Pat)]
    [InlineData(AuthType.ProjectAccessToken)]
    [InlineData(AuthType.GroupAccessToken)]
    public async Task A_401_is_a_rejection(AuthType type)
    {
        var gitlab = Gitlab(user: new StubReply(401, """{"message":"401 Unauthorized"}"""), self: new StubReply(401, "{}"));

        var probe = await Provider().ProbeCredentialAsync(Context(gitlab.BaseUrl, type), CancellationToken.None);

        probe.IsValid.ShouldBeFalse();
        probe.IsRejected.ShouldBeTrue("GitLab answered, and refused the token");
        probe.Error.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    public async Task A_refusal_that_is_not_about_the_token_is_not_a_rejection(int status)
    {
        var gitlab = Gitlab(user: new StubReply(status, """{"message":"nope"}"""), self: new StubReply(200, "{}"));

        var probe = await Provider().ProbeCredentialAsync(Context(gitlab.BaseUrl, AuthType.Pat), CancellationToken.None);

        probe.IsValid.ShouldBeFalse();
        probe.IsRejected.ShouldBeFalse("a 403 is a missing scope or role — the token is not dead, and marking it so would stop every run through it");
    }

    [Fact]
    public async Task A_403_tagged_insufficient_scope_names_the_scope_and_is_not_a_rejection_of_the_token()
    {
        var gitlab = Gitlab(user: new StubReply(403, """{"error":"insufficient_scope","error_description":"The request requires higher privileges than provided by the access token.","scope":"read_user"}"""), self: new StubReply(200, "{}"));

        var probe = await Provider().ProbeCredentialAsync(Context(gitlab.BaseUrl, AuthType.Pat), CancellationToken.None);

        probe.IsValid.ShouldBeFalse();
        probe.IsRejected.ShouldBeFalse("the token works; it lacks a scope, which is what the person is told");
        probe.Error.ShouldNotBeNull().ShouldContain("read_user");
    }

    [Theory]
    [InlineData("""<html>login</html>""")]
    [InlineData("""{"id":"seven","username":"nacho"}""")]
    [InlineData("""{"id":7}""")]
    [InlineData("""[]""")]
    public async Task An_answer_that_is_not_a_user_is_no_answer_about_the_token(string body)
    {
        // A 200 from a proxy's login page says nothing about whether the token works: it is neither a validation nor a rejection.
        var gitlab = Gitlab(user: new StubReply(200, body), self: new StubReply(200, "{}"));

        var probe = await Provider().ProbeCredentialAsync(Context(gitlab.BaseUrl, AuthType.Pat), CancellationToken.None);

        probe.IsValid.ShouldBeFalse();
        probe.IsRejected.ShouldBeFalse();
    }

    [Fact]
    public async Task A_server_error_is_asked_again_before_it_is_given_up_on()
    {
        var calls = 0;
        var gitlab = new StubProviderHost().Answer("GET", "/api/v4/user", _ => ++calls == 1 ? new StubReply(503, """{"message":"maintenance"}""") : new StubReply(200, User)).Answer("GET", "/api/v4/personal_access_tokens/self", _ => new StubReply(200, "{}"));
        _gitlabs.Add(gitlab);

        var probe = await Provider().ProbeCredentialAsync(Context(gitlab.BaseUrl, AuthType.Pat), CancellationToken.None);

        probe.IsValid.ShouldBeTrue("one 503 is a bad second, not a verdict");
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_401_is_not_asked_again()
    {
        var gitlab = Gitlab(user: new StubReply(401, """{"message":"401 Unauthorized"}"""), self: new StubReply(401, "{}"));

        await Provider().ProbeCredentialAsync(Context(gitlab.BaseUrl, AuthType.Pat), CancellationToken.None);

        gitlab.Requests.Count(r => r.PathAndQuery.StartsWith("/api/v4/user", StringComparison.Ordinal)).ShouldBe(1, "a refusal of the token is the answer; asking again would only spend quota on the same one");
    }

    [Fact]
    public async Task A_server_error_is_no_answer_about_the_token()
    {
        var gitlab = Gitlab(user: new StubReply(503, """{"message":"maintenance"}"""), self: new StubReply(200, "{}"));

        var probe = await Provider().ProbeCredentialAsync(Context(gitlab.BaseUrl, AuthType.Pat), CancellationToken.None);

        probe.IsValid.ShouldBeFalse();
        probe.IsRejected.ShouldBeFalse();
    }

    [Fact]
    public async Task A_provider_that_cannot_be_reached_is_no_answer_about_the_token()
    {
        var closed = new StubProviderHost();
        var url = closed.BaseUrl;
        closed.Dispose();

        var probe = await Provider().ProbeCredentialAsync(Context(url, AuthType.Pat), CancellationToken.None);

        probe.IsValid.ShouldBeFalse();
        probe.IsRejected.ShouldBeFalse();
    }

    [Fact]
    public async Task A_provider_that_never_answers_is_given_up_on_when_the_callers_deadline_passes()
    {
        // The identity call used to be NGitLab's synchronous one: it ignored the token, blocked its thread for the HTTP
        // client's five minutes, and retried a connection that never answered up to three times — so a probe's own deadline
        // never took effect, and one such credential held the health sweep's worker for a quarter of an hour.
        using var silent = new SilentProviderHost();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var provider = Provider();
        var context = Context(silent.BaseUrl, AuthType.Pat);

        // On a thread of its own: a probe that ignores the deadline must fail this test, not hang it.
        var probing = Task.Run(() => provider.ProbeCredentialAsync(context, deadline.Token));
        var finished = await Task.WhenAny(probing, Task.Delay(TimeSpan.FromSeconds(15))) == probing;

        finished.ShouldBeTrue("the probe must stop when its deadline passes, not when the HTTP client's own timeout does");

        var probe = await probing;

        probe.IsValid.ShouldBeFalse();
        probe.IsRejected.ShouldBeFalse("no answer says nothing about the token: marking it dead on a deadline would stop every run through it");
        silent.Connections.ShouldBe(1, "a call that was given up on is not sent again");
    }

    [Theory]
    [InlineData("""{"active":false,"expires_at":"2020-01-01"}""")]
    [InlineData("""{"active":true,"revoked":true}""")]
    public async Task A_token_gitlab_calls_inactive_or_revoked_is_rejected_even_when_the_user_call_was_answered(string self)
    {
        var gitlab = Gitlab(user: new StubReply(200, User), self: new StubReply(200, self));

        var probe = await Provider().ProbeCredentialAsync(Context(gitlab.BaseUrl, AuthType.Pat), CancellationToken.None);

        probe.IsValid.ShouldBeFalse();
        probe.IsRejected.ShouldBeTrue();
        probe.Active.ShouldBe(false);
    }

    [Fact]
    public async Task An_OAuth_probe_refused_with_a_stale_token_is_replaced_and_valid()
    {
        var gitlab = new StubProviderHost().Answer("GET", "/api/v4/user", request => request.AuthorizationHeader == "Bearer stale-0123456789" ? new StubReply(401, "{}") : new StubReply(200, User)).Answer("GET", "/api/v4/personal_access_tokens/self", _ => new StubReply(404, "{}"));
        _gitlabs.Add(gitlab);

        var probe = await Provider(new ReplacingAuth()).ProbeCredentialAsync(Context(gitlab.BaseUrl, AuthType.OAuth), CancellationToken.None);

        probe.IsValid.ShouldBeTrue();
        probe.AuthenticatedUserName.ShouldBe("nacho");
    }

    [Fact]
    public async Task An_oauth_token_answered_401_by_the_token_only_self_endpoint_is_not_taken_for_a_stale_token()
    {
        // GitLab answers an OAuth token 401 at personal_access_tokens/self by design. That says nothing about the token, and
        // asking the resolver for a replacement would rotate a working pair on every probe of every OAuth credential.
        var gitlab = new StubProviderHost().Answer("GET", "/api/v4/user", _ => new StubReply(200, User)).Answer("GET", "/api/v4/personal_access_tokens/self", _ => new StubReply(401, """{"message":"401 Unauthorized"}"""));
        _gitlabs.Add(gitlab);
        var auth = new CountingAuth();

        var probe = await Provider(auth).ProbeCredentialAsync(Context(gitlab.BaseUrl, AuthType.OAuth), CancellationToken.None);

        probe.IsValid.ShouldBeTrue();
        probe.Active.ShouldBeNull();
        auth.Invalidations.ShouldBe(0, "the self endpoint's 401 is not a reason to rotate");
        gitlab.Requests.Count(r => r.PathAndQuery.Contains("personal_access_tokens/self")).ShouldBe(1, "and it was not sent again either");
    }

    [Fact]
    public async Task A_credential_marked_as_needing_a_reconnect_is_a_rejection_without_asking_the_provider()
    {
        var gitlab = Gitlab(user: new StubReply(200, User), self: new StubReply(200, "{}"));

        var probe = await Provider(new RefusingAuth()).ProbeCredentialAsync(Context(gitlab.BaseUrl, AuthType.OAuth), CancellationToken.None);

        probe.IsRejected.ShouldBeTrue();
        probe.IsValid.ShouldBeFalse();
        gitlab.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void A_probe_failure_is_a_rejection_only_for_a_refusal_of_the_credential()
    {
        CodeSpace.Messages.Dtos.Providers.CredentialProbeResult.FromFailure(new ProviderApiException(ProviderKind.GitLab, 401, "op", "no", new Exception())).IsRejected.ShouldBeTrue();
        CodeSpace.Messages.Dtos.Providers.CredentialProbeResult.FromFailure(new CredentialNeedsReconnectException(Guid.NewGuid(), "c", "invalid_grant")).IsRejected.ShouldBeTrue();
        CodeSpace.Messages.Dtos.Providers.CredentialProbeResult.FromFailure(new ProviderApiException(ProviderKind.GitLab, 403, "op", "no", new Exception())).IsRejected.ShouldBeFalse();
        CodeSpace.Messages.Dtos.Providers.CredentialProbeResult.FromFailure(new ProviderApiException(ProviderKind.GitLab, 503, "op", "no", new Exception())).IsRejected.ShouldBeFalse();
        CodeSpace.Messages.Dtos.Providers.CredentialProbeResult.FromFailure(new HttpRequestException("down")).IsRejected.ShouldBeFalse();
        CodeSpace.Messages.Dtos.Providers.CredentialProbeResult.FromFailure(new CredentialDisconnectedException(Guid.NewGuid(), "c")).IsRejected.ShouldBeFalse();
    }

    private StubProviderHost Gitlab(StubReply user, StubReply self)
    {
        var gitlab = new StubProviderHost().Answer("GET", "/api/v4/user", _ => user).Answer("GET", "/api/v4/personal_access_tokens/self", _ => self);

        _gitlabs.Add(gitlab);

        return gitlab;
    }

    private sealed class ReplacingAuth : IProviderAuthResolver
    {
        public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new ResolvedAuth { Token = "stale-0123456789" });

        public Task<ResolvedAuth?> InvalidateAsync(ProviderContext context, string rejectedToken, CancellationToken cancellationToken) => Task.FromResult<ResolvedAuth?>(new ResolvedAuth { Token = "fresh-0123456789" });
    }

    private sealed class CountingAuth : IProviderAuthResolver
    {
        public int Invalidations;

        public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new ResolvedAuth { Token = "oauth-token-0123456789" });

        public Task<ResolvedAuth?> InvalidateAsync(ProviderContext context, string rejectedToken, CancellationToken cancellationToken)
        {
            Invalidations++;

            return Task.FromResult<ResolvedAuth?>(new ResolvedAuth { Token = "replacement-0123456789" });
        }
    }

    private sealed class RefusingAuth : IProviderAuthResolver
    {
        public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken) => throw new CredentialNeedsReconnectException(context.Credential.Id, "GitLab · nacho", "invalid_grant");
    }
}
