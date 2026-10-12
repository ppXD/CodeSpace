using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Credentials;

/// <summary>
/// Retire every personal token still live in a team its person no longer has standing in — no membership there, and not
/// an instance Admin, who stands in every team as an Owner does — the person being the token's owner, or, for a token
/// stored without one, whoever added it; a token whose creator stamp names no account is never retired on a guess.
/// Leaving or being removed retires them in the same transaction; this retires the ones a departure left behind anyway:
/// every departure from before departures retired anything, any an older pod serves during a rolling deploy, and every
/// token an instance Admin lent a team they are not in, once they lose the role — the app never takes the role away, so
/// no departure retires those. Each token is retired exactly as a departure retires it — blanked, Revoked,
/// soft-deleted, its repositories marked "needs a new credential", its act-as identities soft-deleted — and the provider
/// is not asked. Idempotent and bounded: a retired token is no longer a candidate, so the backlog only shrinks. NOT
/// tenant-scoped: a system-wide repair that runs without an actor context (mirrors <c>RepairDisconnectedCredentialBindingsCommand</c>).
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): each token is retired and saved on its own, and a
/// token that fails stays a candidate while the pass carries on. One transaction around the pass would let one failing
/// token undo every other one's retirement.</para>
/// </summary>
public sealed record RetireDepartedMembersCredentialsCommand : ICommand<int>, INonTransactionalCommand
{
    /// <summary>Tokens retired per tick — bounds each pass.</summary>
    public int BatchSize { get; init; } = 50;
}
