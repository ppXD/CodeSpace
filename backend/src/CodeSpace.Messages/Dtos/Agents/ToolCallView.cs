using CodeSpace.Messages.Agents;

namespace CodeSpace.Messages.Dtos.Agents;

/// <summary>
/// One audit row of a side-effecting MCP tool call an agent run made — what tool, when, the outcome, and the
/// approval trail. The lean, audit-focused projection of a <c>ToolCallLedger</c> row (like <see cref="AgentRunEventDto"/>
/// is for an <c>AgentRunEvent</c>): an operator reads this to SEE what an agent did. Team-scoped at the read source.
///
/// <para>DELIBERATELY OMITS the raw tool-result body: <c>ResultJson</c> is already redacted at persist time, but a
/// tool-output blob bloats a list view and isn't audit-relevant — the {tool, outcome, when, who-approved} tuple is.
/// Also omits the INTERNAL exactly-once + approval machinery: <c>IdempotencyKey</c> / <c>InputHash</c> (server-derived
/// dedup handles, not operator-facing) and <c>ApprovalToken</c> (a server-side BEARER SECRET — never surfaced to a
/// client). <see cref="Error"/> IS exposed because it is redacted at persist time, the same way
/// <see cref="AgentRunEventDto"/>'s text is.</para>
/// </summary>
public sealed record ToolCallView
{
    /// <summary>The tool that was called, e.g. "git.open_pr" (<c>ToolCallLedger.ToolKind</c>).</summary>
    public required string ToolKind { get; init; }

    /// <summary>Lifecycle outcome — Pending / Succeeded / Failed / Denied / AwaitingApproval / Running / Expired.</summary>
    public required ToolCallLedgerStatus Status { get; init; }

    /// <summary>When the call was claimed (the row was born). The chronological ordering key.</summary>
    public required DateTimeOffset CreatedDate { get; init; }

    /// <summary>When the row last transitioned (terminal record, approval stamp, …).</summary>
    public required DateTimeOffset LastModifiedDate { get; init; }

    /// <summary>Terminal failure / denial reason (already redacted at persist). Null on success / while in flight.</summary>
    public string? Error { get; init; }

    /// <summary>The human who approved this call (the approval audit trail). Null when the call never needed approval or isn't yet approved.</summary>
    public Guid? ApprovedByUserId { get; init; }

    /// <summary>When the call was approved (the approval audit trail). Null until approved.</summary>
    public DateTimeOffset? ApprovedAt { get; init; }
}
