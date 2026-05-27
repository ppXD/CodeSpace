using System.Text;
using CodeSpace.Core.Services.Variables;
using Shouldly;

namespace CodeSpace.UnitTests.Variables;

/// <summary>
/// Pins the variable-encryption contract. Coverage: round-trip across UTF-8 surface area,
/// non-determinism (random nonce per call), authenticated-decrypt failure modes, envelope-
/// layout assumptions the rotation tooling will rely on, constructor key-length enforcement.
///
/// The env-var name constant is also pinned in <see cref="VariableEncryptionConfigPinningTests"/>
/// (Rule 8) — operators set the master key via that env var; renaming it silently breaks
/// every deployment, so the rename has to show up as a failing test.
/// </summary>
[Trait("Category", "Unit")]
public class AesGcmVariableEncryptionTests
{
    // Fixed 32-byte test key in base64 — never used in production, just for these tests.
    private const string TestMasterKeyBase64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    private static AesGcmVariableEncryption MakeSut(string keyB64 = TestMasterKeyBase64) =>
        new(Convert.FromBase64String(keyB64));

    // ─── Round-trip ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("a-typical-api-key-shape-32chars0")]
    [InlineData("postgresql://user:pass@host:5432/db?sslmode=require")]
    [InlineData("multi\nline\nvalue\nwith\ttabs")]
    [InlineData("中文密碼測試 🔐 ünïcødé")]
    public void Encrypt_then_decrypt_returns_the_original_utf8_plaintext(string plaintext)
    {
        var sut = MakeSut();

        var blob = sut.Encrypt(plaintext);
        var roundTripped = sut.Decrypt(blob);

        roundTripped.ShouldBe(plaintext);
    }

    [Fact]
    public void Encrypt_handles_large_values_up_to_64KiB()
    {
        var sut = MakeSut();
        var big = new string('x', 64 * 1024);

        var blob = sut.Encrypt(big);
        sut.Decrypt(blob).ShouldBe(big);
    }

    // ─── Non-determinism (random nonce per call) ──────────────────────────────

    [Fact]
    public void Encrypt_is_non_deterministic_random_nonce_per_call()
    {
        // Two encrypts of the same plaintext MUST yield different ciphertexts. Otherwise
        // an attacker can detect duplicates across rows + leak structural information.
        var sut = MakeSut();
        const string plaintext = "deterministic-input";

        var a = sut.Encrypt(plaintext);
        var b = sut.Encrypt(plaintext);

        a.ShouldNotBe(b, "AES-GCM with random nonce must produce distinct ciphertexts");
        sut.Decrypt(a).ShouldBe(plaintext);
        sut.Decrypt(b).ShouldBe(plaintext);
    }

    // ─── Envelope layout (rotation tooling relies on this) ─────────────────────

    [Fact]
    public void Envelope_layout_is_nonce_12_then_ciphertext_then_tag_16()
    {
        // The rotation job (read every row, decrypt with old key, re-encrypt with new)
        // doesn't need to know the layout — it just calls Decrypt + Encrypt. But the
        // overhead per row is pinned so capacity planning stays honest: nonce(12) +
        // tag(16) = 28 bytes per secret, regardless of plaintext length.
        var sut = MakeSut();
        var blob = sut.Encrypt("hello");

        const int nonceBytes = 12;
        const int tagBytes = 16;
        const int plaintextBytes = 5; // "hello"

        blob.Length.ShouldBe(nonceBytes + plaintextBytes + tagBytes);
    }

    // ─── Authenticated decryption — must fail closed on tampering ─────────────

    [Fact]
    public void Decrypt_fails_when_key_does_not_match_the_encrypting_key()
    {
        var encryptedWithKeyA = MakeSut().Encrypt("secret");

        // Different 32 bytes (last byte flipped).
        var keyBBytes = Convert.FromBase64String(TestMasterKeyBase64);
        keyBBytes[31] ^= 0xFF;
        var decryptWithKeyB = new AesGcmVariableEncryption(keyBBytes);

        Should.Throw<Exception>(() => decryptWithKeyB.Decrypt(encryptedWithKeyA),
            "AES-GCM authentication tag must fail under a different key");
    }

    [Fact]
    public void Decrypt_fails_when_ciphertext_byte_is_flipped()
    {
        var sut = MakeSut();
        var blob = sut.Encrypt("secret");

        // Flip a single bit in the ciphertext (skip the nonce so the failure is
        // specifically the auth tag detecting tampering, not a nonce mismatch).
        blob[15] ^= 0x01;

        Should.Throw<Exception>(() => sut.Decrypt(blob),
            "AES-GCM authentication tag must catch ciphertext tampering");
    }

    [Fact]
    public void Decrypt_fails_when_tag_byte_is_flipped()
    {
        var sut = MakeSut();
        var blob = sut.Encrypt("secret");
        blob[^1] ^= 0x01;

        Should.Throw<Exception>(() => sut.Decrypt(blob),
            "AES-GCM authentication tag must catch direct tag tampering");
    }

    [Fact]
    public void Decrypt_fails_on_truncated_blob()
    {
        var sut = MakeSut();
        var blob = sut.Encrypt("secret");
        var truncated = blob[..^4];

        Should.Throw<Exception>(() => sut.Decrypt(truncated),
            "truncated envelope must not silently succeed");
    }

    [Fact]
    public void Decrypt_fails_on_envelope_shorter_than_nonce_plus_tag()
    {
        var sut = MakeSut();
        var stub = new byte[10]; // less than nonce(12) + tag(16) = 28 minimum overhead

        Should.Throw<Exception>(() => sut.Decrypt(stub));
    }

    // ─── Constructor — key length enforcement ─────────────────────────────────

    [Theory]
    [InlineData(16)]  // AES-128 key — rejected, we are AES-256-only
    [InlineData(24)]  // AES-192 key — rejected
    [InlineData(31)]  // off-by-one short
    [InlineData(33)]  // off-by-one long
    public void Ctor_rejects_keys_that_are_not_exactly_32_bytes(int keyLength)
    {
        Should.Throw<ArgumentException>(() => new AesGcmVariableEncryption(new byte[keyLength]));
    }

    [Fact]
    public void Encrypt_rejects_null_plaintext()
    {
        // null is distinct from empty-string (which IS supported, see round-trip theory).
        // Caller bug if they pass null — surface it loudly instead of silently encrypting
        // the literal "null" text.
        var sut = MakeSut();
        Should.Throw<ArgumentNullException>(() => sut.Encrypt(null!));
    }

    // ─── Utility: BOM + non-ASCII bytes encode identically ───────────────────

    [Fact]
    public void Encrypt_treats_input_as_utf8_no_BOM_no_surrogate_loss()
    {
        var sut = MakeSut();
        const string plaintext = "🎯 — em-dash and emoji";

        var blob = sut.Encrypt(plaintext);
        var roundTripped = sut.Decrypt(blob);

        roundTripped.ShouldBe(plaintext);
        // Sanity: the raw UTF-8 byte count is recoverable from blob length.
        var rawBytes = Encoding.UTF8.GetBytes(plaintext);
        blob.Length.ShouldBe(12 + rawBytes.Length + 16);
    }
}
