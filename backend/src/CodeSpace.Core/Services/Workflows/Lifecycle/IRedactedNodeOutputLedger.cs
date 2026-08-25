using CodeSpace.Messages.Workflows;

namespace CodeSpace.Core.Services.Workflows.Lifecycle;

/// <summary>
/// The one capability <see cref="IRunRecordLogger"/> deliberately does NOT carry: writing a <c>node.completed</c>
/// that ADMITS its outputs are a redacted stand-in. Kept a sibling rather than a widening because the admission
/// only makes sense for the engine's redaction path — every other emitter (a plugin's <c>external_call</c>, the
/// rerun seeder's cell clone, a test's hand-written row) has nothing to admit and must not be forced to answer.
///
/// <para>Feature-detected at exactly one consumer (the engine's node-completion write). An implementation that
/// does NOT offer it falls back to the plain <see cref="IRunRecordLogger.NodeCompletedAsync"/>, whose rows carry
/// no claim — the conservative default, and the reading every row written before this interface existed already
/// has.</para>
/// </summary>
public interface IRedactedNodeOutputLedger
{
    /// <summary>Emit <c>node.completed</c> carrying the redacted outputs AND the flag that says so, in one INSERT. Returns the new record id, which the encrypted recovery sidecar must bind to.</summary>
    Task<Guid> NodeCompletedRedactedAsync(RedactedNodeCompletion completion, CancellationToken cancellationToken);
}
