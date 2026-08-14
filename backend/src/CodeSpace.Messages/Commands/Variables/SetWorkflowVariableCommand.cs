using System.Text.Json;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Variables;

/// <summary>
/// Upsert a workflow-scoped variable. Tenant guard: the service rejects with
/// <see cref="KeyNotFoundException"/> if the workflow doesn't belong to the caller's
/// current team. <see cref="WorkflowId"/> comes from the URL — controller does
/// <c>command with { WorkflowId = routeId }</c> before dispatch.
/// </summary>
public sealed record SetWorkflowVariableCommand : ICommand<Unit>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.VariablesWrite;

    public Guid WorkflowId { get; init; }
    public required string Name { get; init; }
    public required VariableValueType ValueType { get; init; }
    /// <summary>
    /// The new value, or NULL for "leave the stored value alone".
    ///
    /// <para>Optional because the panel edits a variable's description and name on their own, and a
    /// caller with no value to send had no way to say so: it had to invent one. For a Secret it
    /// invented the empty string — the plaintext is never returned to a client, so there was nothing
    /// else to send — and this handler encrypted it over the real credential, which no copy of
    /// existed anywhere. Absent now means absent.</para>
    /// </summary>
    public JsonElement? Value { get; init; }

    /// <summary>
    /// The name this variable is CURRENTLY stored under, when this write is a rename. Null otherwise.
    ///
    /// <para>A rename has to be server-side: the row is found by its old name and the new name is
    /// moved onto it, so the value never has to be reproduced. The client cannot reproduce a Secret —
    /// it is never given the plaintext — and the previous delete-then-recreate replaced every renamed
    /// secret with an encrypted empty string.</para>
    /// </summary>
    public string? RenameFrom { get; init; }
    public string? Description { get; init; }
}
