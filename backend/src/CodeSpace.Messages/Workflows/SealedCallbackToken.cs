namespace CodeSpace.Messages.Workflows;

/// <summary>
/// A freshly minted callback-wait token in the three forms its wait row stores. <see cref="Token"/> is the bearer secret the
/// callback URL carries, kept in the token column for one release so a pod on the previous build — which compares that
/// column with the presented token — still resumes the URL; <see cref="Digest"/> (its SHA-256) is what a resume looks the
/// wait up by; <see cref="Ciphertext"/> is the token AES-GCM encrypted (base64) under the deployment's environment-held
/// variable master key, for the RunsControl-gated reveal — nothing the database holds opens it.
/// </summary>
public sealed record SealedCallbackToken(string Token, string Digest, string Ciphertext);
