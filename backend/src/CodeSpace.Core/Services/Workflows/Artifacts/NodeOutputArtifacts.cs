using System.Text;
using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// The "selective leaf-value offload" for a node's <c>outputs_jsonb</c> that <see cref="IArtifactOffloader"/>
/// deliberately does NOT do (offloading a whole field would break <c>{{nodes.X.outputs.foo}}</c> resolution).
/// This is the separate concern named in that interface's doc: it walks a node's output properties and, for any
/// value whose serialized size exceeds the threshold, moves the value's raw JSON into the content-addressed
/// <see cref="IArtifactStore"/> and replaces it with a compact reference object — so an oversize HTTP body / LLM
/// completion never lands inline in the append-only, never-deleted run-record ledger, yet the output STRUCTURE
/// (the keys) is preserved so resolution still navigates it.
///
/// <para>The reference shape is <c>{"$artifact_ref":{"id":"&lt;guid&gt;","size_bytes":N,"content_type":"…"}}</c> —
/// the <c>$</c>-prefixed key marks it as a pointer (mirroring <c>NodeObservability.PersistArtifactAsync</c>'s
/// convention) and is vanishingly unlikely to collide with real output data. Resolution is fail-SAFE: a ref whose
/// artifact is missing / cross-team is left verbatim rather than dropped, so a storage miss never loses the
/// structure. Offload is idempotent — an already-offloaded ref is passed through, never double-wrapped.</para>
///
/// <para>Offload touches only the LEDGER copy of a node's outputs; the engine keeps the FULL values in the
/// in-process scope (via MergeNodeOutcome), so a single-pass walk resolves <c>{{nodes.X.outputs.*}}</c> against
/// the real values with no fetch. Refs are re-inflated only when scope is rebuilt FROM the ledger on crash-resume
/// / map / loop replay (<see cref="ResolveAsync"/>).</para>
/// </summary>
public static class NodeOutputArtifacts
{
    /// <summary>The marker key whose presence identifies an offloaded-value reference object inside an output.</summary>
    public const string RefKey = "$artifact_ref";

    private const string OutputContentType = "application/json";

    /// <summary>
    /// Return a copy of <paramref name="outputs"/> in which any property value whose UTF-8 serialized size exceeds
    /// <paramref name="thresholdBytes"/> is offloaded to <paramref name="store"/> and replaced by a ref. Values
    /// within budget — and values already a ref (idempotent) — are passed through unchanged. <paramref name="thresholdBytes"/>
    /// &lt;= 0 disables offload.
    /// </summary>
    public static async Task<Dictionary<string, JsonElement>> OffloadLargeAsync(IArtifactStore store, Guid teamId, IReadOnlyDictionary<string, JsonElement> outputs, int thresholdBytes, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, JsonElement>(outputs.Count);

        foreach (var (key, value) in outputs)
            result[key] = await OffloadValueAsync(store, teamId, value, thresholdBytes, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// The reverse of <see cref="OffloadLargeAsync"/>: replace every ref value with its stored content fetched
    /// from <paramref name="store"/>. Non-ref values pass through; a ref whose artifact is missing / cross-team is
    /// left verbatim (fail-safe — never drop the structure).
    /// </summary>
    public static async Task<Dictionary<string, JsonElement>> ResolveAsync(IArtifactStore store, Guid teamId, IReadOnlyDictionary<string, JsonElement> outputs, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, JsonElement>(outputs.Count);

        foreach (var (key, value) in outputs)
            result[key] = await ResolveValueAsync(store, teamId, value, cancellationToken).ConfigureAwait(false);

        return result;
    }

    private static async Task<JsonElement> OffloadValueAsync(IArtifactStore store, Guid teamId, JsonElement value, int thresholdBytes, CancellationToken cancellationToken)
    {
        if (thresholdBytes <= 0 || TryReadRefId(value, out _)) return value;

        var raw = value.GetRawText();
        var sizeBytes = Encoding.UTF8.GetByteCount(raw);

        if (sizeBytes <= thresholdBytes) return value;

        var artifactId = await store.PutAsync(teamId, Encoding.UTF8.GetBytes(raw), OutputContentType, cancellationToken).ConfigureAwait(false);

        return BuildRef(artifactId, sizeBytes);
    }

    private static async Task<JsonElement> ResolveValueAsync(IArtifactStore store, Guid teamId, JsonElement value, CancellationToken cancellationToken)
    {
        if (!TryReadRefId(value, out var artifactId)) return value;

        var artifact = await store.GetBytesAsync(teamId, artifactId, cancellationToken).ConfigureAwait(false);

        if (artifact is null) return value;   // missing / cross-team — fail-safe: keep the ref rather than lose the value

        using var doc = JsonDocument.Parse(artifact.Bytes);
        return doc.RootElement.Clone();
    }

    /// <summary>Whether the value is an offloaded-value reference object.</summary>
    public static bool IsRef(JsonElement value) => TryReadRefId(value, out _);

    private static JsonElement BuildRef(Guid artifactId, int sizeBytes) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            [RefKey] = new { id = artifactId, size_bytes = sizeBytes, content_type = OutputContentType },
        });

    /// <summary>Parse the artifact id out of a ref value, or false when the value isn't a well-formed ref (an object with a <see cref="RefKey"/> property carrying a Guid <c>id</c>).</summary>
    private static bool TryReadRefId(JsonElement value, out Guid artifactId)
    {
        artifactId = Guid.Empty;

        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(RefKey, out var refObj)) return false;

        if (refObj.ValueKind != JsonValueKind.Object || !refObj.TryGetProperty("id", out var idElement)) return false;

        return idElement.TryGetGuid(out artifactId);
    }
}
