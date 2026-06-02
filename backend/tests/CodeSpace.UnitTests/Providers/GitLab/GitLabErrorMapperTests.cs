using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.GitLab;

/// <summary>
/// Pins GitLabErrorMapper.ClassifyScope — the rule that decides whether a GitLab failure is a real
/// SCOPE gap (→ ProviderInsufficientScopeException) or something else (→ null, so it falls through
/// to ProviderApiException with an accurate status/message). The key correctness guard: a BARE 403
/// (a permission/membership problem) must NOT be mislabelled "missing api scope".
/// </summary>
[Trait("Category", "Unit")]
public class GitLabErrorMapperTests
{
    private readonly GitLabErrorMapper _mapper = new();

    [Fact]
    public void Kind_is_GitLab() => _mapper.Kind.ShouldBe(ProviderKind.GitLab);

    [Fact]
    public void Tagged_403_insufficient_scope_maps_to_the_named_scope()
    {
        var body = """{"error":"insufficient_scope","error_description":"needs api","scope":"api"}""";

        var result = _mapper.ClassifyScope(403, body, "insufficient_scope", "SubmitReviewAsync");

        result.ShouldNotBeNull();
        result!.MissingScopes.ShouldBe(new[] { "api" });
        result.ProviderKind.ShouldBe(ProviderKind.GitLab);
    }

    [Fact]
    public void Tagged_403_reads_the_specific_scope_from_the_body()
    {
        var body = """{"error":"insufficient_scope","scope":"read_api"}""";

        var result = _mapper.ClassifyScope(403, body, null, "ListPullRequestsAsync");

        result.ShouldNotBeNull();
        result!.MissingScopes.ShouldBe(new[] { "read_api" });
    }

    [Fact]
    public void Bare_403_without_the_tag_is_NOT_a_scope_issue()
    {
        // The regression guard: a permission/membership 403 (not a project member, role too low,
        // protected-branch rule) must return null → ProviderApiException(403) → "lack access" message,
        // NOT a fabricated "missing api scope".
        _mapper.ClassifyScope(403, "403 Forbidden", "403 Forbidden", "SubmitReviewAsync").ShouldBeNull();
    }

    [Theory]
    [InlineData(404)]   // no access to the repo / PR not found
    [InlineData(401)]   // bad / revoked token — a different path
    [InlineData(422)]   // semantic rejection (e.g. can't approve own MR)
    [InlineData(200)]
    public void Non_403_statuses_are_never_scope_issues(int statusCode)
    {
        _mapper.ClassifyScope(statusCode, """{"error":"insufficient_scope","scope":"api"}""", null, "SubmitReviewAsync").ShouldBeNull();
    }

    [Fact]
    public void TryMapInsufficientScope_returns_null_for_a_non_GitLab_exception()
    {
        _mapper.TryMapInsufficientScope(new InvalidOperationException("not a gitlab error"), "op").ShouldBeNull();
    }
}
