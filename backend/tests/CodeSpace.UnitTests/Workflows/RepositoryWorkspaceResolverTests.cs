using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <see cref="RepositoryWorkspaceResolver"/> pure logic — the per-provider HTTPS basic-auth username
/// paired with a clone token. GitHub wants <c>x-access-token</c>, GitLab <c>oauth2</c>; anything else
/// falls back to the GitHub-style username.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RepositoryWorkspaceResolverTests
{
    [Theory]
    [InlineData(ProviderKind.GitHub, "x-access-token")]
    [InlineData(ProviderKind.GitLab, "oauth2")]
    [InlineData(ProviderKind.Git, "x-access-token")]
    public void Token_username_matches_the_provider(ProviderKind provider, string expected) =>
        RepositoryWorkspaceResolver.TokenUsernameFor(provider).ShouldBe(expected);
}
