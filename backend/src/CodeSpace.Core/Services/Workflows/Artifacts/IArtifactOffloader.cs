namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// The GENERIC field-level offload primitive over <see cref="IArtifactStore"/>: the single, reusable
/// "a large text field → an artifact reference; a small one stays inline" policy that EVERY producer (agent
/// diff / stderr / transcript, agent-event data_json, …) routes through, so the size-routing + content-type
/// handling lives in ONE place instead of being re-implemented per call site.
///
/// <para>Non-breaking by design: it keeps the established "<c>string Field</c> + optional <c>Guid? FieldArtifactId</c>"
/// shape rather than changing field types — small payloads round-trip unchanged inline; large ones move to the
/// store and the inline field is cleared. Reads resolve transparently (<see cref="ResolveAsync"/>).</para>
///
/// <para>Field-level (not blob-level) on purpose: it offloads a WHOLE field's text, which fits any value that is
/// NOT variable-resolved (an agent patch, an event payload, a transcript). A node's <c>outputs_jsonb</c> — which
/// the engine resolves <c>{{nodes.X.outputs.foo}}</c> against — must NOT be offloaded wholesale (that breaks
/// resolution); selective leaf-value offload for that path is a separate concern, not this primitive.</para>
/// </summary>
public interface IArtifactOffloader
{
    /// <summary>
    /// Offload <paramref name="text"/> when its UTF-8 size exceeds <see cref="ArtifactStoreConfig.InlineThresholdBytes"/>:
    /// returns <c>(Inline: "", ArtifactId: id)</c>. Otherwise returns <c>(Inline: text ?? "", ArtifactId: null)</c>.
    /// Idempotent (the store dedups by sha) — a re-offload of the same content reuses the same id.
    /// </summary>
    Task<OffloadedText> OffloadIfLargeAsync(Guid teamId, string? text, string contentType, CancellationToken cancellationToken);

    /// <summary>
    /// The reverse: the inline text when present; else the full text fetched from the artifact store via
    /// <paramref name="artifactId"/>; else <c>""</c> (neither set, or the artifact is missing / cross-team).
    /// </summary>
    Task<string> ResolveAsync(Guid teamId, string? inline, Guid? artifactId, CancellationToken cancellationToken);
}

/// <summary>The result of <see cref="IArtifactOffloader.OffloadIfLargeAsync"/>: exactly one of <see cref="Inline"/> (small) / <see cref="ArtifactId"/> (offloaded) carries the value.</summary>
public readonly record struct OffloadedText(string Inline, Guid? ArtifactId);
