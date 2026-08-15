using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Credentials;

/// <summary>Pure admission, lifecycle, and opaque-metadata rules for the storage-credential ledger.</summary>
internal static class StorageCredentialRules
{
    private static readonly Regex StableNamePattern = new("^[a-z0-9][a-z0-9-]{0,127}$", RegexOptions.CultureInvariant);

    public static string NormalizeStableName(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!StableNamePattern.IsMatch(normalized)) throw new ArgumentException("StableName must be 1-128 lowercase letters, digits, or hyphens and must start with a letter or digit.");
        return normalized;
    }

    public static string? NormalizeSafeHint(string? value)
    {
        if (value == null) return null;
        var normalized = value.Trim();
        if (normalized.Length == 0 || normalized.EnumerateRunes().Count() > 32 || normalized.Any(char.IsControl))
            throw new ArgumentException("SafeHint must be 1-32 visible characters when provided.");
        return normalized;
    }

    public static void EnsureRotationAllowed(StorageCredentialState state)
    {
        if (state == StorageCredentialState.Active) return;
        if (state == StorageCredentialState.Revoked) throw new ArgumentException("A revoked storage credential is terminal and cannot receive a new revision.");
        throw new ArgumentException($"Storage credential state '{state}' is not supported.");
    }

    public static void EnsureRevocationAllowed(StorageCredentialState state)
    {
        if (state == StorageCredentialState.Active) return;
        if (state == StorageCredentialState.Revoked) throw new ArgumentException("The storage credential is already revoked; revocation is terminal.");
        throw new ArgumentException($"Storage credential state '{state}' is not supported.");
    }

    public static string EnvelopeFingerprint(string ciphertext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ciphertext);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ciphertext))).ToLowerInvariant();
    }

    public static string CredentialRef(Guid credentialId, int revision)
    {
        if (credentialId == Guid.Empty) throw new ArgumentException("Credential id cannot be empty.", nameof(credentialId));
        if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision), revision, "Credential revision must be positive.");
        return $"db:{credentialId:D}:{revision}";
    }
}
