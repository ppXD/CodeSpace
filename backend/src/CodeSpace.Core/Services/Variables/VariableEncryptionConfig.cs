namespace CodeSpace.Core.Services.Variables;

/// <summary>
/// Operator-facing configuration constants for the variable-encryption subsystem.
/// Centralised so the env-var name has a single source of truth — pinned by
/// <c>VariableEncryptionConfigPinningTests</c> (Rule 8) so a rename surfaces as a
/// failing test rather than silently breaking every deployed instance that already
/// configured its key store with this exact name.
/// </summary>
public static class VariableEncryptionConfig
{
    /// <summary>
    /// Environment variable holding the base64-encoded AES-256 master key (32 bytes
    /// decoded). Required at startup in every non-Development environment; missing in
    /// Development falls back to a fixed dev key with a startup warning.
    /// </summary>
    public const string MasterKeyEnvVar = "CODESPACE_VARIABLE_MASTER_KEY";

    /// <summary>
    /// Legacy env var. The registration code falls back to this name when the canonical
    /// <see cref="MasterKeyEnvVar"/> is unset, so existing dev environments and the
    /// integration test fixture don't break during the transition.
    /// </summary>
    public const string LegacyMasterKeyEnvVar = "CODESPACE_TEAM_SECRET_MASTER_KEY";
}
