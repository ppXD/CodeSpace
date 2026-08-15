using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Credentials;

[Trait("Category", "Unit")]
public sealed class StorageCredentialRulesTests
{
    [Theory]
    [InlineData(" Primary-Store ", "primary-store")]
    [InlineData("A", "a")]
    [InlineData("store-01", "store-01")]
    public void Stable_names_are_server_normalized_to_the_database_key_contract(string value, string expected) =>
        StorageCredentialRules.NormalizeStableName(value).ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("-starts-with-hyphen")]
    [InlineData("contains space")]
    [InlineData("contains_underscore")]
    public void Invalid_stable_names_are_rejected(string value) =>
        Should.Throw<ArgumentException>(() => StorageCredentialRules.NormalizeStableName(value));

    [Fact]
    public void Safe_hints_are_trimmed_bounded_and_control_character_free()
    {
        StorageCredentialRules.NormalizeSafeHint("  ending-1234  ").ShouldBe("ending-1234");
        StorageCredentialRules.NormalizeSafeHint(null).ShouldBeNull();

        Should.Throw<ArgumentException>(() => StorageCredentialRules.NormalizeSafeHint("   "));
        Should.Throw<ArgumentException>(() => StorageCredentialRules.NormalizeSafeHint("line\nbreak"));
        Should.Throw<ArgumentException>(() => StorageCredentialRules.NormalizeSafeHint(new string('x', 33)));
    }

    [Fact]
    public void Fingerprints_cover_ciphertext_and_opaque_refs_use_the_profile_contract()
    {
        StorageCredentialRules.EnvelopeFingerprint("ciphertext-envelope")
            .ShouldBe("sha256:2b11825af2420f404162d4fe770262fc017b6b38341cccfd3e5b3c746c9f9296");
        StorageCredentialRules.CredentialRef(Guid.Parse("11111111-2222-3333-4444-555555555555"), 7)
            .ShouldBe("db:11111111-2222-3333-4444-555555555555:7");
    }

    [Fact]
    public void Revoked_credentials_cannot_rotate()
    {
        StorageCredentialRules.EnsureRotationAllowed(StorageCredentialState.Active);
        Should.Throw<ArgumentException>(() => StorageCredentialRules.EnsureRotationAllowed(StorageCredentialState.Revoked)).Message.ShouldContain("revoked", Case.Insensitive);
    }
}
