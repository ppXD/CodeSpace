using CodeSpace.Core.Services.Credentials.Remedy;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Credentials.Remedy;

/// <summary>
/// When a remedy flips from "repair this" to "run it again". A fix that CodeSpace can see (a dead credential coming back, an
/// address that stopped redirecting) is read off what is recorded; one it cannot (a provider's 401 on a row that looked fine)
/// is only believed once something validated the credential since.
/// </summary>
[Trait("Category", "Unit")]
public class RemedyResolutionTests
{
    private static readonly DateTimeOffset Now = new(2030, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FailedAt = Now.AddHours(-1);

    private static RemedyResolution.CredentialState State(CredentialStatus status = CredentialStatus.Active, AuthType type = AuthType.OAuth, DateTimeOffset? validated = null, DateTimeOffset? expires = null, bool retired = false, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), status, type, expires, validated, retired);

    [Theory]
    [InlineData(CredentialStatus.Active, false, true)]
    [InlineData(CredentialStatus.Error, false, false)]
    [InlineData(CredentialStatus.Revoked, false, false)]
    [InlineData(CredentialStatus.Expired, false, false)]
    [InlineData(CredentialStatus.Active, true, false)]     // retired: soft-deleted, never live again
    public void A_credential_is_live_when_it_is_in_service_and_not_marked_dead(CredentialStatus status, bool retired, bool live)
    {
        RemedyResolution.IsLive(State(status, retired: retired), Now).ShouldBe(live);
    }

    [Fact]
    public void A_static_token_past_its_recorded_expiry_is_not_live()
    {
        RemedyResolution.IsLive(State(type: AuthType.Pat, expires: Now.AddMinutes(-1)), Now).ShouldBeFalse();
        RemedyResolution.IsLive(State(type: AuthType.Pat, expires: Now.AddDays(5)), Now).ShouldBeTrue("expiring soon is still live");
    }

    [Theory]
    [InlineData(FailureCodes.CredentialDisconnected)]
    [InlineData(FailureCodes.CredentialNeedsReconnect)]
    public void A_credential_that_was_dead_and_is_live_again_is_fixed_whatever_the_dates(string code)
    {
        RemedyResolution.FailingCredentialIsFixed(State(validated: null), code, FailedAt, Now).ShouldBeTrue();
    }

    [Theory]
    [InlineData(FailureCodes.ProviderUnauthorized)]
    [InlineData(FailureCodes.GitCredentialRejected)]
    [InlineData(FailureCodes.OAuthExchangeFailed)]
    public void A_credential_a_401_blamed_is_fixed_only_once_something_validated_it_since(string code)
    {
        RemedyResolution.FailingCredentialIsFixed(State(validated: FailedAt.AddMinutes(-5)), code, FailedAt, Now).ShouldBeFalse("it looked fine before the failure too, so a live row proves nothing");
        RemedyResolution.FailingCredentialIsFixed(State(validated: null), code, FailedAt, Now).ShouldBeFalse();
        RemedyResolution.FailingCredentialIsFixed(State(validated: FailedAt.AddMinutes(5)), code, FailedAt, Now).ShouldBeTrue("a reconnect, a replaced token or a passing probe since");
    }

    [Fact]
    public void A_dead_credential_is_never_fixed()
    {
        RemedyResolution.FailingCredentialIsFixed(State(CredentialStatus.Error, validated: Now), FailureCodes.ProviderUnauthorized, FailedAt, Now).ShouldBeFalse();
    }

    [Fact]
    public void Only_a_fix_that_shows_in_the_record_is_observable()
    {
        RemedyResolution.FixIsObservable(FailureCodes.CredentialDisconnected).ShouldBeTrue();
        RemedyResolution.FixIsObservable(FailureCodes.CredentialNeedsReconnect).ShouldBeTrue();
        RemedyResolution.FixIsObservable(FailureCodes.ProviderBaseUrlRedirects).ShouldBeTrue();
        RemedyResolution.FixIsObservable(FailureCodes.ProviderUnauthorized).ShouldBeFalse();
        RemedyResolution.FixIsObservable(FailureCodes.GitCredentialRejected).ShouldBeFalse();
    }

    [Fact]
    public void A_repository_re_linked_to_a_different_live_credential_works()
    {
        var failing = Guid.NewGuid();

        RemedyResolution.RepositoryWorks(State(), failing, FailureCodes.CredentialDisconnected, FailedAt, Now).ShouldBeTrue();
        RemedyResolution.RepositoryWorks(State(CredentialStatus.Revoked), failing, FailureCodes.CredentialDisconnected, FailedAt, Now).ShouldBeFalse("moved onto another dead credential");
        RemedyResolution.RepositoryWorks(null, failing, FailureCodes.CredentialDisconnected, FailedAt, Now).ShouldBeFalse("bound to nothing, or to a credential that is gone");
    }

    [Fact]
    public void A_repository_still_on_the_failing_credential_works_only_when_that_credential_is_fixed()
    {
        var failing = Guid.NewGuid();

        RemedyResolution.RepositoryWorks(State(id: failing, status: CredentialStatus.Error), failing, FailureCodes.CredentialNeedsReconnect, FailedAt, Now).ShouldBeFalse();
        RemedyResolution.RepositoryWorks(State(id: failing), failing, FailureCodes.CredentialNeedsReconnect, FailedAt, Now).ShouldBeTrue("reconnected in place: same row, live again");
        RemedyResolution.RepositoryWorks(State(id: failing), failing, FailureCodes.ProviderUnauthorized, FailedAt, Now).ShouldBeFalse("a 401 on a live-looking row needs a validation after it");
    }

    [Theory]
    [InlineData("http://gitlab.example.com", "http://gitlab.example.com", false)]
    [InlineData("http://gitlab.example.com", "https://gitlab.example.com", true)]
    [InlineData("http://gitlab.example.com", "http://gitlab.example.com/", false)]
    [InlineData("http://gitlab.example.com", "HTTP://GITLAB.EXAMPLE.COM/gitlab", false)]
    [InlineData("https://gitlab.example.com:8443", "https://gitlab.example.com", true)]
    [InlineData("https://old.example.com", "https://new.example.com", true)]
    [InlineData(null, "https://new.example.com", false)]
    public void A_redirect_is_resolved_once_the_base_url_moves_off_the_redirecting_origin(string? requested, string currentBaseUrl, bool resolved)
    {
        RemedyResolution.RedirectIsResolved(requested, currentBaseUrl).ShouldBe(resolved);
    }

    [Theory]
    // requested origin, where it points, the stored base URL, the stored API URL, how many credentials are connected → the repair that will be ACCEPTED
    [InlineData("http://gitlab.example.com", "https://gitlab.example.com", "http://gitlab.example.com", null, 3, RedirectRepair.EditAddress)]        // the one edit allowed past connected tokens: http to https on the same host
    [InlineData("http://gitlab.example.com", "https://gitlab.example.com", "http://gitlab.example.com/gitlab", null, 3, RedirectRepair.EditAddress)]
    [InlineData("http://old.example.com", "https://new.example.com", "http://old.example.com", null, 0, RedirectRepair.EditAddress)]              // nothing connected, so a connection moves freely
    [InlineData("http://old.example.com", "https://new.example.com", "http://old.example.com", null, 1, RedirectRepair.DisconnectFirst)]          // another host: its tokens would follow it
    [InlineData("http://gitlab.example.com", "http://gitlab.example.com:8080", "http://gitlab.example.com", null, 2, RedirectRepair.DisconnectFirst)] // another port
    [InlineData("https://gitlab.example.com", "http://gitlab.example.com", "https://gitlab.example.com", null, 2, RedirectRepair.DisconnectFirst)]   // a downgrade is not an upgrade
    [InlineData("http://gitlab.example.com", "https://gitlab.example.com", "http://gitlab.example.com", "https://api.example.com", 2, RedirectRepair.EditAddress)] // the API address is its own and does not move
    [InlineData("http://gitlab.example.com", "http://gitlab.example.com", "http://gitlab.example.com", null, 0, RedirectRepair.HostSide)]          // same origin: a redirect inside it that was not followed; no address of ours to correct
    [InlineData("http://gitlab.example.com", "HTTP://GITLAB.EXAMPLE.COM", "http://gitlab.example.com", null, 2, RedirectRepair.HostSide)]
    [InlineData("http://gitlab.example.com", null, "http://gitlab.example.com", null, 0, RedirectRepair.HostSide)]                                 // it named nowhere to point
    public void Only_a_repair_the_server_will_accept_is_offered_for_a_redirect(string? requested, string? to, string baseUrl, string? apiUrl, int connected, RedirectRepair expected)
    {
        RemedyResolution.RepairRedirect(requested, to, baseUrl, apiUrl, connected).ShouldBe(expected);
    }

    [Theory]
    // the server's own rule (ProviderInstanceService.EndpointMoves) is what decides — this pins that the remedy and the endpoint cannot disagree
    [InlineData("http://old.example.com", "https://new.example.com", 1)]
    [InlineData("http://gitlab.example.com", "https://gitlab.example.com", 1)]
    [InlineData("http://gitlab.example.com", "http://gitlab.example.com:81", 1)]
    public void The_repair_agrees_with_what_editing_the_address_would_do_while_credentials_are_connected(string baseUrl, string to, int connected)
    {
        var edit = CodeSpace.Core.Services.ProviderInstances.ProviderInstanceService.EndpointMoves(baseUrl, null, to, null);

        (RemedyResolution.RepairRedirect(baseUrl, to, baseUrl, null, connected) == RedirectRepair.DisconnectFirst).ShouldBe(edit);
    }
}
