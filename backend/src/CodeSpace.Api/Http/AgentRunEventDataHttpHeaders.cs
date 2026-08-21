namespace CodeSpace.Api.Http;

/// <summary>Canonical response headers for one bounded Agent Run event structured-payload read.</summary>
public static class AgentRunEventDataHttpHeaders
{
    public const string AgentRunId = "X-CodeSpace-Agent-Run-Id";
    public const string EventSequence = "X-CodeSpace-Agent-Event-Sequence";
    public const string ArtifactId = "X-CodeSpace-Agent-Event-Data-Artifact-Id";
    public const string Offset = "X-CodeSpace-Agent-Event-Data-Offset";
    public const string NextOffset = "X-CodeSpace-Agent-Event-Data-Next-Offset";
    public const string TotalBytes = "X-CodeSpace-Agent-Event-Data-Total-Bytes";
    public const string Sha256 = "X-CodeSpace-Agent-Event-Data-Sha256";
    public const string ContentType = "X-CodeSpace-Agent-Event-Data-Content-Type";
    public const string IntegrityVerified = "X-CodeSpace-Agent-Event-Data-Integrity-Verified";

    public static readonly string[] RangeResponseHeaders =
    {
        AgentRunId, EventSequence, ArtifactId, Offset, NextOffset, TotalBytes, Sha256, ContentType, IntegrityVerified,
    };
}
