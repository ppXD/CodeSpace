using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// Canonical content hash for a <see cref="WorkflowDefinition"/>. Drives the release-identity
/// + tamper-detection mechanism: every <c>workflow_version</c> row captures the hash at
/// INSERT time; every <c>workflow_run</c> captures the same hash at run start; replay
/// verifies the captured hash still matches the version row's stored hash, throwing if
/// anything in the definition has been mutated between original run and replay.
///
/// <para>Canonicalisation rules — pinned by <c>DefinitionHashTests</c>:
///   • SHA-256, output as 64-char lowercase hex
///   • Object keys serialised in ordinal-sorted order, recursively (so the hash is
///     independent of JSON property order)
///   • Array element order preserved (it is semantically meaningful for nodes / edges)
///   • No whitespace / indentation
///   • Null properties omitted
/// Two logically-equal definitions written by different clients (or by the same client
/// across formatter changes) MUST produce the same hash.</para>
///
/// <para><b>Why recursive key sorting is mandatory, not a nicety:</b> the definition is
/// persisted in a PostgreSQL <c>jsonb</c> column, which does NOT preserve object key order —
/// it stores keys in its own internal order (by length, then bytes). The engine recomputes
/// the hash from the round-tripped JSON at run start, so the free-form <c>JsonElement</c>
/// fields (<see cref="NodeDefinition.Config"/> / <see cref="NodeDefinition.Inputs"/>) come
/// back with reordered keys. Hashing in declaration/parse order would therefore diverge from
/// the stored hash on every run of any workflow with a multi-key node config — a false
/// <c>ReleaseTamperedException</c>. Sorting keys on both the publish and the recompute path
/// makes the hash invariant to that storage normalisation.</para>
///
/// <para>The hash captures the DEFINITION shape (graph + IO contract), NOT the surrounding
/// workflow.id or workflow.team_id. This is intentional: the hash answers "is this exact
/// graph?", not "is this exact row?". Future deduplication / template-matching can use
/// the hash as a content-addressed identifier.</para>
/// </summary>
public static class DefinitionHash
{
    /// <summary>
    /// Returns a 64-char lowercase hex SHA-256 of the canonicalised definition.
    /// </summary>
    public static string Compute(WorkflowDefinition definition)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));

        // Serialise via the canonical options shared with the workflow engine, then re-emit
        // with object keys sorted so the hash basis is independent of property order (see the
        // class doc-comment on the jsonb round-trip). null-omit + no-indent is the rest of the
        // canonical shape.
        var tree = JsonSerializer.SerializeToNode(definition, CanonicalOptions);
        var canonicalJson = WriteSortedKeys(tree);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Re-emits a JSON tree with object keys in ordinal order, recursively. Arrays keep their order.</summary>
    private static string WriteSortedKeys(JsonNode? root)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteNode(writer, root);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteNode(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array) WriteNode(writer, item);
                writer.WriteEndArray();
                break;

            case null:
                writer.WriteNullValue();
                break;

            default:
                node.WriteTo(writer);
                break;
        }
    }

    private static readonly JsonSerializerOptions CanonicalOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
