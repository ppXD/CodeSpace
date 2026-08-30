using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using Microsoft.Extensions.Logging;

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
/// convention) and is vanishingly unlikely to collide with real output data. Display resolution is fail-SAFE: a ref
/// whose artifact is missing, cross-team or unreadable is left verbatim rather than dropped — carrying a
/// <see cref="ReasonKey"/> naming the lane that failed, so the reader is told rather than shown a bare pointer.
/// Required execution resolution is fail-CLOSED via <see cref="ResolveRequiredAsync"/>, so replay never
/// feeds the pointer itself to a downstream node. Offload is idempotent — an already-offloaded ref is passed through,
/// never double-wrapped.</para>
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

    /// <summary>The key a SHED reference carries inside its marker, naming the storage lane that kept the value from coming back.</summary>
    public const string ReasonKey = "reason";

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
    /// from <paramref name="store"/>. Non-ref values pass through; a ref whose bytes do not come back is left verbatim
    /// plus the reason they did not (fail-safe — never drop the structure). Shedding is PER PROPERTY, so one rotted
    /// value costs the reader that value and never its neighbours.
    /// </summary>
    public static async Task<Dictionary<string, JsonElement>> ResolveAsync(IArtifactStore store, ILogger logger, Guid teamId, IReadOnlyDictionary<string, JsonElement> outputs, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, JsonElement>(outputs.Count);

        foreach (var (key, value) in outputs)
            result[key] = await ResolveValueAsync(store, logger, teamId, value, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Execution/replay counterpart to <see cref="ResolveAsync"/>. Every well-formed reference must produce verified
    /// JSON bytes; missing metadata, unavailable storage, corrupt bytes, and malformed reference markers raise a typed
    /// <see cref="ArtifactContentUnavailableException"/> instead of allowing the pointer object into model inputs,
    /// map branch space, or loop state. Non-reference values pass through byte-for-byte.
    /// </summary>
    public static async Task<Dictionary<string, JsonElement>> ResolveRequiredAsync(IArtifactStore store, Guid teamId, IReadOnlyDictionary<string, JsonElement> outputs, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, JsonElement>(outputs.Count);

        foreach (var (key, value) in outputs)
            result[key] = await ResolveRequiredValueAsync(store, teamId, value, cancellationToken).ConfigureAwait(false);

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

    private static async Task<JsonElement> ResolveValueAsync(IArtifactStore store, ILogger logger, Guid teamId, JsonElement value, CancellationToken cancellationToken)
    {
        if (!TryReadRefId(value, out var artifactId)) return value;

        try
        {
            var artifact = await store.GetBytesAsync(teamId, artifactId, cancellationToken).ConfigureAwait(false);

            if (artifact is null) return ShedProperty(logger, value, artifactId, ArtifactContentUnavailableKind.MetadataMissing, cause: null);

            using var doc = JsonDocument.Parse(artifact.Bytes);
            return doc.RootElement.Clone();
        }
        catch (ArtifactContentUnavailableException ex)
        {
            // Per PROPERTY, and only the store's own typed verdict about THESE bytes. Anything else is a bug or an
            // outage rather than one unreadable object, and shedding it would report a healthy read built on cells
            // nobody actually read.
            return ShedProperty(logger, value, artifactId, ex.Kind, ex);
        }
        catch (JsonException ex)
        {
            return ShedProperty(logger, value, artifactId, ArtifactContentUnavailableKind.IntegrityFailure, ex);
        }
    }

    /// <summary>
    /// One property's shed, and the ONE place it is announced. This is where a reader silently trades a value for a
    /// pointer, so it is the boundary that owes the backend a trace: the per-CELL boundary above this walk logs its
    /// own, but nothing reaches it while this shed holds, which would leave a destination that has started rotting
    /// with no server-side signal at all. Warning, once per shed property — the run still answers.
    /// </summary>
    private static JsonElement ShedProperty(ILogger logger, JsonElement value, Guid artifactId, ArtifactContentUnavailableKind reason, Exception? cause)
    {
        logger.LogWarning(cause, "Offloaded output artifact {ArtifactId} could not be read ({ArtifactFailureKind}); that property keeps its pointer carrying the reason and its neighbours are unaffected", artifactId, reason);

        return Shed(value, reason);
    }

    /// <summary>
    /// Every reference in <paramref name="outputs"/>, shed onto the SAME marker one property's shed writes. For a
    /// caller that lost a whole cell at once — an isolation boundary above this walk — so the outcome a reader sees is
    /// the same shape whether one property or the cell failed, rather than a bare pointer with no account of it.
    /// Non-reference values are passed through unchanged.
    /// </summary>
    public static Dictionary<string, JsonElement> ShedAll(IReadOnlyDictionary<string, JsonElement> outputs, ArtifactContentUnavailableKind reason)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        var result = new Dictionary<string, JsonElement>(outputs.Count);

        foreach (var (key, value) in outputs)
            result[key] = IsRef(value) ? Shed(value, reason) : value;

        return result;
    }

    /// <summary>
    /// The shed form of a reference whose bytes did not come back: the pointer the ledger already holds, carrying the
    /// lane that failed. Both sheds converge here, so a reader is never shown a bare pointer with no account of it.
    /// </summary>
    private static JsonElement Shed(JsonElement value, ArtifactContentUnavailableKind reason)
    {
        var marker = new Dictionary<string, JsonElement>();

        foreach (var property in value.GetProperty(RefKey).EnumerateObject())
            marker[property.Name] = property.Value;

        marker[ReasonKey] = JsonSerializer.SerializeToElement(reason.ToString());

        return JsonSerializer.SerializeToElement(new Dictionary<string, object> { [RefKey] = marker });
    }

    private static async Task<JsonElement> ResolveRequiredValueAsync(IArtifactStore store, Guid teamId, JsonElement value, CancellationToken cancellationToken)
    {
        if (!TryReadRefId(value, out var artifactId))
        {
            if (HasRefMarker(value)) throw new ArtifactContentUnavailableException(Guid.Empty, ArtifactContentUnavailableKind.IntegrityFailure);
            return value;
        }

        try
        {
            var artifact = await store.GetBytesAsync(teamId, artifactId, cancellationToken).ConfigureAwait(false);
            if (artifact is null) throw new ArtifactContentUnavailableException(artifactId, ArtifactContentUnavailableKind.MetadataMissing);

            using var doc = JsonDocument.Parse(artifact.Bytes);
            return doc.RootElement.Clone();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArtifactContentUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ArtifactReadFailureClassifier.TryClassify(ex, out var kind))
        {
            throw new ArtifactContentUnavailableException(artifactId, kind, ex);
        }
    }

    /// <summary>Whether the value is an offloaded-value reference object.</summary>
    public static bool IsRef(JsonElement value) => TryReadRefId(value, out _);

    private static bool HasRefMarker(JsonElement value) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(RefKey, out _);

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
