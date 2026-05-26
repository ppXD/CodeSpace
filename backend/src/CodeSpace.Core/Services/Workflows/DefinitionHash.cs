using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
///   • Properties serialised in stable order (record-shape order)
///   • No whitespace / indentation
///   • Null properties omitted
/// Two logically-equal definitions written by different clients (or by the same client
/// across formatter changes) MUST produce the same hash.</para>
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

        // Serialise via the canonical options shared with the workflow engine so any
        // future tweak to the definition wire format automatically updates the hash basis.
        // null-omit + no-indent is the canonical shape; record property order is stable
        // by IL ordering (System.Text.Json reflects in declaration order).
        var canonicalJson = JsonSerializer.Serialize(definition, CanonicalOptions);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions CanonicalOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
