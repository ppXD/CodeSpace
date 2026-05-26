namespace CodeSpace.Core.Persistence.Entities;

public class User : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public string Email { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? AvatarUrl { get; set; }
    public string? PasswordHash { get; set; }

    /// <summary>
    /// When true, the user can sign in but must rotate the password before any other API
    /// call succeeds. Set by migration 0007 on the bootstrap admin; cleared by a
    /// successful ChangePasswordCommand.
    /// </summary>
    public bool PasswordMustChange { get; set; }

    public DateTimeOffset? LastLoginDate { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
    public DateTimeOffset? DeletedDate { get; set; }
}
