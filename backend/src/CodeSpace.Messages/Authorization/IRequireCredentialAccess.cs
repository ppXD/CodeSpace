namespace CodeSpace.Messages.Authorization;

/// <summary>
/// Marker for commands/queries keyed by CredentialId. Same dereference pattern as
/// IRequireRepositoryAccess — behavior loads credential → its TeamId → membership.
/// </summary>
public interface IRequireCredentialAccess
{
    Guid CredentialId { get; }
}
