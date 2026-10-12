namespace CodeSpace.Messages.Authorization;

/// <summary>
/// Marker — a write on one credential, named by its id. Its owner may perform it on their own Personal credential;
/// anyone else needs <c>TeamPermissions.CredentialsManage</c>.
///
/// <para>Extends <see cref="IRequireCredentialAccess"/>, so the credential is first proven to be the
/// <c>X-Team-Id</c> team's and the caller a member of it; this tier only decides whether membership is enough. A
/// Personal credential is one person's own provider token — any member can link one through the self-service identity
/// endpoints — so a member who could not disconnect it again would be held to a token they chose to stop trusting.
/// Every other credential, a teammate's or the team's own, keeps the Admin bar, because on membership alone any Viewer
/// could disconnect the Owner's and break every repository bound through it.</para>
/// </summary>
public interface IRequireCredentialManagement : IRequireCredentialAccess
{
}
