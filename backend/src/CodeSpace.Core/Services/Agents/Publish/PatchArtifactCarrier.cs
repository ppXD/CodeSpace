using CodeSpace.Core.Services.Workflows.Artifacts;

namespace CodeSpace.Core.Services.Agents.Publish;

/// <summary>
/// The closed two-carrier reader for a recorded patch. An artifact reference is the producer's authoritative full
/// patch; any inline text beside it is only a bounded compatibility copy and must never mask an unavailable, corrupt,
/// purged, or foreign artifact. Without a reference, the inline bytes remain byte-identical and storage is untouched.
/// This policy is patch-specific: transcripts and other generic carriers retain <see cref="IArtifactOffloader.ResolveAsync"/>'s
/// existing inline-first behavior.
/// </summary>
public static class PatchArtifactCarrier
{
    public static Task<string> ResolvePatchRequiredAsync(this IArtifactOffloader offloader, Guid teamId, string? inlinePatch, Guid? patchArtifactId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offloader);
        return patchArtifactId is { } artifactId
            ? offloader.ResolveRequiredAsync(teamId, "", artifactId, cancellationToken)
            : Task.FromResult(inlinePatch ?? "");
    }
}
