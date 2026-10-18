using CodeSpace.Core.Services.Providers.GitLab;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.GitLab;

/// <summary>
/// What <c>GET /personal_access_tokens/self</c> says about a token beyond its scopes: when it expires and whether it is
/// still live. GitLab gives every new personal, project and group token an expiry and a token that reaches it answers 401
/// to everything, yet CodeSpace discarded both fields. Each part is null when the body does not say it — a caller must
/// never read "does not say" as "never expires" or "not active".
/// </summary>
[Trait("Category", "Unit")]
public class GitLabTokenSelfTests
{
    [Fact]
    public void A_dated_expiry_is_midnight_UTC_at_its_start_and_the_token_is_live()
    {
        var self = GitLabRepositoryProvider.ParseTokenSelf("""{"id":1,"name":"codespace","scopes":["api","read_user"],"expires_at":"2030-07-04","active":true,"revoked":false}""");

        self.ExpiresAt.ShouldBe(new DateTimeOffset(2030, 7, 4, 0, 0, 0, TimeSpan.Zero));
        self.Active.ShouldBe(true);
        self.Scopes.ShouldBe(new[] { "api", "read_user" });
    }

    [Theory]
    [InlineData("""{"expires_at":"2030-07-04T10:30:00.000Z","active":true}""", 2030, 7, 4, 10, 30)]
    [InlineData("""{"expires_at":"2030-07-04T10:30:00+08:00","active":true}""", 2030, 7, 4, 2, 30)]
    public void A_timestamped_expiry_is_read_as_the_instant_it_names(string json, int year, int month, int day, int hour, int minute)
    {
        GitLabRepositoryProvider.ParseTokenSelf(json).ExpiresAt.ShouldBe(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_token_that_never_expires_has_no_expiry_but_is_still_described()
    {
        var self = GitLabRepositoryProvider.ParseTokenSelf("""{"scopes":["api"],"expires_at":null,"active":true}""");

        self.ExpiresAt.ShouldBeNull();
        self.Active.ShouldBe(true, "the body described the token — null expiry here means none, and Active is what says so");
    }

    [Theory]
    [InlineData("""{"active":false}""")]
    [InlineData("""{"active":true,"revoked":true}""")]
    [InlineData("""{"revoked":true}""")]
    public void A_revoked_or_inactive_token_is_not_active(string json)
    {
        GitLabRepositoryProvider.ParseTokenSelf(json).Active.ShouldBe(false);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"scopes":["api"]}""")]
    [InlineData("""{"expires_at":"not a date","active":"yes"}""")]
    [InlineData("""{"expires_at":20300704}""")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    public void What_the_body_does_not_say_is_unknown_not_a_default(string json)
    {
        var self = GitLabRepositoryProvider.ParseTokenSelf(json);

        self.ExpiresAt.ShouldBeNull();
        self.Active.ShouldBeNull();
    }
}
