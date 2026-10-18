using CodeSpace.Core.Services.ProviderInstances;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Providers;

/// <summary>
/// Pins when an edit MOVES a provider connection's endpoint — where its bound tokens and OAuth client secret are sent —
/// and the typed refusals that keep them from following a repoint. A connection presents its credentials at two places:
/// the API (<c>ApiUrl</c>, or the base URL when unset) for every provider call, and the base URL itself for the OAuth
/// token, authorize and revoke endpoints. Moving either one's scheme, host or port is a move; a path or a credential
/// slot is not. The DB round-trips are integration-pinned in <c>ProviderInstanceEndpointBindingFlowTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class ProviderInstanceEndpointBindingTests
{
    [Theory]
    [InlineData("https://gitlab.acme.test", null, "https://gitlab.acme.test", null, false)]
    [InlineData("https://gitlab.acme.test", null, "https://GITLAB.acme.test:443", null, false)]
    [InlineData("https://gitlab.acme.test", null, "https://gitlab.acme.test/gitlab", null, false)]
    [InlineData("https://gitlab.acme.test", "https://gitlab.acme.test/api", "https://gitlab.acme.test", "https://gitlab.acme.test/other", false)]
    [InlineData("https://gitlab.acme.test", null, "https://gitlab.acme.test", "https://gitlab.acme.test/api", false)]
    [InlineData("https://gitlab.acme.test", "https://gitlab.acme.test", "https://gitlab.acme.test", null, false)]
    [InlineData("https://svc:pw@gitlab.acme.test", null, "https://gitlab.acme.test", null, false)]
    [InlineData("https://gitlab.acme.test", null, "https://attacker.test", null, true)]
    [InlineData("https://gitlab.acme.test", null, "http://gitlab.acme.test", null, true)]
    [InlineData("https://gitlab.acme.test", null, "https://gitlab.acme.test:8443", null, true)]
    [InlineData("https://gitlab.acme.test", null, "https://gitlab.acme.test", "https://attacker.test", true)]
    [InlineData("https://gitlab.acme.test", "https://api.acme.test", "https://gitlab.acme.test", null, true)]
    [InlineData("https://gitlab.acme.test", "https://api.acme.test", "https://attacker.test", "https://api.acme.test", true)]
    // Conservative on purpose: the rule reads URLs, not each provider's derivation, so it cannot know github.com's API
    // already lives at api.github.com — spelling it out reads as a move, refused only while a token or client secret is bound.
    [InlineData("https://github.com", null, "https://github.com", "https://api.github.com", true)]
    // The one move that is not one: an upgrade of http to https on the SAME host, which a redirect from the old URL asks for
    // and which sends a token nowhere it was not already going — only over TLS. Default ports (80 to 443) or the same
    // explicit port; the host, case aside, must be the very same.
    [InlineData("http://gitlab.acme.test", null, "https://gitlab.acme.test", null, false)]
    [InlineData("http://gitlab.acme.test:80", null, "https://gitlab.acme.test:443", null, false)]
    [InlineData("http://gitlab.acme.test:8080", null, "https://gitlab.acme.test:8080", null, false)]
    [InlineData("http://GITLAB.acme.test/", null, "https://gitlab.acme.test/gitlab", null, false)]
    [InlineData("http://svc:pw@gitlab.acme.test", null, "https://gitlab.acme.test", null, false)]
    [InlineData("http://gitlab.acme.test", "http://api.acme.test", "https://gitlab.acme.test", "https://api.acme.test", false)]
    [InlineData("http://gitlab.acme.test", "http://api.acme.test", "https://gitlab.acme.test", "http://api.acme.test", false)]
    [InlineData("http://gitlab.acme.test", null, "https://gitlab.acme.test", "http://gitlab.acme.test", false)]
    // Anything else about the move still is one: another port, another host (an alias included), a downgrade, a sibling domain.
    [InlineData("http://gitlab.acme.test", null, "https://gitlab.acme.test:8443", null, true)]
    [InlineData("http://gitlab.acme.test:8080", null, "https://gitlab.acme.test", null, true)]
    [InlineData("http://gitlab.acme.test:8080", null, "https://gitlab.acme.test:8443", null, true)]
    [InlineData("http://gitlab.acme.test:443", null, "https://gitlab.acme.test", null, true)]
    [InlineData("http://gitlab.acme.test", null, "https://gitlab-alias.acme.test", null, true)]
    [InlineData("http://gitlab.acme.test", null, "https://gitlab.acme.test.attacker.test", null, true)]
    [InlineData("http://gitlab.acme.test", null, "https://attacker.test", null, true)]
    [InlineData("http://gitlab.acme.test", "http://api.acme.test", "https://gitlab.acme.test", "https://attacker.test", true)]
    [InlineData("http://gitlab.acme.test", "http://api.acme.test", "https://gitlab.acme.test", "https://api.acme.test:8443", true)]
    [InlineData("http://gitlab.acme.test", "https://api.acme.test", "https://gitlab.acme.test", "http://api.acme.test", true)]
    [InlineData("http://gitlab.acme.test", null, "ftp://gitlab.acme.test", null, true)]
    public void An_edit_moves_the_endpoint_when_the_api_or_the_base_url_changes_scheme_host_or_port(string storedBaseUrl, string? storedApiUrl, string nextBaseUrl, string? nextApiUrl, bool expected) =>
        ProviderInstanceService.EndpointMoves(storedBaseUrl, storedApiUrl, nextBaseUrl, nextApiUrl).ShouldBe(expected);

    [Theory]
    [InlineData("http://gitlab.acme.test", null, "https://gitlab.acme.test", null, true)]
    [InlineData("http://gitlab.acme.test", null, "https://gitlab.acme.test:8443", null, true)]
    [InlineData("http://gitlab.acme.test", null, "http://gitlab.acme.test/gitlab", null, false)]
    public void An_upgrade_is_still_an_endpoint_change_for_the_audit_line(string storedBaseUrl, string? storedApiUrl, string nextBaseUrl, string? nextApiUrl, bool expected) =>
        ProviderInstanceService.EndpointChanges(storedBaseUrl, storedApiUrl, nextBaseUrl, nextApiUrl).ShouldBe(expected);

    [Fact]
    public void Bound_credentials_refuse_the_move_as_a_precondition_naming_how_many()
    {
        var id = Guid.NewGuid();
        var refusal = new ProviderEndpointCredentialsBoundException(id, 3);

        ((IFailure)refusal).Kind.ShouldBe(FailureKind.PreconditionRequired);
        ((IFailure)refusal).Code.ShouldBe("provider_endpoint_credentials_bound");
        ((IFailure)refusal).Details!["providerInstanceId"].ShouldBe(id);
        ((IFailure)refusal).Details!["boundCredentialCount"].ShouldBe(3);
        refusal.Message.ShouldContain("3");
    }

    [Fact]
    public void A_stored_oauth_secret_refuses_the_move_until_it_is_entered_again()
    {
        var id = Guid.NewGuid();
        var refusal = new ProviderEndpointSecretRequiredException(id);

        ((IFailure)refusal).Kind.ShouldBe(FailureKind.Invalid);
        ((IFailure)refusal).Code.ShouldBe("provider_endpoint_secret_required");
        ((IFailure)refusal).Details!["providerInstanceId"].ShouldBe(id);
    }
}
