using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

public class ProviderInstance : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }
    public ProviderKind Provider { get; set; }
    public string DisplayName { get; set; } = default!;
    public string BaseUrl { get; set; } = default!;
    public string? ApiUrl { get; set; }
    public string? WebUrl { get; set; }

    public string? OauthClientId { get; set; }
    public string? OauthClientSecretEnc { get; set; }
    public string? OauthRedirectPath { get; set; }
    public List<string>? OauthDefaultScopes { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
    public DateTimeOffset? DeletedDate { get; set; }

    public Team Team { get; set; } = default!;
}
