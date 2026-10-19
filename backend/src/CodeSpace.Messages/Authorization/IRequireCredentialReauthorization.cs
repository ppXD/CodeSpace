namespace CodeSpace.Messages.Authorization;

/// <summary>
/// Marker — a connect that may name an existing credential to re-authorize in place. It carries two bars, chosen by whether it names one:
/// a connect that names none mints a credential, and that takes <c>TeamPermissions.CredentialsManage</c> of anyone; one that names a
/// credential puts a new token into THAT row, and that is <see cref="IRequireCredentialManagement"/>'s bar — its own person on
/// membership alone, anyone else <c>credentials.manage</c>.
///
/// <para>Extends <see cref="IRequireTeamMembership"/>, so the <c>X-Team-Id</c> team is vetted and the caller a member of it first; this
/// tier only decides whether that is enough. It is not the credential marker (<see cref="IRequireCredentialAccess"/>): its id is
/// optional, and the credential-keyed behaviors prove the team of one that is always there. The rule proves it itself — an owner's pass
/// is for a credential of THIS team.</para>
///
/// <para>Why the owner's pass: a Personal credential is one person's own provider token, and putting a dead sign-in right adds nothing
/// the person could not do by pasting a token over it (<c>PUT /api/credentials/{id}/token</c>, the same bar) — the provider account that
/// signs in must still be the one the credential was connected as. Without it a Member whose sign-in expired could only ask an Admin, who
/// could only complete it by signing in as that Member.</para>
///
/// <para>What this tier does not decide is the reach over the credential's person, which is not a permission: a reconnect signs in AS them, so
/// the service also refuses an Admin who would reconnect another Admin's or an Owner's sign-in (the rank every act on a member keeps), and a
/// person who has left. Both are the same answer a client is given beforehand — <c>CredentialSummary.CallerMayRepair</c>, the remedy's buttons —
/// so a button is shown only where the request would be accepted.</para>
/// </summary>
public interface IRequireCredentialReauthorization : IRequireTeamMembership
{
    /// <summary>The credential this connect re-authorizes in place, or null when it creates one.</summary>
    Guid? ReplaceCredentialId { get; }
}
