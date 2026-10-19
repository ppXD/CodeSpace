using CodeSpace.Core.Authorization;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Credentials.Remedy;

public sealed partial class ProviderRemedyService
{
    /// <summary>One remedy's working set: what was asked, what was read, what was decided. A new one per call, so a scoped service answering twice never lets the first answer leak into the second.</summary>
    private sealed class RemedyContext
    {
        public required Guid TeamId { get; init; }

        /// <summary>The failure's code for a run; the refusal's reason for a delivery.</summary>
        public required string Code { get; init; }

        public required PersistedFailure Failure { get; init; }
        public required DateTimeOffset At { get; init; }
        public required IReadOnlyList<Guid> ScopeRepositoryIds { get; init; }

        /// <summary>False for a refused delivery: there is no run to run again.</summary>
        public required bool OffersRerun { get; init; }

        public List<RepositoryRow> Repositories { get; set; } = new();
        public Dictionary<Guid, CredentialRow> Credentials { get; set; } = new();

        /// <summary>The credential the remedy is about: the failure's own, else the one the first repository runs through.</summary>
        public CredentialRow? Credential { get; set; }

        public string? OwnerName { get; set; }

        /// <summary>Who the credential belongs to as the rules about acting on it read them: null when it belongs to no person, and a person who stands as no one when they left (<see cref="CredentialPerson"/>).</summary>
        public CredentialPerson? Person { get; set; }

        public InstanceRow? Instance { get; set; }

        /// <summary>Credentials still presented through the instance — read only for a redirect, where it decides whether editing the address is accepted.</summary>
        public int ConnectedCredentials { get; set; }

        public TeamRole Role { get; set; }
        public Guid? CallerId { get; set; }

        public bool Resolved { get; set; }
        public RemedyFacts Facts { get; set; } = default!;
        public IReadOnlyList<RemedyAction> Actions { get; set; } = Array.Empty<RemedyAction>();
    }

    private sealed record RepositoryRow(Guid Id, string FullPath, RepositoryStatus Status, Guid? CredentialId, Guid ProviderInstanceId);

    /// <summary>A credential narrowed to what a remedy reads — deliberately NOT the entity, which carries the sealed token.</summary>
    private sealed record CredentialRow(Guid Id, string DisplayName, CredentialOwnership Ownership, Guid? OwnerUserId, AuthType AuthType, CredentialStatus Status, DateTimeOffset? ExpiresDate, DateTimeOffset? LastValidatedDate, DateTimeOffset? DeletedDate, Guid ProviderInstanceId);

    /// <summary>A provider connection narrowed the same way: its address, never its OAuth secret.</summary>
    private sealed record InstanceRow(Guid Id, string DisplayName, ProviderKind Provider, string BaseUrl, string? ApiUrl);
}
