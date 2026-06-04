using System.Text.Json;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes;

/// <summary>
/// What a node returns from <c>RunAsync</c>. The engine reads <see cref="Status"/> to decide
/// what to do next:
///  - <c>Success</c>: persist outputs, advance to downstream nodes
///  - <c>Skipped</c>: no outputs (or empty), treat as success-with-no-effect
///  - <c>Failure</c>: persist <see cref="Error"/>, halt the run
///  - <c>Suspended</c>: persist <see cref="SuspendUntil"/>, idle until resume
///
/// Convenience factory methods are provided to keep node code readable —
/// <c>return NodeResult.Ok(outputs)</c> reads better than constructing the record by hand.
/// </summary>
public sealed record NodeResult
{
    public required NodeStatus Status { get; init; }
    public IReadOnlyDictionary<string, JsonElement> Outputs { get; init; } = new Dictionary<string, JsonElement>();
    public string? Error { get; init; }

    /// <summary>Set when Status == Suspended. Opaque token the resume mechanism feeds back.</summary>
    public SuspensionToken? SuspendUntil { get; init; }

    /// <summary>
    /// For branch / multi-output nodes: which output handle(s) the engine should follow.
    /// Null = follow all outgoing edges (default single-handle behaviour). When set, the
    /// engine fires only edges whose <c>SourceHandle</c> matches one of these names.
    /// Edges whose source handle is unlisted produce Skipped downstream nodes (the standard
    /// skip-propagation rules apply).
    /// </summary>
    public IReadOnlyList<string>? RoutingHints { get; init; }

    public static NodeResult Ok(IReadOnlyDictionary<string, JsonElement>? outputs = null) =>
        new() { Status = NodeStatus.Success, Outputs = outputs ?? new Dictionary<string, JsonElement>() };

    /// <summary>Branch-friendly factory: success + restrict downstream routing to the named handles.</summary>
    public static NodeResult Route(IReadOnlyList<string> handles, IReadOnlyDictionary<string, JsonElement>? outputs = null) =>
        new() { Status = NodeStatus.Success, Outputs = outputs ?? new Dictionary<string, JsonElement>(), RoutingHints = handles };

    public static NodeResult Fail(string error) => new() { Status = NodeStatus.Failure, Error = error };

    public static NodeResult Skip() => new() { Status = NodeStatus.Skipped };

    public static NodeResult Suspend(SuspensionToken token) =>
        new() { Status = NodeStatus.Suspended, SuspendUntil = token };
}

/// <summary>
/// Opaque marker the engine persists when a node returns <c>Suspended</c>. Carries the
/// kind + payload the resume mechanism uses (an approval token, a sleep-until timestamp,
/// an external-callback id).
/// </summary>
public sealed record SuspensionToken
{
    public required string Kind { get; init; }
    public required JsonElement Payload { get; init; }

    /// <summary>
    /// Optional caller-supplied correlation token for the wait. When set, the engine uses it as the
    /// wait's <c>Token</c> instead of minting a fresh Guid — letting one node pre-share the token with
    /// another (e.g. <c>chat.post_message</c> mints it for the card; <c>flow.wait_action</c> parks on
    /// the SAME token, so a button click resolves exactly this wait). Null ⇒ the engine mints one (the
    /// default for Timer / Approval / Callback). Ignored for Subworkflow, whose token is the child run id.
    /// </summary>
    public string? CorrelationToken { get; init; }

    /// <summary>
    /// Optional deadline for a BOUNDED wait. When set, the engine schedules a resume at this instant
    /// that — if the wait is still pending — resolves it with <see cref="TimeoutPayload"/> (a person
    /// never responded). Generic: any wait can be bounded; the node decides the default outcome. Null ⇒
    /// the wait is unbounded (the prior behaviour). Independent of a Timer wait's own wake_at.
    /// </summary>
    public DateTimeOffset? DeadlineAt { get; init; }

    /// <summary>
    /// The resume payload injected as this node's <c>ResumePayload</c> when <see cref="DeadlineAt"/>
    /// fires — i.e. the default decision on timeout (e.g. <c>{ action, by, _timedOut }</c>). Required
    /// when <see cref="DeadlineAt"/> is set; ignored otherwise. The node reads it on the resumed pass
    /// exactly like a human response.
    /// </summary>
    public JsonElement? TimeoutPayload { get; init; }
}
