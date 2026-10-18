using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Enums;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;
using static CodeSpace.UnitTests.Providers.GitLab.GitLabLoopback;

namespace CodeSpace.UnitTests.Providers.GitLab;

/// <summary>
/// Which header a GitLab credential travels in. GitLab 13.0 to 17.7 reads an <c>Authorization: Bearer</c> value of 41 or
/// more characters as an OAuth token alone, so a personal, project or group access token that long (a custom prefix, a
/// 66-character token) is refused as a Bearer however valid it is — it belongs in <c>PRIVATE-TOKEN</c>. An OAuth token
/// belongs in <c>Authorization: Bearer</c>. Every call sends the one header its credential type names and never both,
/// through both of NGitLab's request paths (the typed client, and the raw paged reader built from its internals) and
/// through each raw HTTP call the provider makes, against a loopback GitLab.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitLabCredentialHeaderTests : IDisposable
{
    private readonly StubProviderHost _gitlab = Start();

    public void Dispose() => _gitlab.Dispose();

    public static TheoryData<string, AuthType> EveryOperationForEveryCredentialType()
    {
        var data = new TheoryData<string, AuthType>();

        foreach (var operation in Operations.Keys)
        foreach (var type in new[] { AuthType.Pat, AuthType.ProjectAccessToken, AuthType.GroupAccessToken, AuthType.OAuth })
            data.Add(operation, type);

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryOperationForEveryCredentialType))]
    public async Task Every_request_carries_the_one_header_its_credential_type_names(string operation, AuthType type)
    {
        await TryAsync(() => Operations[operation](Provider(), Context(_gitlab.BaseUrl, type)));

        _gitlab.Requests.ShouldNotBeEmpty($"fixture check: '{operation}' reached GitLab");

        foreach (var request in _gitlab.Requests) ShouldCarryOnly(request, type);
    }

    [Theory]
    [InlineData(AuthType.Pat)]
    [InlineData(AuthType.ProjectAccessToken)]
    [InlineData(AuthType.GroupAccessToken)]
    public async Task A_credential_probe_sends_its_two_requests_the_same_way(AuthType type)
    {
        var probe = await Provider().ProbeCredentialAsync(Context(_gitlab.BaseUrl, type), CancellationToken.None);

        probe.IsValid.ShouldBeTrue(probe.Error);
        _gitlab.Requests.Select(r => r.PathAndQuery).ShouldBe(new[] { "/api/v4/user", "/api/v4/personal_access_tokens/self" }, "the identity read (typed client), then the scope read (raw)");

        foreach (var request in _gitlab.Requests) ShouldCarryOnly(request, type);
    }

    [Theory]
    [InlineData(AuthType.Pat)]
    [InlineData(AuthType.ProjectAccessToken)]
    [InlineData(AuthType.GroupAccessToken)]
    public async Task A_long_access_token_is_not_read_as_an_oauth_token_by_the_legacy_finder(AuthType type)
    {
        // GitLab 13.0 to 17.7: an Authorization header of 41+ characters is looked up as an OAuth token alone, so this
        // 66-character token is a 401 when sent as a Bearer. The typed client and a raw call both used to send one.
        Token.Length.ShouldBeGreaterThan(41, "fixture check: the token is long enough to take the legacy finder's OAuth-only path");
        using var legacy = new StubProviderHost().Answer("GET", "/api/v4/", Legacy);

        (await Provider().GetByExternalIdAsync(Context(legacy.BaseUrl, type), "42", CancellationToken.None)).FullPath.ShouldBe("g/r", "the typed client's call");
        (await Provider().GetMemberStandingAsync(Context(legacy.BaseUrl, type), Repository, "7", CancellationToken.None)).ShouldBe(PullRequestAuthorAssociation.Member, "a raw call");
    }

    /// <summary>GitLab 13.0 to 17.7's finder: any Authorization header is an OAuth lookup that a PAT cannot pass; PRIVATE-TOKEN is the PAT lookup.</summary>
    private static StubReply Legacy(RecordedRequest request) => request.AuthorizationHeader != null || request.PrivateTokenHeader != Token ? new StubReply(401, """{"message":"401 Unauthorized"}""") : Answer(request);

    private static async Task TryAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception) { /* what GitLab answers is not what this test reads — only the request it was sent */ }
    }
}
