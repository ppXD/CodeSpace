using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

public class Repository : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }

    public Guid ProviderInstanceId { get; set; }
    public Guid? CredentialId { get; set; }

    public string ExternalId { get; set; } = default!;
    public string NamespacePath { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string FullPath { get; set; } = default!;
    public string DefaultBranch { get; set; } = "main";
    public RepositoryVisibility Visibility { get; set; } = RepositoryVisibility.Private;
    public string? Description { get; set; }
    public string WebUrl { get; set; } = default!;
    public string? CloneUrlHttps { get; set; }
    public string? CloneUrlSsh { get; set; }
    public bool Archived { get; set; }

    public DateTimeOffset? LastSyncedDate { get; set; }
    public DateTimeOffset? LastEventDate { get; set; }
    public RepositoryStatus Status { get; set; } = RepositoryStatus.Active;
    public string? LastError { get; set; }

    /// <summary>The publish guard chain's repo-level override — see <see cref="RepositoryPublishMode"/>.</summary>
    public RepositoryPublishMode PublishMode { get; set; } = RepositoryPublishMode.Branch;

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
    public DateTimeOffset? DeletedDate { get; set; }

    public Team Team { get; set; } = default!;
    public ProviderInstance ProviderInstance { get; set; } = default!;
    public Credential? Credential { get; set; }
}
