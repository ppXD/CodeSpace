using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Credentials;

/// <summary>
/// Ask each provider whether a bounded batch of credentials still works, and record what it answered: a credential it
/// accepts gets its validation date (and a static token its real expiry); one it refuses with a definite 401 is marked
/// <c>Error</c> with the reason, so the credential list says so and a run fails fast with <c>credential_needs_reconnect</c>
/// instead of at its first provider call. An answer that is not an answer — a dropped connection, a timeout, a 5xx — says
/// nothing about the credential and changes nothing. Credentials last validated recently are not candidates, and a
/// credential already marked <c>Error</c> is asked again only after an hour, so it heals by itself if the provider accepts it
/// again. NOT tenant-scoped: a system-wide sweep that runs without an actor context (mirrors
/// <c>RetireDepartedMembersCredentialsCommand</c>).
///
/// <para>A pass ends at its time budget and a provider that stops answering costs one credential its probe deadline
/// (<c>CredentialHealthDeadlines</c>); a tick that finds the previous pass still running skips. Both leave the credentials not
/// reached due for the next tick.</para>
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): each credential is probed and recorded on its own
/// connection, and one that fails stays a candidate while the pass carries on. One transaction around the pass would hold
/// every row it touched across every provider call it makes.</para>
/// </summary>
public sealed record ProbeCredentialsCommand : ICommand<int>, INonTransactionalCommand
{
    /// <summary>Credentials probed per tick — bounds each pass, which also attempts a bounded multiple of it: credentials that fail to answer do not count, are not retried by the same pass, and after several passes in a row are set aside for a while.</summary>
    public int BatchSize { get; init; } = 25;
}
