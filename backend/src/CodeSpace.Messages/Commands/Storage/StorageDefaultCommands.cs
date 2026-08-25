using System.Text.Json;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Storage;

/// <summary>
/// Authors the deployment default for one routed data class.
///
/// <para>Carries NO team on purpose: a template describes the whole deployment. It is gated by the instance
/// capability <c>storage.defaults.manage</c> rather than by team membership, and it must never be dispatched from a
/// team-scoped controller — the SPA injects <c>X-Team-Id</c> from local storage into every request and no non-team
/// route clears it, so an admin surface hitting a team-scoped controller writes into whatever team was last
/// visited.</para>
///
/// <para><c>Secret</c> is write-only request material and never appears in a response DTO. Nothing consumes the
/// template yet; the materializer lane is the intended reader.</para>
/// </summary>
public sealed record CreateStorageDefaultCommand : ICommand<StorageDefaultDetail>, IRequireGlobalPermission
{
    public string RequiredGlobalPermission => Permissions.StorageDefaultsManage;
    public required string DataClassTypeKey { get; init; }
    public required string ProviderTypeKey { get; init; }
    public required JsonElement NonSecretConfig { get; init; }

    /// <summary>A ROOT, never a finished namespace — the materializer appends a per-team segment before it reaches a team's profile.</summary>
    public required string NamespaceRoot { get; init; }

    public required StorageDefaultAdoptionPolicyValue AdoptionPolicy { get; init; }
    public bool IsEnabled { get; init; }
    public JsonElement? Secret { get; init; }
    public string? SafeHint { get; init; }
}

/// <summary>
/// Replaces the template's configuration and advances its revision. Omitting <c>Secret</c> keeps the attached
/// envelope; supplying one appends a new envelope and repoints the template at it; <c>ClearCredential</c> detaches
/// the envelope for a provider that needs none. Superseded envelopes are never overwritten in place.
/// </summary>
public sealed record UpdateStorageDefaultCommand : ICommand<StorageDefaultDetail?>, IRequireGlobalPermission
{
    public string RequiredGlobalPermission => Permissions.StorageDefaultsManage;
    public Guid DefaultId { get; init; }
    public required uint ExpectedXmin { get; init; }
    public required int ExpectedRevision { get; init; }
    public required string ProviderTypeKey { get; init; }
    public required JsonElement NonSecretConfig { get; init; }
    public required string NamespaceRoot { get; init; }
    public required StorageDefaultAdoptionPolicyValue AdoptionPolicy { get; init; }
    public JsonElement? Secret { get; init; }
    public string? SafeHint { get; init; }
    public bool ClearCredential { get; init; }
}

/// <summary>Turns a template on or off. A disabled template is inert; templates are never deleted.</summary>
public sealed record SetStorageDefaultEnabledCommand : ICommand<StorageDefaultDetail?>, IRequireGlobalPermission
{
    public string RequiredGlobalPermission => Permissions.StorageDefaultsManage;
    public Guid DefaultId { get; init; }
    public required uint ExpectedXmin { get; init; }
    public required int ExpectedRevision { get; init; }
    public required bool IsEnabled { get; init; }
}
