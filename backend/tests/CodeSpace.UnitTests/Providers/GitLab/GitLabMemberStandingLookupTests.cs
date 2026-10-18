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
/// The member-standing lookup says only what GitLab said about the author: a role (200) or none (404). Everything else —
/// the credential refused, GitLab erroring — is not an answer about the author, and was returned as <c>Unknown</c>, which a
/// members-only trigger reads as "not a member": a dead token or one 503 refused the author's own merge request, with no log
/// line and a reason that told the operator to open the trigger to everyone. It is now thrown with its status, after one more
/// try for a 429 or 5xx.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitLabMemberStandingLookupTests : IDisposable
{
    private const string Member = """{"id":7,"access_level":30}""";
    private const string StandingPath = "/members/all/7";

    private readonly List<IDisposable> _gitlabs = new();

    public void Dispose()
    {
        foreach (var gitlab in _gitlabs) gitlab.Dispose();
    }

    [Theory]
    [InlineData(200, Member, PullRequestAuthorAssociation.Member)]
    [InlineData(200, """{"id":7,"access_level":20}""", PullRequestAuthorAssociation.None)]
    [InlineData(404, """{"message":"404 Not found"}""", PullRequestAuthorAssociation.None)]
    public async Task Only_a_role_or_the_absence_of_one_is_an_answer_about_the_author(int status, string body, PullRequestAuthorAssociation expected)
    {
        var gitlab = Gitlab(_ => new StubReply(status, body));

        (await Standing(gitlab, AuthType.Pat)).ShouldBe(expected);

        gitlab.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(429)]
    public async Task A_429_or_5xx_is_asked_again_once_and_the_second_answer_is_used(int firstStatus)
    {
        var calls = 0;
        var gitlab = Gitlab(_ => Interlocked.Increment(ref calls) == 1 ? new StubReply(firstStatus, """{"message":"try later"}""") : new StubReply(200, Member));

        (await Standing(gitlab, AuthType.Pat)).ShouldBe(PullRequestAuthorAssociation.Member);

        gitlab.Requests.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(429)]
    public async Task A_second_failure_is_thrown_with_its_status_and_the_lookup_is_not_asked_a_third_time(int status)
    {
        var gitlab = Gitlab(_ => new StubReply(status, """{"message":"still down"}"""));

        var failure = await Should.ThrowAsync<ProviderApiException>(() => Standing(gitlab, AuthType.Pat));

        failure.StatusCode.ShouldBe(status);
        failure.ProviderKind.ShouldBe(ProviderKind.GitLab);
        gitlab.Requests.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(401)]
    [InlineData(400)]
    public async Task A_refusal_is_thrown_at_once_and_never_asked_again(int status)
    {
        var gitlab = Gitlab(_ => new StubReply(status, """{"message":"refused"}"""));

        var failure = await Should.ThrowAsync<ProviderApiException>(() => Standing(gitlab, AuthType.Pat));

        failure.StatusCode.ShouldBe(status);
        gitlab.Requests.Count.ShouldBe(1, "the same credential would be refused the same way");
    }

    [Fact]
    public async Task What_the_failing_response_said_is_never_carried_into_the_failure()
    {
        var gitlab = Gitlab(_ => new StubReply(403, """{"message":"token glpat-ECHOED-BACK-0123456789 may not read members"}"""));

        var failure = await Should.ThrowAsync<ProviderApiException>(() => Standing(gitlab, AuthType.Pat));

        failure.Message.ShouldNotContain("glpat-ECHOED-BACK");
        failure.ProviderMessage.ShouldNotContain("glpat-ECHOED-BACK");
    }

    [Fact]
    public async Task An_OAuth_credential_refused_with_a_stale_token_is_replaced_and_the_lookup_answers()
    {
        var gitlab = Gitlab(request => request.AuthorizationHeader == "Bearer stale-token-0123456789" ? new StubReply(401, """{"message":"401 Unauthorized"}""") : new StubReply(200, Member));

        var standing = await Provider(new ReplacingAuth("stale-token-0123456789", "fresh-token-0123456789")).GetMemberStandingAsync(Context(gitlab.BaseUrl, AuthType.OAuth), Repository, "7", CancellationToken.None);

        standing.ShouldBe(PullRequestAuthorAssociation.Member);
        gitlab.Requests.Select(r => r.AuthorizationHeader).ShouldBe(new[] { "Bearer stale-token-0123456789", "Bearer fresh-token-0123456789" });
    }

    private async Task<PullRequestAuthorAssociation> Standing(StubProviderHost gitlab, AuthType type) =>
        await Provider().GetMemberStandingAsync(Context(gitlab.BaseUrl, type), Repository, "7", CancellationToken.None);

    private StubProviderHost Gitlab(Func<RecordedRequest, StubReply> answer)
    {
        var gitlab = new StubProviderHost().Answer("GET", StandingPath, answer);

        _gitlabs.Add(gitlab);

        return gitlab;
    }

    private sealed class ReplacingAuth : IProviderAuthResolver
    {
        private readonly string _stale;
        private readonly string _fresh;

        public ReplacingAuth(string stale, string fresh)
        {
            _stale = stale;
            _fresh = fresh;
        }

        public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new ResolvedAuth { Token = _stale });

        public Task<ResolvedAuth?> InvalidateAsync(ProviderContext context, string rejectedToken, CancellationToken cancellationToken) =>
            Task.FromResult<ResolvedAuth?>(rejectedToken == _stale ? new ResolvedAuth { Token = _fresh } : null);
    }
}
