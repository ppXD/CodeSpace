namespace CodeSpace.Core.Services.Variables;

/// <summary>
/// Encryption envelope for any <c>Secret</c>-typed variable value, regardless of scope
/// (Team / Workflow / future). The contract is intentionally symmetric and stateless:
/// one master key in, opaque byte blob out — same shape across every scope so a single
/// rotation tool can re-encrypt the entire <c>variable.value_encrypted</c> column without
/// caring whether a row is team-scoped or workflow-scoped.
///
/// <para>The interface MUST NOT expose nonces, key ids, or envelope internals — those
/// live in the implementation and are pinned by the unit-test envelope layout assertion
/// so capacity planning stays accurate.</para>
/// </summary>
public interface IVariableValueEncryption
{
    /// <summary>
    /// Encrypts a UTF-8 plaintext into a self-contained envelope byte blob suitable for
    /// the <c>variable.value_encrypted</c> column. Output is non-deterministic: two calls
    /// with the same plaintext yield different blobs (random nonce per call), so duplicate
    /// detection across rows is impossible without decryption.
    /// </summary>
    byte[] Encrypt(string plaintext);

    /// <summary>
    /// Reverses <see cref="Encrypt"/>. Throws on any tampering, truncation, key mismatch,
    /// or malformed envelope — the AES-GCM authentication tag fails closed.
    /// </summary>
    string Decrypt(byte[] envelope);
}
