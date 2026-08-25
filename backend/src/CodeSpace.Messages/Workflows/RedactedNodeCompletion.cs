using System.Text.Json;

namespace CodeSpace.Messages.Workflows;

/// <summary>
/// One <c>node.completed</c> whose outputs a persistence redactor actually changed: the public row carries the
/// REDACTED copy and the originals belong in the encrypted same-record sidecar. Travels as one envelope because
/// the ledger write and the "this row is redacted" claim must be the same INSERT — a claim written separately
/// could roll back on its own, leaving a redacted row that reads as trustworthy.
/// </summary>
public sealed record RedactedNodeCompletion(Guid RunId, string NodeId, string IterationKey, IReadOnlyDictionary<string, JsonElement> RedactedOutputs, IReadOnlyList<string>? RoutingHints, TimeSpan Duration);
