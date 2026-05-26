using System.Security.Cryptography;
using System.Text;

namespace CodeSpace.Core.Services.Variables;

/// <summary>
/// AES-256-GCM implementation of <see cref="IVariableValueEncryption"/>. The envelope
/// layout is <c>[nonce(12) || ciphertext(plaintext-length) || tag(16)]</c> — pinned by
/// <c>AesGcmVariableEncryptionTests.Envelope_layout_*</c> so capacity planning + the
/// future rotation tool can assume the 28-byte overhead per row without re-reading the
/// implementation.
///
/// <para>The master key is 32 raw bytes (AES-256). Operators supply it base64-encoded
/// via <see cref="VariableEncryptionConfig.MasterKeyEnvVar"/>; the DI registration
/// decodes once at startup and constructs a singleton.</para>
///
/// <para>Why GCM, not CBC + HMAC: GCM is authenticated in one primitive, has a much
/// smaller mistake surface, and .NET's <see cref="AesGcm"/> is hardware-accelerated on
/// every modern x86 / ARM target. The tag size is the .NET default 16 bytes — strongest
/// available for AES-GCM.</para>
/// </summary>
public sealed class AesGcmVariableEncryption : IVariableValueEncryption
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MasterKeySize = 32;

    private readonly byte[] _masterKey;

    public AesGcmVariableEncryption(byte[] masterKey)
    {
        if (masterKey == null) throw new ArgumentNullException(nameof(masterKey));
        if (masterKey.Length != MasterKeySize)
            throw new ArgumentException($"Master key must be exactly {MasterKeySize} bytes (AES-256); got {masterKey.Length}.", nameof(masterKey));

        // Defensive copy so callers can't mutate our key after construction.
        _masterKey = new byte[MasterKeySize];
        Buffer.BlockCopy(masterKey, 0, _masterKey, 0, MasterKeySize);
    }

    public byte[] Encrypt(string plaintext)
    {
        if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var envelope = new byte[NonceSize + plaintextBytes.Length + TagSize];

        var nonceSpan = envelope.AsSpan(0, NonceSize);
        var ciphertextSpan = envelope.AsSpan(NonceSize, plaintextBytes.Length);
        var tagSpan = envelope.AsSpan(NonceSize + plaintextBytes.Length, TagSize);

        RandomNumberGenerator.Fill(nonceSpan);

        using var aes = new AesGcm(_masterKey, TagSize);
        aes.Encrypt(nonceSpan, plaintextBytes, ciphertextSpan, tagSpan);

        return envelope;
    }

    public string Decrypt(byte[] envelope)
    {
        if (envelope == null) throw new ArgumentNullException(nameof(envelope));
        if (envelope.Length < NonceSize + TagSize)
            throw new CryptographicException($"Envelope too short ({envelope.Length} bytes); minimum is {NonceSize + TagSize}.");

        var ciphertextLength = envelope.Length - NonceSize - TagSize;
        var nonceSpan = envelope.AsSpan(0, NonceSize);
        var ciphertextSpan = envelope.AsSpan(NonceSize, ciphertextLength);
        var tagSpan = envelope.AsSpan(NonceSize + ciphertextLength, TagSize);
        var plaintextBytes = new byte[ciphertextLength];

        using var aes = new AesGcm(_masterKey, TagSize);
        // Throws CryptographicException on tag mismatch — wrong key, tampered ciphertext,
        // tampered tag, swapped nonce all fail through here. Don't catch + retry: the
        // failure IS the security signal.
        aes.Decrypt(nonceSpan, ciphertextSpan, tagSpan, plaintextBytes);

        return Encoding.UTF8.GetString(plaintextBytes);
    }
}
