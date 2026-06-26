using System;
using System.Linq;
using System.Security.Cryptography;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Dtos.ModelCredentials;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the secret-masking pieces of the model-credential management surface: the last-4 hint, and the
/// structural guarantee that the read DTO has NO field that could carry a full key. The DB/team-scoping
/// branches are integration-pinned.
/// </summary>
[Trait("Category", "Unit")]
public class ModelCredentialServiceMaskTests
{
    [Theory]
    [InlineData("sk-ant-api03-1234abcd", "····abcd")]
    [InlineData("sk-or-9f3a", "····9f3a")]
    [InlineData("xy", "····xy")]          // shorter than 4 → whole value after the mask prefix
    [InlineData("", null)]
    [InlineData(null, null)]
    public void MaskKey_exposes_only_the_last_four(string? plaintext, string? expected) =>
        ModelCredentialService.MaskKey(plaintext).ShouldBe(expected);

    [Fact]
    public void MaskKey_never_returns_the_full_key()
    {
        const string key = "sk-ant-supersecret-tail";
        ModelCredentialService.MaskKey(key).ShouldNotContain("supersecret");
    }

    [Fact]
    public void Summary_dto_exposes_only_a_masked_hint_never_a_secret_field()
    {
        var props = typeof(ModelCredentialSummary).GetProperties().Select(p => p.Name).ToList();

        props.ShouldContain("KeyHint", "the read model surfaces only the masked tail");
        props.ShouldNotContain("ApiKey");
        props.ShouldNotContain("EncryptedApiKey");
        props.ShouldNotContain("Secret");
    }

    [Fact]
    public void SafeKeyHint_masks_a_decryptable_secret() =>
        ModelCredentialService.SafeKeyHint(new FixedEncryptor("sk-live-secret-1234"), "ciphertext").ShouldBe("····1234");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SafeKeyHint_is_null_for_a_keyless_credential(string? storedKey) =>
        ModelCredentialService.SafeKeyHint(new FixedEncryptor("never-read"), storedKey).ShouldBeNull();

    [Theory]
    [InlineData(typeof(CryptographicException))]   // key rotated / lost / wrong ring — the #725 key-ring migration case
    [InlineData(typeof(FormatException))]          // stored value isn't even valid protected data
    public void SafeKeyHint_returns_null_when_the_secret_cannot_be_decrypted(Type thrown) =>
        // The list must never 500 because one row's secret is unreadable — render the row with no hint instead.
        ModelCredentialService.SafeKeyHint(new ThrowingEncryptor(thrown), "unreadable-ciphertext").ShouldBeNull();

    private sealed class FixedEncryptor : IPayloadEncryptor
    {
        private readonly string _plaintext;
        public FixedEncryptor(string plaintext) { _plaintext = plaintext; }
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => _plaintext;
    }

    private sealed class ThrowingEncryptor : IPayloadEncryptor
    {
        private readonly Type _thrown;
        public ThrowingEncryptor(Type thrown) { _thrown = thrown; }
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => throw (Exception)Activator.CreateInstance(_thrown)!;
    }
}
