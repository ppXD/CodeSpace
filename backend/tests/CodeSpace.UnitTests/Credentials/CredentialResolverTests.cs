using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using Microsoft.AspNetCore.DataProtection;
using Shouldly;

namespace CodeSpace.UnitTests.Credentials;

/// <summary>
/// A disconnect revokes the credential row and blanks its payload. Fed to Data Protection, that blank reads as a payload
/// "not protected with this protection provider" — the message that sent an operator hunting for a key-ring fault after
/// a reconnect. The resolver is every provider auth strategy's one decrypt, so it is where the disconnect gets named.
/// </summary>
[Trait("Category", "Unit")]
public class CredentialResolverTests
{
    private readonly DataProtectionPayloadEncryptor _encryptor = new(new EphemeralDataProtectionProvider());
    private readonly CredentialPayloadSerializer _serializer = new();

    [Fact]
    public void A_connected_credential_resolves_to_its_payload()
    {
        var credential = BuildCredential(CredentialStatus.Active, _encryptor.Encrypt(_serializer.Serialize(new PatPayload { Token = "glpat-live" })));

        var payload = new CredentialResolver(_encryptor, _serializer).Resolve(credential);

        payload.ShouldBeOfType<PatPayload>().Token.ShouldBe("glpat-live");
    }

    [Fact]
    public void A_disconnected_credential_names_the_disconnect_instead_of_decrypting_its_blanked_payload()
    {
        var credential = BuildCredential(CredentialStatus.Revoked, string.Empty);

        var failure = Should.Throw<CredentialDisconnectedException>(() => new CredentialResolver(_encryptor, _serializer).Resolve(credential));

        failure.CredentialId.ShouldBe(credential.Id);
        failure.Code.ShouldBe(FailureCodes.CredentialDisconnected);
        failure.Kind.ShouldBe(FailureKind.PreconditionRequired, "the remedy is nameable — reconnect or re-link — and a retry as-is reads the same dead row");
        failure.Message.ShouldContain("'alice's GitLab' was disconnected");
    }

    private static Credential BuildCredential(CredentialStatus status, string encryptedPayload) =>
        new() { Id = Guid.NewGuid(), AuthType = AuthType.Pat, DisplayName = "alice's GitLab", Status = status, EncryptedPayload = encryptedPayload };
}
