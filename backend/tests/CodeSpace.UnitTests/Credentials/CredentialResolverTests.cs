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

    [Fact]
    public void The_disconnect_names_the_remedies_a_person_can_perform_today_and_who_may_apply_them()
    {
        // A run's failure is the one place most people meet a disconnect, so it must name what actually fixes it: a team
        // Admin re-linking the repository, or the owner of a personal connection reconnecting. Not a step that has no screen
        // yet — re-linking every repository on the credential at once is an API only — and not "re-link" said to someone who
        // is not allowed to.
        var credential = BuildCredential(CredentialStatus.Revoked, string.Empty);

        var message = Should.Throw<CredentialDisconnectedException>(() => new CredentialResolver(_encryptor, _serializer).Resolve(credential)).Message;

        message.ShouldContain("team Admin");
        message.ShouldContain("re-link the repository");
        message.ShouldContain("its owner reconnecting the provider");
        message.ShouldNotContain("at once", Case.Insensitive, "the all-at-once relink has no screen, so no message sends anyone looking for it");
    }

    [Fact]
    public void A_repository_left_on_a_disconnected_credential_carries_the_same_remedies_in_its_own_error()
    {
        var error = CredentialRevocationService.DisconnectedRepositoryError;

        error.ShouldContain("Re-link this repository");
        error.ShouldContain("a team Admin can");
        error.ShouldContain("its owner reconnecting the provider");
        error.ShouldContain("unbind");
        error.ShouldNotContain("at once", Case.Insensitive, "the all-at-once relink has no screen, so no message sends anyone looking for it");
    }

    private static Credential BuildCredential(CredentialStatus status, string encryptedPayload) =>
        new() { Id = Guid.NewGuid(), AuthType = AuthType.Pat, DisplayName = "alice's GitLab", Status = status, EncryptedPayload = encryptedPayload };
}
