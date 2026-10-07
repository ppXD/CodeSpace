using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.PullRequests;

/// <summary>
/// The review body-required guard is the one piece of <see cref="PullRequestService"/> logic that runs
/// BEFORE any dependency (db / registry / scope checker) is touched, so a stub-constructed service
/// exercises it directly. The downstream preflight (repo lookup, scope check, capability dispatch) is
/// the same pattern as the existing PostComment path and is covered by the provider-capability tests. So is the pure
/// half of a pinned write's re-read (<see cref="PullRequestService.Moved"/>): what counts as the pull request having moved
/// from what a reviewer saw; the read itself runs over a loopback forge in the integration tier.
/// </summary>
[Trait("Category", "Unit")]
public class PullRequestServiceTests
{
    private static readonly PullRequestService Service = new(null!, null!, null!, null!);

    [Theory]
    [InlineData(PullRequestReviewVerdict.Comment)]
    [InlineData(PullRequestReviewVerdict.RequestChanges)]
    public async Task SubmitReview_requires_a_non_empty_body_for_comment_and_request_changes(PullRequestReviewVerdict verdict)
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            Service.SubmitReviewAsync(Guid.NewGuid(), Guid.NewGuid(), 1, new SubmitPullRequestReviewInput { Verdict = verdict, Body = "   " }, actorUserId: null, CancellationToken.None));

        ex.Message.ShouldContain(verdict.ToString());
        ex.Message.ShouldContain("non-empty body");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task SubmitReview_rejects_a_null_or_empty_body_for_a_comment(string? body)
    {
        await Should.ThrowAsync<InvalidOperationException>(() =>
            Service.SubmitReviewAsync(Guid.NewGuid(), Guid.NewGuid(), 1, new SubmitPullRequestReviewInput { Verdict = PullRequestReviewVerdict.Comment, Body = body }, actorUserId: null, CancellationToken.None));
    }

    private static RemotePullRequest Current(string? headSha = "0a1b2c3d", string targetBranch = "main") => new()
    {
        ExternalId = "7007", Number = 7, Title = "Retry safely", State = PullRequestState.Open, SourceBranch = "release", TargetBranch = targetBranch,
        CommentsCount = 0, WebUrl = "https://forge.test/acme/api/pull/7", CreatedDate = DateTimeOffset.UnixEpoch, UpdatedDate = DateTimeOffset.UnixEpoch, HeadSha = headSha,
    };

    [Theory]
    // pinned head        pinned base       head now      base now        what moved
    [InlineData("0a1b2c3d", "main", "0a1b2c3d", "main", null)]
    [InlineData("0A1B2C3D", null, "0a1b2c3d", "main", null)]                           // a sha compares in any case
    [InlineData(null, null, "ffff0000", "release/2.0", null)]                          // nothing pinned: nothing can move
    [InlineData("0a1b2c3d", "main", "ffff0000", "main", "its head is now ffff0000, not 0a1b2c3d, the one it was pinned to")]
    [InlineData("0a1b2c3d", null, null, "main", "its head is now unknown, not 0a1b2c3d, the one it was pinned to")]   // a head the provider stopped reporting has moved
    [InlineData(null, "docs-sandbox", "0a1b2c3d", "main", "its base is now main, not docs-sandbox, the one it was pinned to")]
    [InlineData(null, "main", "0a1b2c3d", "Main", "its base is now Main, not main, the one it was pinned to")]   // a branch compares exactly, as git compares it
    public void A_pinned_write_is_refused_when_the_pull_request_moved_from_its_pins(string? pinnedHead, string? pinnedBase, string? headNow, string baseNow, string? moved)
    {
        var refusal = PullRequestService.Moved(Current(headNow, baseNow), new PullRequestService.PullRequestPin(pinnedHead, pinnedBase));

        (refusal?.Message).ShouldBe(moved);
        if (refusal is not null) refusal.Number.ShouldBe(7);
    }
}
