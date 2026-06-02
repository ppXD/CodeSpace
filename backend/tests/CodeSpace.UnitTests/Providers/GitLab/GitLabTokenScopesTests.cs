using CodeSpace.Core.Services.Providers.GitLab;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.GitLab;

/// <summary>
/// Pins GitLabRepositoryProvider.ParseTokenScopes — the pure parse of GitLab's
/// <c>personal_access_tokens/self</c> body that feeds a PAT's real granted scopes into the
/// capability check. The contract is: return the scope list when present, else null (NOT empty)
/// so the capability check treats "couldn't read scopes" as unknown rather than "zero scopes".
/// </summary>
[Trait("Category", "Unit")]
public class GitLabTokenScopesTests
{
    [Fact]
    public void ParseTokenScopes_returns_the_scopes_array()
    {
        var json = """{"id":1,"name":"code space","scopes":["api","read_api","read_repository"],"active":true}""";

        GitLabRepositoryProvider.ParseTokenScopes(json).ShouldBe(new[] { "api", "read_api", "read_repository" });
    }

    [Theory]
    [InlineData("""{"scopes":[]}""")]              // empty array → unknown, not "zero scopes"
    [InlineData("""{"id":1,"active":true}""")]     // no scopes field at all
    [InlineData("""{"scopes":"api"}""")]           // wrong type — string, not an array
    [InlineData("not json at all")]                // malformed body
    [InlineData("")]                               // empty body
    public void ParseTokenScopes_returns_null_when_absent_empty_or_malformed(string json)
    {
        GitLabRepositoryProvider.ParseTokenScopes(json).ShouldBeNull();
    }

    [Fact]
    public void ParseTokenScopes_skips_blank_and_non_string_entries()
    {
        var json = """{"scopes":["api","",null,123,"read_user"]}""";

        GitLabRepositoryProvider.ParseTokenScopes(json).ShouldBe(new[] { "api", "read_user" });
    }
}
