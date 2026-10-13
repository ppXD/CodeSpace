using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Credentials;

/// <summary>
/// Blank the ciphertext of every secret that is retired but still decryptable — the rows migration 0249 blanks on upgrade:
/// deleted Secret variables, revoked or soft-deleted git tokens, a deleted connection's OAuth client secret and group
/// hooks, every revision of a revoked storage credential, and deployment-default envelopes no template names. Retiring
/// one blanks it in this build; 0249 blanks what earlier builds left, but runs once, so anything a build older than 0249
/// retires afterwards — an older pod still serving during a rolling deploy, or the whole fleet after a rollback — keeps
/// its ciphertext until this runs. Idempotent: a blanked row is no longer a candidate, so an idle pass changes nothing.
/// NOT tenant-scoped: a system-wide repair that runs without an actor context (mirrors
/// <c>RetireDepartedMembersCredentialsCommand</c>).
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): each kind of secret is blanked by one statement that
/// commits on its own, so a statement that fails leaves the others' work in place and its own rows candidates for the
/// next pass. One transaction around the pass would hold every candidate row's lock until the last statement.</para>
/// </summary>
public sealed record ShredRetiredSecretsCommand : ICommand<int>, INonTransactionalCommand;
