namespace CodeSpace.Core.Persistence.Entities;

public class RepositoryWebhook : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid RepositoryId { get; set; }
    public string ExternalId { get; set; } = default!;
    public string CallbackUrl { get; set; } = default!;
    public string SecretEnc { get; set; } = default!;
    public List<string> SubscribedEvents { get; set; } = new();
    public bool Active { get; set; } = true;
    public DateTimeOffset? LastReceivedDate { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    public Repository Repository { get; set; } = default!;
}
