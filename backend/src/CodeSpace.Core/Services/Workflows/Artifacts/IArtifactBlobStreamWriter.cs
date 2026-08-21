namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// Optional sibling capability for a legacy artifact blob backend that can consume a bounded-memory stream. Keeping
/// it separate from <see cref="IArtifactBlobBackend"/> preserves existing/custom backends; a streaming caller checks
/// this capability explicitly and never disguises a missing implementation with a whole-payload fallback.
/// </summary>
public interface IArtifactBlobStreamWriter
{
    /// <summary>
    /// Persist exactly <paramref name="contentLength"/> bytes whose digest is <paramref name="sha256"/>. The writer
    /// must verify both claims, must not dispose <paramref name="content"/>, and must not expose a partial object when
    /// the copy fails or is cancelled.
    /// </summary>
    Task<string> WriteStreamAsync(string sha256, Stream content, long contentLength, CancellationToken cancellationToken);
}
