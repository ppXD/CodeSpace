using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// Keeps what CodeSpace records about a credential's health in step with what its provider says, without waiting for a run to
/// find out: asks each provider whether a bounded batch of credentials still works and records the answer.
/// </summary>
public interface ICredentialHealthService
{
    /// <summary>
    /// Probes up to <paramref name="batchSize"/> credentials that are due (never validated, or validated long enough ago;
    /// one marked Error only after an hour) and returns how many the provider answered about. A credential it accepts is
    /// validated — its expiry recorded if it is a static token, an Error lifted; one it refuses with a 401 is marked Error. A
    /// probe that got no answer changes nothing, counts for nothing, and is not asked again by the same pass.
    ///
    /// <para>A pass is bounded in time as well as in credentials (<see cref="CodeSpace.Messages.Dtos.Credentials.CredentialHealthDeadlines"/>):
    /// a provider that stops answering costs one credential its probe deadline, not the worker, and the pass ends at its budget —
    /// ahead of the next tick. A tick that finds the previous pass still running returns 0 at once and asks no provider.</para>
    /// </summary>
    Task<int> ProbeBatchAsync(int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// For a credential about to be added: asks its provider about the token — within the probe deadline — and, when the
    /// provider accepts it, records on <paramref name="credential"/> that it was just validated and, for a static token the
    /// provider describes, when it expires; so the credential reads right from the first list instead of after the sweep's
    /// first pass. Never fails and never refuses: a provider that cannot be reached, is silent, or refuses the token leaves the
    /// credential as it was, due for the sweep — which is what marks one the provider refuses. Does nothing for a credential that
    /// is not a static token, or whose provider has no probe. Tracked changes to <paramref name="credential"/> only.
    /// </summary>
    Task ValidateNewAsync(Credential credential, ProviderInstance instance, CancellationToken cancellationToken);
}
