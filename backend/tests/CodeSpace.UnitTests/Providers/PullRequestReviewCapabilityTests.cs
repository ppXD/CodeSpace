using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Providers;

/// <summary>
/// The provider-neutral review verdict (Approve / RequestChanges / Comment) is translated per provider
/// by a PURE function — that's the rigor surface, since the Octokit / NGitLab HTTP calls themselves are
/// thin untestable wrappers. Plus: both providers must IMPLEMENT the capability (so the registry's
/// interface-scan resolves it) and both modules must DECLARE its write scope (so EnsureCapability gates it).
/// </summary>
[Trait("Category", "Unit")]
public class PullRequestReviewCapabilityTests
{
    // ── GitHub: native review event (1:1) ──────────────────────────────────────────

    [Theory]
    [InlineData(PullRequestReviewVerdict.Approve, Octokit.PullRequestReviewEvent.Approve)]
    [InlineData(PullRequestReviewVerdict.RequestChanges, Octokit.PullRequestReviewEvent.RequestChanges)]
    [InlineData(PullRequestReviewVerdict.Comment, Octokit.PullRequestReviewEvent.Comment)]
    public void GitHub_maps_each_verdict_to_its_native_review_event(PullRequestReviewVerdict verdict, Octokit.PullRequestReviewEvent expected)
    {
        GitHubReviewMapping.ToEvent(verdict).ShouldBe(expected);
    }

    // ── GitLab: a labeled MR note (no native verdict) ───────────────────────────────

    [Fact]
    public void GitLab_labels_approve_and_request_changes_and_keeps_a_bare_comment()
    {
        GitLabReviewPlan.NoteFor(PullRequestReviewVerdict.Approve, "ship it")
            .ShouldBe("**✅ Approved**\n\nship it");

        GitLabReviewPlan.NoteFor(PullRequestReviewVerdict.RequestChanges, "fix the leak")
            .ShouldBe("**🛑 Changes requested**\n\nfix the leak");

        GitLabReviewPlan.NoteFor(PullRequestReviewVerdict.Comment, "just a thought")
            .ShouldBe("just a thought", "a plain comment is posted verbatim — no label");
    }

    [Fact]
    public void GitLab_emits_a_bare_label_when_an_approve_carries_no_body()
    {
        GitLabReviewPlan.NoteFor(PullRequestReviewVerdict.Approve, null).ShouldBe("**✅ Approved**");
        GitLabReviewPlan.NoteFor(PullRequestReviewVerdict.Approve, "   ").ShouldBe("**✅ Approved**");
    }

    // ── Wiring: provider implements the capability ⇒ the registry resolves it ───────

    [Theory]
    [InlineData(typeof(GitHubRepositoryProvider))]
    [InlineData(typeof(GitLabRepositoryProvider))]
    public void Provider_implements_the_review_capability(Type providerType)
    {
        typeof(IPullRequestReviewCapability).IsAssignableFrom(providerType)
            .ShouldBeTrue($"{providerType.Name} must implement IPullRequestReviewCapability so IProviderRegistry.Require resolves it");
    }

    // ── Scope: both modules declare a WRITE scope for the capability ────────────────

    [Fact]
    public void GitHub_module_gates_review_behind_a_repo_write_scope()
    {
        var req = new GitHubProviderModule().CapabilityScopeRequirements[typeof(IPullRequestReviewCapability)];

        req.IsSatisfied(new[] { "repo" }).ShouldBeTrue();
        req.IsSatisfied(new[] { "public_repo" }).ShouldBeTrue();
        req.IsSatisfied(Array.Empty<string>()).ShouldBeFalse("a token with no repo scope can't submit a review");
    }

    [Fact]
    public void GitLab_module_gates_review_behind_the_api_write_scope()
    {
        var req = new GitLabProviderModule().CapabilityScopeRequirements[typeof(IPullRequestReviewCapability)];

        req.IsSatisfied(new[] { "api" }).ShouldBeTrue();
        req.IsSatisfied(new[] { "read_api" }).ShouldBeFalse("read_api is read-only — submitting a review needs write `api`");
    }
}
