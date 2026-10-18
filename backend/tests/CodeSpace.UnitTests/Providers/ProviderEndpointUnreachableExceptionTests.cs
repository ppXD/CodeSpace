using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Providers;

/// <summary>
/// The typed failure for an edit that upgrades a connected provider's address to https when nothing answers HTTP there: what
/// it names, what it tells the owner to check, and that it never repeats more of the address than scheme, host and port.
/// </summary>
[Trait("Category", "Unit")]
public class ProviderEndpointUnreachableExceptionTests
{
    [Fact]
    public void An_address_that_does_not_answer_is_an_unprocessable_failure_naming_the_origin_and_what_to_check()
    {
        var failure = new ProviderEndpointUnreachableException(ProviderKind.GitLab, new Uri("https://gitlab.acme.test:8443/gitlab?private_token=SECRET"), "AuthenticationException");

        ((IFailure)failure).Kind.ShouldBe(FailureKind.Unprocessable);
        ((IFailure)failure).Code.ShouldBe("provider_endpoint_unreachable");
        failure.Origin.ShouldBe("https://gitlab.acme.test:8443");
        failure.Message.ShouldContain("https://gitlab.acme.test:8443");
        failure.Message.ShouldContain("AuthenticationException");
        failure.Message.ShouldContain("certificate this server trusts");
        failure.Message.ShouldNotContain("SECRET");
        ((IFailure)failure).Details!["provider"].ShouldBe("GitLab");
        ((IFailure)failure).Details!["origin"].ShouldBe("https://gitlab.acme.test:8443");
    }
}
