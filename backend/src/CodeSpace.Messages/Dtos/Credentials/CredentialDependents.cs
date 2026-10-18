using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Credentials;

/// <summary>
/// Everything in a team that leans on one credential — what a Disconnect would break, or what a dead credential left
/// stranded. Read for a live credential before disconnecting it, and for a disconnected or retired one to see what is
/// waiting for a new home.
/// </summary>
public sealed record CredentialDependents
{
    public required Guid CredentialId { get; init; }
    public required string DisplayName { get; init; }
    public required CredentialStatus Status { get; init; }

    /// <summary>Soft-deleted: a departed member's token the team retired. It is gone from the credential list, but what was bound through it is not.</summary>
    public required bool Retired { get; init; }

    public Guid? OwnerUserId { get; init; }
    public required Guid ProviderInstanceId { get; init; }

    /// <summary>Live repositories bound through the credential, whatever their status, each with the projects it belongs to.</summary>
    public required IReadOnlyList<DependentRepository> Repositories { get; init; }

    /// <summary>Group or organization hooks that register, retry and delete through the credential, other than ones already retired.</summary>
    public required IReadOnlyList<DependentConnectionWebhook> ConnectionWebhooks { get; init; }

    /// <summary>Act-as identities — a person's linked provider account — backed by the credential. A Disconnect ends them; moving bindings does not.</summary>
    public required IReadOnlyList<DependentIdentity> Identities { get; init; }

    /// <summary>Workflow triggers that name any of the repositories above: what stops firing for them while it is stranded.</summary>
    public required IReadOnlyList<DependentActivation> Activations { get; init; }
}

public sealed record DependentRepository
{
    public required Guid Id { get; init; }
    public required string FullPath { get; init; }
    public required RepositoryStatus Status { get; init; }
    public string? LastError { get; init; }
    public required IReadOnlyList<DependentProject> Projects { get; init; }
}

public sealed record DependentProject
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
}

public sealed record DependentConnectionWebhook
{
    public required Guid Id { get; init; }
    public required string OwnerPath { get; init; }
    public required RepositoryWebhookRegistrationStatus RegistrationStatus { get; init; }
}

public sealed record DependentIdentity
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; init; }
    public string? UserName { get; init; }
    public required string ProviderUsername { get; init; }
}

public sealed record DependentActivation
{
    public required Guid ActivationId { get; init; }
    public required Guid WorkflowId { get; init; }
    public required string WorkflowName { get; init; }
    public required string TypeKey { get; init; }
    public required bool Enabled { get; init; }

    /// <summary>The dependent repositories this trigger names.</summary>
    public required IReadOnlyList<Guid> RepositoryIds { get; init; }
}
