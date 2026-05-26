using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

public class Credential : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }
    public Guid ProviderInstanceId { get; set; }
    public Guid? OwnerUserId { get; set; }

    public AuthType AuthType { get; set; }
    public string DisplayName { get; set; } = default!;
    public string EncryptedPayload { get; set; } = default!;
    public List<string>? Scopes { get; set; }
    public DateTimeOffset? ExpiresDate { get; set; }
    public DateTimeOffset? LastValidatedDate { get; set; }
    public CredentialStatus Status { get; set; } = CredentialStatus.Active;
    public string? LastError { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
    public DateTimeOffset? DeletedDate { get; set; }

    public Team Team { get; set; } = default!;
    public ProviderInstance ProviderInstance { get; set; } = default!;
    public User? Owner { get; set; }
}
