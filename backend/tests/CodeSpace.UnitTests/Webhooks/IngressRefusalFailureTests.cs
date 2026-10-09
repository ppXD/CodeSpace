using CodeSpace.Core.Services.Webhooks.Exceptions;
using CodeSpace.Core.Services.Workflows.RunSources.Admission.Exceptions;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Webhooks;

/// <summary>
/// What each ingress refusal means to a caller that sees only an <see cref="IFailure"/>. A delivery missing the id its
/// provider always sends is malformed; an author a members-only trigger does not admit is a known identity that is not
/// enough; a debounced pull request is a spent window that the same event clears later, untouched.
/// </summary>
[Trait("Category", "Unit")]
public class IngressRefusalFailureTests
{
    public static TheoryData<Exception, FailureKind, string> Refusals => new()
    {
        { new WebhookDeliveryUnidentifiedException(Guid.NewGuid(), "X-GitHub-Delivery"), FailureKind.Invalid, FailureCodes.InvalidRequest },
        { new PullRequestAuthorRefusedException("pull request #7 was written by an author whose standing is 'none'", "audit-key"), FailureKind.Forbidden, FailureCodes.Forbidden },
        { new PullRequestTriggerDebouncedException(7, "head-1", TimeSpan.FromSeconds(60), "audit-key"), FailureKind.Exhausted, FailureCodes.RateLimited },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Each_refusal_says_what_a_caller_should_do_about_it(Exception refusal, FailureKind kind, string code)
    {
        var failure = refusal.ShouldBeAssignableTo<IFailure>().ShouldNotBeNull();

        failure.Kind.ShouldBe(kind);
        failure.Code.ShouldBe(code);
    }
}
