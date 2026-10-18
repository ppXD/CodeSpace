using System.Text.Json;
using System.Text.Json.Serialization;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Credentials;

/// <summary>
/// What a credential list says about each credential, from what is recorded: never stored, so it cannot disagree with the
/// columns. Only a static token has an expiry worth showing — an OAuth credential's expiry is its access token's and renews
/// every couple of hours.
/// </summary>
[Trait("Category", "Unit")]
public class CredentialHealthDerivationTests
{
    private static readonly DateTimeOffset Now = new(2030, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    // status, auth type, days until the recorded expiry (null = none recorded), owner left, expected
    [InlineData(CredentialStatus.Active, AuthType.Pat, null, false, CredentialHealth.Active)]
    [InlineData(CredentialStatus.Active, AuthType.Pat, 200, false, CredentialHealth.Active)]
    [InlineData(CredentialStatus.Active, AuthType.Pat, 31, false, CredentialHealth.Active)]
    [InlineData(CredentialStatus.Active, AuthType.Pat, 30, false, CredentialHealth.Expiring)]
    [InlineData(CredentialStatus.Active, AuthType.ProjectAccessToken, 3, false, CredentialHealth.Expiring)]
    [InlineData(CredentialStatus.Active, AuthType.GroupAccessToken, 1, false, CredentialHealth.Expiring)]
    [InlineData(CredentialStatus.Active, AuthType.Pat, -1, false, CredentialHealth.Rejected)]   // its provider-side expiry has passed: it will be refused
    [InlineData(CredentialStatus.Active, AuthType.OAuth, 0, false, CredentialHealth.Active)]     // an access token's expiry is not the credential's
    [InlineData(CredentialStatus.Active, AuthType.OAuth, -1, false, CredentialHealth.Active)]
    [InlineData(CredentialStatus.Error, AuthType.OAuth, null, false, CredentialHealth.Rejected)]
    [InlineData(CredentialStatus.Error, AuthType.Pat, 100, false, CredentialHealth.Rejected)]
    [InlineData(CredentialStatus.Expired, AuthType.Pat, null, false, CredentialHealth.Rejected)]
    [InlineData(CredentialStatus.Revoked, AuthType.Pat, null, false, CredentialHealth.Revoked)]
    [InlineData(CredentialStatus.Revoked, AuthType.OAuth, null, true, CredentialHealth.Revoked)]  // disconnected beats everything
    [InlineData(CredentialStatus.Active, AuthType.Pat, 100, true, CredentialHealth.OwnerLeft)]
    [InlineData(CredentialStatus.Active, AuthType.Pat, 5, true, CredentialHealth.OwnerLeft)]       // someone must act on the departure before the expiry
    [InlineData(CredentialStatus.Error, AuthType.Pat, null, true, CredentialHealth.Rejected)]      // it needs reconnecting whoever holds it
    public void Health_is_read_off_the_recorded_columns(CredentialStatus status, AuthType type, int? daysToExpiry, bool ownerLeft, CredentialHealth expected)
    {
        DateTimeOffset? expires = daysToExpiry is { } days ? Now.AddDays(days).AddHours(-1) : null;   // "30" is a token with a little under 30 days left

        CredentialHealthDerivation.Derive(status, type, expires, ownerLeft, Now).ShouldBe(expected);
    }

    [Fact]
    public void An_expiry_at_this_very_moment_has_passed()
    {
        CredentialHealthDerivation.Derive(CredentialStatus.Active, AuthType.Pat, Now, ownerLeft: false, Now).ShouldBe(CredentialHealth.Rejected);
    }

    [Theory]
    [InlineData(AuthType.Pat, 3.5, 3)]
    [InlineData(AuthType.ProjectAccessToken, 0.2, 0)]
    [InlineData(AuthType.GroupAccessToken, 364.9, 364)]
    [InlineData(AuthType.Pat, -4.0, 0)]
    public void Days_to_expiry_are_whole_days_left_and_never_negative(AuthType type, double days, int expected)
    {
        CredentialHealthDerivation.DaysToExpiry(type, Now.AddDays(days), Now).ShouldBe(expected);
    }

    [Theory]
    [InlineData(AuthType.OAuth)]
    [InlineData(AuthType.GitHubApp)]
    public void A_credential_that_is_not_a_static_token_has_no_days_to_expiry(AuthType type)
    {
        CredentialHealthDerivation.DaysToExpiry(type, Now.AddDays(5), Now).ShouldBeNull();
    }

    [Fact]
    public void A_static_token_with_no_recorded_expiry_has_no_days_to_expiry()
    {
        CredentialHealthDerivation.DaysToExpiry(AuthType.Pat, null, Now).ShouldBeNull();
    }

    [Fact]
    public void The_warning_window_is_a_month()
    {
        CredentialHealthDerivation.ExpiringWithin.ShouldBe(TimeSpan.FromDays(30));
    }

    [Fact]
    public void The_summary_is_additive_on_the_wire_and_names_its_health_in_the_words_clients_branch_on()
    {
        // Clients that read the old fields keep reading them; the two new ones are extra, and the health is spelled the way
        // the contract says (active | expiring | rejected | revoked | owner_left), not as the C# member names.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var summary = new CredentialSummary { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), ProviderInstanceId = Guid.NewGuid(), Ownership = CredentialOwnership.Personal, AuthType = AuthType.Pat, DisplayName = "GitLab · nacho", Status = CredentialStatus.Active, CreatedDate = Now, Health = CredentialHealth.OwnerLeft, DaysToExpiry = 12 };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(summary, options));
        var root = json.RootElement;

        foreach (var property in new[] { "id", "teamId", "providerInstanceId", "ownerUserId", "ownerUserName", "ownership", "authType", "displayName", "status", "expiresDate", "lastValidatedDate", "lastError", "createdDate" })
            root.TryGetProperty(property, out _).ShouldBeTrue($"'{property}' was in the summary before and must stay");

        root.GetProperty("status").GetString().ShouldBe("Active", "existing enums keep their spelling");
        root.GetProperty("health").GetString().ShouldBe("owner_left");
        root.GetProperty("daysToExpiry").GetInt32().ShouldBe(12);
    }

    [Theory]
    [InlineData(CredentialHealth.Active, "active")]
    [InlineData(CredentialHealth.Expiring, "expiring")]
    [InlineData(CredentialHealth.Rejected, "rejected")]
    [InlineData(CredentialHealth.Revoked, "revoked")]
    [InlineData(CredentialHealth.OwnerLeft, "owner_left")]
    public void Every_health_has_its_pinned_wire_name(CredentialHealth health, string wire)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

        JsonSerializer.Serialize(health, options).ShouldBe($"\"{wire}\"", "a rename is a breaking change to every client that branches on it");
        JsonSerializer.Deserialize<CredentialHealth>($"\"{wire}\"", options).ShouldBe(health);
    }
}
