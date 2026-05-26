using System.Text.Json;
using CodeSpace.Messages.Events;

namespace CodeSpace.Core.Services.Workflows.RunSources;

/// <summary>
/// One matcher per source TYPE; answers "given this NormalizedEvent and this
/// <c>workflow_activation</c> row, should the workflow fire?".
///
/// The dispatcher loops every active activation whose <c>type_key</c> matches this matcher's
/// <see cref="TypeKey"/>, calls <see cref="Match"/>, and on true creates a
/// <c>workflow_run_request</c> + downstream <c>workflow_run</c>.
///
/// New event-triggered behaviours land as new matcher classes — zero engine changes.
/// </summary>
public interface IRunSourceMatcher
{
    /// <summary>e.g. "trigger.pr.opened". Matches workflow_activation.type_key AND the trigger node's INodeRuntime.TypeKey.</summary>
    string TypeKey { get; }

    /// <summary>
    /// True iff this NormalizedEvent should fire a workflow with the given activation config.
    /// MUST be pure — no DB / network — so the dispatcher can call it under a tight loop
    /// inside a single transaction.
    /// </summary>
    bool Match(NormalizedEvent normalizedEvent, JsonElement activationConfig);

    /// <summary>
    /// Build the normalised payload that becomes
    /// <c>workflow_run_request.normalized_payload_json</c> and the StartNode's outputs.
    /// Decouples "what the matcher saw" from "what the node graph reads" — gives the matcher
    /// a chance to flatten / rename fields for ergonomic downstream <c>{{ref}}</c> paths.
    /// </summary>
    JsonElement BuildPayload(NormalizedEvent normalizedEvent);
}
