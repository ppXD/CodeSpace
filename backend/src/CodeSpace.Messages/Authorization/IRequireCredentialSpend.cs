namespace CodeSpace.Messages.Authorization;

/// <summary>
/// Marker — a request that presents one credential, named by its id, to its provider on the caller's behalf. Its owner
/// may spend their own Personal credential; anyone else needs <c>TeamPermissions.ReposManage</c>.
///
/// <para>Extends <see cref="IRequireCredentialAccess"/>, so the credential is first proven to be the <c>X-Team-Id</c>
/// team's and the caller a member of it; this tier only decides whether membership is enough. It is not: a provider
/// answers with everything the token's account can see, so on membership alone any Viewer could spend the Owner's
/// personal token and read back private repositories outside the team. The listings such a request serves feed a bind,
/// and a bind takes repos.manage — the same bar here, for a teammate's credential and for the team's own.</para>
/// </summary>
public interface IRequireCredentialSpend : IRequireCredentialAccess
{
}
