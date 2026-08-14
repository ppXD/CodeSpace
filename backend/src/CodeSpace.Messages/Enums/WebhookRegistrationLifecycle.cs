namespace CodeSpace.Messages.Enums;

/// <summary>
/// The single definition of "this hook is still in service", shared by everything that has to
/// agree about it: the ingestion path deciding whether to accept a delivery, the provisioner
/// deciding whether an owner is already covered and which hooks a scope switch must retire, and
/// the <c>uq_connection_webhook_owner</c> partial unique index in DbUp 0121.
///
/// <para>These three MUST use one rule. When they disagree, the gap is silent and structural:
/// a status that ingestion accepts but coverage ignores lets the provisioner register a second
/// hook beside a delivering one, so every push arrives twice and starts two runs; a status the
/// index tolerates but retirement skips leaves that duplicate behind after a scope switch.</para>
/// </summary>
public static class WebhookRegistrationLifecycle
{
    /// <summary>
    /// Every status except <see cref="RepositoryWebhookRegistrationStatus.Cancelled"/>.
    ///
    /// <para>Ingestion is what makes this the right line, because a hook that can still deliver
    /// is still in service whatever we believe about it. <c>Cancelled</c> is the one state an
    /// operator deliberately moved off — an unbind or a scope switch CAS'd it there — so a
    /// delivery still arriving on it comes from a hook we asked the provider to delete and
    /// could not, and running a workflow off it would use a mode the connection has left.</para>
    ///
    /// <para>Every non-terminal status is in for the obvious reason. The ones that look like
    /// failures are in because a hook we did not manage to create can still EXIST at the provider:
    /// the operator finished it by hand from the setup steps the Webhook tab prints. That is
    /// already how the repository-scoped panel reads a dead-lettered hook with a last delivery.</para>
    ///
    /// <para><see cref="RepositoryWebhookRegistrationStatus.DeadLettered"/> is the one worth
    /// naming, because it covers two different worlds: a registration that never created a remote
    /// hook, and a teardown that failed to delete one still sitting there and still firing.
    /// Nothing in the row distinguishes them, so the only safe reading is that the remote hook may
    /// exist — count it as covering, and retire it on a scope switch. Reading it the other way is
    /// what lets a second hook be registered beside a delivering one.</para>
    ///
    /// <para>An explicit list rather than a bare "not Cancelled" so a status added later has to be
    /// placed here deliberately instead of being silently swept in.</para>
    /// </summary>
    public static readonly IReadOnlyList<RepositoryWebhookRegistrationStatus> InService = new[]
    {
        RepositoryWebhookRegistrationStatus.Pending,
        RepositoryWebhookRegistrationStatus.Enqueued,
        RepositoryWebhookRegistrationStatus.Registering,
        RepositoryWebhookRegistrationStatus.Registered,
        RepositoryWebhookRegistrationStatus.Failed,
        RepositoryWebhookRegistrationStatus.DeadLettered
    };

    /// <summary>Whether a hook in this status is still in service. See <see cref="InService"/>.</summary>
    public static bool IsInService(RepositoryWebhookRegistrationStatus status) =>
        status != RepositoryWebhookRegistrationStatus.Cancelled;

    /// <summary>
    /// The statuses a retirement CASes to <c>Cancelled</c>: everything <see cref="InService"/> except
    /// <see cref="RepositoryWebhookRegistrationStatus.Registered"/>, whose rows every retirement path
    /// hard-deletes instead — the row described a hook that no longer exists, so keeping it would
    /// claim something untrue.
    ///
    /// <para>This is the EXIT half of <see cref="InService"/> and the two have to be widened
    /// together. A status that counts as in service but that no retirement can CAS out is a row
    /// nothing can ever retire: it holds its owner against the <c>uq_connection_webhook_owner</c>
    /// index forever, so the owner can never be covered again — a worse failure than the duplicate
    /// hook that widening <c>InService</c> prevents. <c>DeadLettered</c> is exactly that status, and
    /// it was outside every retirement CAS in the codebase before this list existed.</para>
    /// </summary>
    public static readonly IReadOnlyList<RepositoryWebhookRegistrationStatus> RetirableToCancelled = new[]
    {
        RepositoryWebhookRegistrationStatus.Pending,
        RepositoryWebhookRegistrationStatus.Enqueued,
        RepositoryWebhookRegistrationStatus.Registering,
        RepositoryWebhookRegistrationStatus.Failed,
        RepositoryWebhookRegistrationStatus.DeadLettered
    };
}
