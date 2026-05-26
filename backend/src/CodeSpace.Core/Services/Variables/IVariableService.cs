using System.Text.Json;
using CodeSpace.Messages.Dtos.Variables;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Variables;

/// <summary>
/// Single CRUD surface for the unified <c>variable</c> table. Scope and value-type are
/// parameters, not concepts that fork the API.
///
/// <para>Two value-bearing paths exist:
/// <list type="bullet">
///   <item><see cref="ListAsync"/> / <see cref="GetAsync"/> — operator-facing. Returns
///         metadata + non-secret value_plain JSON. Secret rows have <c>ValuePlain == null</c>
///         by design — the API NEVER returns secret plaintext.</item>
///   <item><see cref="GetAllForEngineAsync"/> — engine-only. Returns the FULL resolved
///         set (including decrypted secrets) for scope-binding. Only the in-process
///         engine calls this; controllers MUST NOT.</item>
/// </list></para>
///
/// <para>Tenant boundary: operator-facing methods take an <c>expectedTeamId</c> parameter
/// (handlers source it from <c>ICurrentTeam</c>). For Team scope this MUST equal
/// <c>scopeId</c>; for Workflow scope the workflow's owning team MUST equal it. Mismatches
/// throw <see cref="KeyNotFoundException"/> — the standard "scope not found OR not yours"
/// conflation that prevents cross-team enumeration. <see cref="GetAllForEngineAsync"/> is
/// engine-only and skips the tenant guard since the engine already loaded the workflow
/// (and thus its team) before calling.</para>
/// </summary>
public interface IVariableService
{
    /// <summary>
    /// Lists every active variable for the (scope, scopeId). Secret rows have
    /// <c>ValuePlain == null</c>; non-secret rows have the JSON-encoded value.
    /// </summary>
    Task<IReadOnlyList<VariableSummary>> ListAsync(VariableScope scope, Guid scopeId, Guid expectedTeamId, CancellationToken cancellationToken);

    /// <summary>
    /// Single-row read. Returns null when the (scope, scopeId, name) tuple has no active
    /// row. Same value-disclosure rules as <see cref="ListAsync"/>.
    /// </summary>
    Task<VariableSummary?> GetAsync(VariableScope scope, Guid scopeId, Guid expectedTeamId, string name, CancellationToken cancellationToken);

    /// <summary>
    /// Engine-only bulk read. Returns every active variable with its value resolved —
    /// secrets decrypted, plain values JSON-parsed. The result feeds directly into
    /// <c>NodeRunScope.Wf</c> / <c>NodeRunScope.Team</c> at run start.
    /// </summary>
    Task<IReadOnlyList<ResolvedVariable>> GetAllForEngineAsync(VariableScope scope, Guid scopeId, CancellationToken cancellationToken);

    /// <summary>
    /// Upsert. New (scope, scopeId, name) tuple → creates a row. Existing active tuple →
    /// replaces value + type + description (rotation in-place). The <paramref name="value"/>
    /// is JSON-encoded for non-secret types and AES-256-GCM encrypted for Secret type.
    /// </summary>
    Task SetAsync(
        VariableScope scope,
        Guid scopeId,
        Guid expectedTeamId,
        string name,
        VariableValueType valueType,
        JsonElement value,
        string? description,
        Guid actorUserId,
        CancellationToken cancellationToken);

    /// <summary>Soft-deletes by tuple. No-op when nothing active exists for that tuple.</summary>
    Task DeleteAsync(VariableScope scope, Guid scopeId, Guid expectedTeamId, string name, Guid actorUserId, CancellationToken cancellationToken);
}
