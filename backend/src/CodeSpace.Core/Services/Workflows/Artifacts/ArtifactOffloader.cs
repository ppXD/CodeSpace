using System.Text;
using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// The generic field-level offloader (see <see cref="IArtifactOffloader"/>): one place that owns the
/// size-routing policy (UTF-8 length vs <see cref="ArtifactStoreConfig.InlineThresholdBytes"/>) over the
/// content-addressed <see cref="IArtifactStore"/>. Every producer that has a potentially-large text field
/// (agent diff / stderr / transcript, event data_json, …) calls this instead of re-deriving the threshold +
/// PutAsync + clear-inline dance.
/// </summary>
public sealed class ArtifactOffloader : IArtifactOffloader, IScopedDependency
{
    private readonly IArtifactStore _store;

    public ArtifactOffloader(IArtifactStore store) { _store = store; }

    public async Task<OffloadedText> OffloadIfLargeAsync(Guid teamId, string? text, string contentType, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text)) return new OffloadedText("", null);

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= ArtifactStoreConfig.InlineThresholdBytes) return new OffloadedText(text, null);

        var artifactId = await _store.PutAsync(teamId, bytes, contentType, cancellationToken).ConfigureAwait(false);

        return new OffloadedText("", artifactId);
    }

    public async Task<string> ResolveAsync(Guid teamId, string? inline, Guid? artifactId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(inline)) return inline;
        if (artifactId is not { } id) return "";

        var bytes = await _store.GetBytesAsync(teamId, id, cancellationToken).ConfigureAwait(false);

        return bytes == null ? "" : Encoding.UTF8.GetString(bytes.Bytes);
    }
}
