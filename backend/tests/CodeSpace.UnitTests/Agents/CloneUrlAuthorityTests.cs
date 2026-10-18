using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// A GitLab repository cloned from another origin than its provider's base URL. git presents the credential to the clone
/// URL's origin only, so when the host redirects between the two origins the clone fails asking for credentials — while the
/// REST calls name the redirect. The resolve says so, once per clone and only when a credential is about to be presented,
/// naming both origins; it never changes the request.
/// </summary>
[Trait("Category", "Unit")]
public class CloneUrlAuthorityTests
{
    [Theory]
    [InlineData(ProviderKind.GitLab, "http://gitlab.acme.test", "https://gitlab.acme.test/org/repo.git", true)]            // http base URL, https clone URL
    [InlineData(ProviderKind.GitLab, "https://gitlab.acme.test", "http://gitlab.acme.test/org/repo.git", true)]
    [InlineData(ProviderKind.GitLab, "https://gitlab.acme.test", "https://gitlab.acme.test:8443/org/repo.git", true)]       // another port
    [InlineData(ProviderKind.GitLab, "https://gitlab.acme.test", "https://gitlab-alias.acme.test/org/repo.git", true)]     // another host
    [InlineData(ProviderKind.GitLab, "https://gitlab.acme.test", "https://gitlab.acme.test/org/repo.git", false)]
    [InlineData(ProviderKind.GitLab, "https://gitlab.acme.test/", "https://GITLAB.acme.test:443/org/repo.git", false)]      // case and the default port are one origin
    [InlineData(ProviderKind.GitLab, "https://gitlab.acme.test/gitlab", "https://gitlab.acme.test/gitlab/org/repo.git", false)]
    [InlineData(ProviderKind.GitLab, "https://svc:pw@gitlab.acme.test", "https://gitlab.acme.test/org/repo.git", false)]    // userinfo is not part of an origin
    [InlineData(ProviderKind.GitLab, "https://gitlab.acme.test", "file:///tmp/origin", false)]                              // not an http clone
    [InlineData(ProviderKind.GitLab, "https://gitlab.acme.test", null, false)]
    [InlineData(ProviderKind.GitLab, null, "https://gitlab.acme.test/org/repo.git", false)]
    [InlineData(ProviderKind.GitHub, "https://github.example.test", "https://elsewhere.example.test/org/repo.git", false)]   // GitHub's API and clone hosts differ by design
    [InlineData(ProviderKind.Git, "https://local", "https://elsewhere.example.test/org/repo.git", false)]
    public void A_clone_url_is_foreign_when_a_gitlab_base_url_and_it_name_different_origins(ProviderKind provider, string? baseUrl, string? cloneUrl, bool expected) =>
        CloneUrlAuthority.IsForeign(provider, baseUrl, cloneUrl).ShouldBe(expected);

    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(false, true, 0)]     // an anonymous clone follows a redirect without harm
    [InlineData(true, false, 0)]
    public void The_warning_is_logged_once_for_a_credentialed_foreign_clone_and_names_both_origins_only(bool credentialed, bool foreign, int expectedWarnings)
    {
        var logger = new CapturingLogger();
        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            CloneUrlHttps = foreign ? "https://svc:CANARY-PW@gitlab.acme.test:8443/org/repo.git?private_token=CANARY-Q" : "http://gitlab.acme.test/org/repo.git",
            ProviderInstance = new ProviderInstance { Provider = ProviderKind.GitLab, BaseUrl = "http://gitlab.acme.test" }
        };

        CloneUrlAuthority.WarnWhenForeign(logger, repository, credentialed);

        logger.Warnings.Count.ShouldBe(expectedWarnings);

        if (expectedWarnings == 0) return;

        logger.Warnings[0].ShouldContain("https://gitlab.acme.test:8443");
        logger.Warnings[0].ShouldContain("http://gitlab.acme.test:80");
        logger.Warnings[0].ShouldContain(repository.Id.ToString());
        logger.Warnings[0].ShouldNotContain("CANARY", Case.Sensitive, "the clone URL's userinfo and query are never logged");
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
