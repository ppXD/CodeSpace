using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.PullRequests;

/// <summary>
/// The review body-required guard is the one piece of <see cref="PullRequestService"/> logic that runs
/// BEFORE any dependency (db / registry / scope checker) is touched, so a stub-constructed service
/// exercises it directly. The downstream preflight (repo lookup, scope check, capability dispatch) is
/// the same pattern as the existing PostComment path and is covered by the provider-capability tests.
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
            Service.SubmitReviewAsync(Guid.NewGuid(), Guid.NewGuid(), 1, verdict, "   ", actorUserId: null, CancellationToken.None));

        ex.Message.ShouldContain(verdict.ToString());
        ex.Message.ShouldContain("non-empty body");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task SubmitReview_rejects_a_null_or_empty_body_for_a_comment(string? body)
    {
        await Should.ThrowAsync<InvalidOperationException>(() =>
            Service.SubmitReviewAsync(Guid.NewGuid(), Guid.NewGuid(), 1, PullRequestReviewVerdict.Comment, body, actorUserId: null, CancellationToken.None));
    }
}
