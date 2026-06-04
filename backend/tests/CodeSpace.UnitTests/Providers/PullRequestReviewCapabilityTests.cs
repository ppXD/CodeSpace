using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Shouldly;

namespace CodeSpace.UnitTests.Providers;

/// <summary>
/// The provider-neutral review verdict (Approve / RequestChanges / Comment) is translated per provider
/// by PURE functions — that's the rigor surface, since the Octokit / NGitLab HTTP calls themselves are
/// thin untestable wrappers. GitHub maps 1:1 to a native review event; GitLab maps to a native approval
/// state change (approve / unapprove) PLUS a labeled note carrying the reasoning. Plus: both providers
/// must IMPLEMENT the capability (so the registry's interface-scan resolves it) and both modules must
/// DECLARE its write scope (so EnsureCapability gates it).
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

    // ── GitLab: native approval state change + a labeled note ───────────────────────

    [Fact]
    public void GitLab_maps_each_verdict_to_its_approval_state_change()
    {
        // approve → native approve; request_changes → retract approval (GitLab has no request-changes
        // verdict); a plain comment changes no approval state. (Enum is internal, so it's asserted in
        // the body rather than surfaced as a [Theory] parameter on a public method.)
        GitLabReviewPlan.ActionFor(PullRequestReviewVerdict.Approve).ShouldBe(GitLabApprovalAction.Approve);
        GitLabReviewPlan.ActionFor(PullRequestReviewVerdict.RequestChanges).ShouldBe(GitLabApprovalAction.Unapprove);
        GitLabReviewPlan.ActionFor(PullRequestReviewVerdict.Comment).ShouldBe(GitLabApprovalAction.None);
    }

    [Fact]
    public void GitLab_approve_decision_is_idempotent_and_eligibility_aware()
    {
        // GitLab's approve isn't idempotent (re-approve → 401), so we read state first and decide:
        // already-approved short-circuits to a no-op; ineligible (author / low role) fails clearly.
        GitLabReviewPlan.DecideApprove(userHasApproved: true, userCanApprove: true).ShouldBe(GitLabApproveDecision.AlreadyApproved);
        GitLabReviewPlan.DecideApprove(userHasApproved: true, userCanApprove: false).ShouldBe(GitLabApproveDecision.AlreadyApproved, "already-approved wins even if eligibility now reads false");
        GitLabReviewPlan.DecideApprove(userHasApproved: false, userCanApprove: true).ShouldBe(GitLabApproveDecision.Approve);
        GitLabReviewPlan.DecideApprove(userHasApproved: false, userCanApprove: false).ShouldBe(GitLabApproveDecision.CannotApprove);
    }

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

    // ── GitLab unapprove (raw call): response classification ─────────────────────────

    [Theory]
    [InlineData(200)]  // retracted
    [InlineData(201)]
    [InlineData(204)]
    [InlineData(404)]  // this token hadn't approved — the "not approved" end-state already holds
    public void GitLab_unapprove_treats_success_and_404_as_a_no_op(int statusCode)
    {
        GitLabRepositoryProvider.ClassifyUnapproveResponse(statusCode, "").ShouldBeNull();
    }

    [Fact]
    public void GitLab_unapprove_403_tagged_insufficient_scope_is_a_typed_scope_gap()
    {
        var ex = GitLabRepositoryProvider.ClassifyUnapproveResponse(403, "{\"error\":\"insufficient_scope\",\"scope\":\"api\"}");

        ex.ShouldBeOfType<ProviderInsufficientScopeException>().MissingScopes.ShouldContain("api");
    }

    [Theory]
    [InlineData(403, "403 Forbidden")]      // bare 403 = permission/membership, NOT a scope gap
    [InlineData(401, "401 Unauthorized")]   // bad/revoked token
    [InlineData(409, "conflict")]
    [InlineData(500, "")]
    public void GitLab_unapprove_other_failures_become_a_status_carrying_api_exception(int statusCode, string body)
    {
        var ex = GitLabRepositoryProvider.ClassifyUnapproveResponse(statusCode, body);

        ex.ShouldBeOfType<ProviderApiException>().StatusCode.ShouldBe(statusCode);
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
