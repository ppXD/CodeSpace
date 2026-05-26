using System.Text;
using CodeSpace.Core.Services.Workflows.Artifacts;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pure-function tests for the SHA-256 hashing primitive + the threshold config. The hash
/// is the artifact's identity; drift in the hash produces de-duplication breakage at scale
/// (every previously-stored byte sequence would suddenly look "new"), so we pin a handful
/// of known digests against the canonical algorithm.
/// </summary>
public class ArtifactStoreShaTests
{
    [Theory]
    // Hex-lowercase SHA-256 reference vectors from NIST FIPS 180-4 examples / RFC 6234.
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("hello world", "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9")]
    public void Sha256_matches_canonical_reference_vectors(string input, string expectedHex)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hex = ArtifactStore.ComputeSha256Hex(bytes);
        hex.ShouldBe(expectedHex);
    }

    [Fact]
    public void Sha256_is_64_chars_lowercase_hex()
    {
        var hex = ArtifactStore.ComputeSha256Hex(Encoding.UTF8.GetBytes("anything"));
        hex.Length.ShouldBe(64);
        hex.ShouldMatch("^[0-9a-f]{64}$",
            "the DB column has a CHECK constraint matching this regex; lowercase hex only");
    }

    [Fact]
    public void Sha256_is_deterministic_same_bytes_same_digest()
    {
        var a = ArtifactStore.ComputeSha256Hex(Encoding.UTF8.GetBytes("the same"));
        var b = ArtifactStore.ComputeSha256Hex(Encoding.UTF8.GetBytes("the same"));
        a.ShouldBe(b, "SHA-256 is deterministic; this is the property that makes dedup work");
    }

    [Fact]
    public void Sha256_differs_for_different_bytes()
    {
        var a = ArtifactStore.ComputeSha256Hex(Encoding.UTF8.GetBytes("alpha"));
        var b = ArtifactStore.ComputeSha256Hex(Encoding.UTF8.GetBytes("beta"));
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inline_threshold_env_var_name_pinned_for_operators()
    {
        // Rule 8 — the env var name is a public contract for air-gapped operators who pin
        // the threshold in their environment config. Renaming silently breaks their setup.
        ArtifactStoreConfig.InlineThresholdEnvVar.ShouldBe("CODESPACE_ARTIFACT_INLINE_THRESHOLD_BYTES");
    }

    [Fact]
    public void Default_inline_threshold_is_8KiB()
    {
        ArtifactStoreConfig.DefaultInlineThresholdBytes.ShouldBe(8 * 1024);
    }

    [Fact]
    public void Threshold_resolves_to_default_when_env_unset()
    {
        var original = Environment.GetEnvironmentVariable(ArtifactStoreConfig.InlineThresholdEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ArtifactStoreConfig.InlineThresholdEnvVar, null);
            ArtifactStoreConfig.InlineThresholdBytes.ShouldBe(ArtifactStoreConfig.DefaultInlineThresholdBytes);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ArtifactStoreConfig.InlineThresholdEnvVar, original);
        }
    }

    [Theory]
    [InlineData("4096", 4096)]
    [InlineData("65536", 65536)]
    [InlineData("1", 1)]
    public void Threshold_resolves_from_env_when_set_to_positive_integer(string envValue, int expected)
    {
        var original = Environment.GetEnvironmentVariable(ArtifactStoreConfig.InlineThresholdEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ArtifactStoreConfig.InlineThresholdEnvVar, envValue);
            ArtifactStoreConfig.InlineThresholdBytes.ShouldBe(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ArtifactStoreConfig.InlineThresholdEnvVar, original);
        }
    }

    [Theory]
    // Garbage / negative / zero — fall back to default so a typo in env doesn't disable artifacts.
    [InlineData("not-a-number")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("   ")]
    public void Threshold_falls_back_to_default_for_invalid_env(string envValue)
    {
        var original = Environment.GetEnvironmentVariable(ArtifactStoreConfig.InlineThresholdEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ArtifactStoreConfig.InlineThresholdEnvVar, envValue);
            ArtifactStoreConfig.InlineThresholdBytes.ShouldBe(ArtifactStoreConfig.DefaultInlineThresholdBytes);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ArtifactStoreConfig.InlineThresholdEnvVar, original);
        }
    }
}
