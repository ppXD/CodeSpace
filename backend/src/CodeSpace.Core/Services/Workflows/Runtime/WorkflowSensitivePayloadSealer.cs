using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Workflows;

namespace CodeSpace.Core.Services.Workflows.Runtime;

/// <summary>
/// Seals a payload that one run hands to another — an agent's task, a child workflow's inputs — before it lands in a
/// column other readers see. The public copy has every given secret value masked the way a ledger row is; the exact
/// copy, together with those values, is encrypted under the same Data Protection purpose as the node-output sidecar.
/// The receiving run opens the exact copy to execute and keeps the values as secrets in everything it persists.
///
/// <para>A payload that carries none of the given secrets is returned verbatim with no ciphertext, so a row written
/// for it is byte-identical to one written before sealing existed — and <see cref="Open"/> reads such a row (and every
/// pre-sealing row) straight from its public column.</para>
/// </summary>
public sealed class WorkflowSensitivePayloadSealer : ISingletonDependency
{
    /// <summary>The versioned shape inside the ciphertext. Checked on open, so a ciphertext of another kind sharing the protector purpose (a node-output sidecar) is refused rather than misread.</summary>
    internal const string EnvelopeKind = "sealed.payload.v1";

    private readonly IWorkflowSensitivePayloadProtector _protector;

    public WorkflowSensitivePayloadSealer(IWorkflowSensitivePayloadProtector protector) => _protector = protector;

    /// <summary>Mask the <paramref name="secrets"/> <paramref name="payload"/> embeds for its public column; encrypt the exact payload together with exactly those values when anything was masked.</summary>
    public SealedPayload Seal(JsonElement payload, PersistenceSecretRedactor secrets)
    {
        var embedded = secrets.FoundIn(payload);

        if (embedded.Count == 0) return new SealedPayload(payload.GetRawText(), null);

        var masked = secrets.Redact(payload).Value.GetRawText();
        var envelope = JsonSerializer.Serialize(new SealedEnvelope(EnvelopeKind, payload, embedded));

        return new SealedPayload(masked, _protector.Protect(envelope));
    }

    /// <summary>The exact payload and the secret values it embeds — read from <paramref name="ciphertext"/> when the row was sealed, else <paramref name="publicJson"/> is itself exact and embeds none.</summary>
    public OpenedPayload Open(string publicJson, string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return new OpenedPayload(publicJson, []);

        var envelope = JsonSerializer.Deserialize<SealedEnvelope>(_protector.Unprotect(ciphertext));

        if (envelope is null || envelope.Kind != EnvelopeKind)
            throw new InvalidOperationException($"A sealed payload has an unexpected envelope kind '{envelope?.Kind}'.");

        return new OpenedPayload(envelope.Payload.GetRawText(), envelope.Secrets);
    }

    private sealed record SealedEnvelope(string Kind, JsonElement Payload, IReadOnlyList<string> Secrets);
}
