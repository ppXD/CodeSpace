using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Runtime;

/// <summary>
/// Taint-aware redaction service. Every engine persistence point (node.started inputs/config,
/// OutputsJson, external_call payloads) routes through here before writing to the ledger, so
/// a Secret-typed variable's resolved value never lands as plaintext in
/// <c>workflow_run_record.payload_json</c>.
///
/// <para>Policy — per-key granularity at the bag level. For each (key, value) pair in the
/// resolved bag the redactor inspects the corresponding subtree of the ORIGINAL template.
/// If any <c>{{path}}</c> or <c>$ref</c> reference inside that subtree targets a path in
/// <paramref name="secretPaths"/>, the whole value at that key is replaced with the marker
/// <c>"[REDACTED: &lt;path&gt;]"</c> (preserves the offending path in the marker so authors
/// can diagnose without seeing the value).</para>
///
/// <para>Why bag-key granularity and not deep AST surgery: a mixed string like
/// <c>"Bearer {{team.API_KEY}}"</c> would otherwise need character-level cutting that's
/// fragile. Replacing the whole key is safe + simple. If an author wants partial-secret
/// behaviour (logging the prefix but redacting the suffix), they split into two separate
/// input keys.</para>
///
/// <para>Out of scope for this redactor: node-emitted outputs (the node is responsible for
/// not putting secrets in its own outputs — there's no template for the redactor to read).
/// The Terminal-output leak guard still catches the case where an operator tries to surface
/// a secret as a workflow Output via {{ref}}.</para>
/// </summary>
public interface IPayloadRedactor
{
    /// <summary>
    /// Returns a NEW dictionary with the same keys; each value is either the original
    /// resolved value (no secret reference in the template) or a redaction marker
    /// (some descendant of the template referenced a secret path).
    /// </summary>
    /// <param name="originalTemplate">
    /// The pre-resolution JsonElement from <c>NodeDefinition.Inputs</c> or <c>NodeDefinition.Config</c>.
    /// MUST be a JSON object whose keys mirror <paramref name="resolvedBag"/>.
    /// </param>
    /// <param name="resolvedBag">The post-resolution bag the engine would otherwise persist.</param>
    /// <param name="secretPaths">
    /// The set of fully-qualified secret-source paths (e.g. "team.API_KEY", "wf.PASSWORD")
    /// that the engine collected at scope-build time. Empty set → fast-path returns
    /// <paramref name="resolvedBag"/> unchanged.
    /// </param>
    IReadOnlyDictionary<string, JsonElement> RedactBag(
        JsonElement originalTemplate,
        IReadOnlyDictionary<string, JsonElement> resolvedBag,
        IReadOnlySet<string> secretPaths);
}
