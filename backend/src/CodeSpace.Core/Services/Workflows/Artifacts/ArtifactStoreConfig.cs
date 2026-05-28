namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// Configuration for the artifact store. Currently a single tunable: the inline-vs-URL
/// threshold. Pinned via env var so air-gapped operators can adjust without a code change.
/// </summary>
public static class ArtifactStoreConfig
{
    /// <summary>
    /// Env var: max bytes stored inline in <c>workflow_artifact.inline_bytes</c>. Anything
    /// larger is rejected. Default 8 KiB — large enough for typical LLM completions and small
    /// HTTP responses, small enough to keep the DB row size bounded.
    /// </summary>
    public const string InlineThresholdEnvVar = "CODESPACE_ARTIFACT_INLINE_THRESHOLD_BYTES";

    /// <summary>Default value when the env var is unset. 8 KiB = 8192 bytes.</summary>
    public const int DefaultInlineThresholdBytes = 8 * 1024;

    /// <summary>Resolved threshold; env var if set + valid, otherwise the default.</summary>
    public static int InlineThresholdBytes
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(InlineThresholdEnvVar);
            if (string.IsNullOrWhiteSpace(raw)) return DefaultInlineThresholdBytes;
            if (int.TryParse(raw, out var parsed) && parsed > 0) return parsed;
            return DefaultInlineThresholdBytes;
        }
    }
}
