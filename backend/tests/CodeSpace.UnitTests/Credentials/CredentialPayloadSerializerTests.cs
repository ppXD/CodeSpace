using System.Text.Json;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Credentials;

[Trait("Category", "Unit")]
public class CredentialPayloadSerializerTests
{
    private readonly CredentialPayloadSerializer _serializer = new();

    [Fact]
    public void Pat_round_trips()
    {
        var original = new PatPayload { Token = "ghp_abc123" };

        var json = _serializer.Serialize(original);
        var roundTripped = _serializer.Deserialize<PatPayload>(AuthType.Pat, json);

        roundTripped.Token.ShouldBe("ghp_abc123");
        roundTripped.Type.ShouldBe(AuthType.Pat);
    }

    [Fact]
    public void ProjectAccessToken_round_trips()
    {
        var original = new ProjectAccessTokenPayload { Token = "glpat-proj-xxx" };

        var json = _serializer.Serialize(original);
        var roundTripped = _serializer.Deserialize<ProjectAccessTokenPayload>(AuthType.ProjectAccessToken, json);

        roundTripped.Token.ShouldBe("glpat-proj-xxx");
    }

    [Fact]
    public void GroupAccessToken_round_trips()
    {
        var original = new GroupAccessTokenPayload { Token = "glpat-group-xxx" };

        var json = _serializer.Serialize(original);
        var roundTripped = _serializer.Deserialize<GroupAccessTokenPayload>(AuthType.GroupAccessToken, json);

        roundTripped.Token.ShouldBe("glpat-group-xxx");
    }

    [Fact]
    public void OAuth_round_trips_with_refresh_and_expiry()
    {
        var expiresAt = DateTimeOffset.Parse("2026-12-31T23:59:59Z");
        var original = new OAuthPayload
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            ExpiresAt = expiresAt
        };

        var json = _serializer.Serialize(original);
        var roundTripped = _serializer.Deserialize<OAuthPayload>(AuthType.OAuth, json);

        roundTripped.AccessToken.ShouldBe("access");
        roundTripped.RefreshToken.ShouldBe("refresh");
        roundTripped.ExpiresAt.ShouldBe(expiresAt);
    }

    [Fact]
    public void OAuth_round_trips_without_optional_fields()
    {
        var original = new OAuthPayload { AccessToken = "access" };

        var json = _serializer.Serialize(original);
        var roundTripped = _serializer.Deserialize<OAuthPayload>(AuthType.OAuth, json);

        roundTripped.AccessToken.ShouldBe("access");
        roundTripped.RefreshToken.ShouldBeNull();
        roundTripped.ExpiresAt.ShouldBeNull();
    }

    [Fact]
    public void GitHubApp_round_trips()
    {
        var original = new GitHubAppPayload
        {
            InstallationId = 12345,
            AppId = 67890,
            PrivateKeyPem = "-----BEGIN RSA PRIVATE KEY-----\nMIIBOgIBAAJB...\n-----END RSA PRIVATE KEY-----"
        };

        var json = _serializer.Serialize(original);
        var roundTripped = _serializer.Deserialize<GitHubAppPayload>(AuthType.GitHubApp, json);

        roundTripped.InstallationId.ShouldBe(12345);
        roundTripped.AppId.ShouldBe(67890);
        roundTripped.PrivateKeyPem.ShouldContain("BEGIN RSA PRIVATE KEY");
    }

    [Fact]
    public void SshKey_round_trips_with_passphrase()
    {
        var original = new SshKeyPayload
        {
            PrivateKeyPem = "ssh-key-pem-content",
            Passphrase = "pass123"
        };

        var json = _serializer.Serialize(original);
        var roundTripped = _serializer.Deserialize<SshKeyPayload>(AuthType.SshKey, json);

        roundTripped.PrivateKeyPem.ShouldBe("ssh-key-pem-content");
        roundTripped.Passphrase.ShouldBe("pass123");
    }

    [Fact]
    public void SshKey_round_trips_without_passphrase()
    {
        var original = new SshKeyPayload { PrivateKeyPem = "key" };

        var json = _serializer.Serialize(original);
        var roundTripped = _serializer.Deserialize<SshKeyPayload>(AuthType.SshKey, json);

        roundTripped.Passphrase.ShouldBeNull();
    }

    [Fact]
    public void BasicAuth_round_trips()
    {
        var original = new BasicAuthPayload { Username = "user", Password = "pwd" };

        var json = _serializer.Serialize(original);
        var roundTripped = _serializer.Deserialize<BasicAuthPayload>(AuthType.BasicAuth, json);

        roundTripped.Username.ShouldBe("user");
        roundTripped.Password.ShouldBe("pwd");
    }

    [Fact]
    public void Deserialize_via_base_type_returns_correct_concrete_type()
    {
        var original = new PatPayload { Token = "secret" };
        var json = _serializer.Serialize(original);

        var result = _serializer.Deserialize(AuthType.Pat, json);

        result.ShouldBeOfType<PatPayload>();
        ((PatPayload)result).Token.ShouldBe("secret");
    }

    [Theory]
    [InlineData(AuthType.Pat)]
    [InlineData(AuthType.ProjectAccessToken)]
    [InlineData(AuthType.GroupAccessToken)]
    [InlineData(AuthType.OAuth)]
    [InlineData(AuthType.GitHubApp)]
    [InlineData(AuthType.SshKey)]
    [InlineData(AuthType.BasicAuth)]
    public void Every_AuthType_has_payload_mapping(AuthType type)
    {
        // Ensures the switch dispatch in Deserialize covers every AuthType enum value.
        // A missing case would throw NotSupportedException — we accept any other exception
        // (e.g., JsonException from incomplete JSON) as proof the mapping exists.
        try
        {
            _serializer.Deserialize(type, "{}");
        }
        catch (NotSupportedException)
        {
            throw new Xunit.Sdk.XunitException($"AuthType.{type} is missing a payload schema mapping in CredentialPayloadSerializer");
        }
        catch
        {
            // Expected: required-property errors from System.Text.Json on empty JSON
        }
    }

    [Fact]
    public void Deserialize_throws_JsonException_for_invalid_json()
    {
        var act = () => _serializer.Deserialize(AuthType.Pat, "not valid json");

        Should.Throw<JsonException>(act);
    }

    [Fact]
    public void Generic_Deserialize_casts_to_requested_subtype()
    {
        var original = new OAuthPayload { AccessToken = "x" };
        var json = _serializer.Serialize(original);

        var typed = _serializer.Deserialize<OAuthPayload>(AuthType.OAuth, json);

        typed.AccessToken.ShouldBe("x");
    }
}
