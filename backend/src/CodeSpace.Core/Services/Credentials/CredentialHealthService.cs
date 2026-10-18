using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Credentials;

public sealed class CredentialHealthService : ICredentialHealthService, IScopedDependency
{
    private const string SweepName = "credential-health-probe";

    /// <summary>How long a validation holds a credential out of the sweep. A token that stops working is found within this, and the number of provider calls a deployment spends on healthy credentials is a few a day each.</summary>
    public static readonly TimeSpan ValidatedStaleAfter = TimeSpan.FromHours(6);

    /// <summary>How long a credential marked Error waits before the provider is asked again: long enough that a dead one costs a call an hour, short enough that one the provider accepts again (an account unblocked, an OAuth client put back) heals within it.</summary>
    public static readonly TimeSpan ErrorRecheckAfter = TimeSpan.FromHours(1);

    /// <summary>The advisory lock a pass holds ("CREDHELT" in ASCII), so a tick that finds the previous pass still running skips. In the key space <see cref="ICrossProcessLock"/> shares, where a credential's key is 64 random bits.</summary>
    public const long PassLockKey = 0x4352_4544_4845_4C54;

    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly ICredentialHealthWriter _writer;
    private readonly ISweepFailureBackoff _backoff;
    private readonly ICrossProcessTryLock _passLock;
    private readonly CredentialHealthDeadlines _deadlines;
    private readonly TimeProvider _clock;
    private readonly ILogger<CredentialHealthService> _logger;

    public CredentialHealthService(CodeSpaceDbContext db, IProviderRegistry registry, ICredentialHealthWriter writer, ISweepFailureBackoff backoff, ICrossProcessTryLock passLock, CredentialHealthDeadlines deadlines, TimeProvider clock, ILogger<CredentialHealthService> logger)
    {
        _db = db;
        _registry = registry;
        _writer = writer;
        _backoff = backoff;
        _passLock = passLock;
        _deadlines = deadlines;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> ProbeBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var pass = await _passLock.TryAcquireAsync(PassLockKey, cancellationToken).ConfigureAwait(false);

        if (pass is null) return SkipOverlappingPass();

        return await RunPassWithinBudgetAsync(batchSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A pass that is already running — here or on another worker — is not queued behind: a tick that arrives while it works would only wait for the provider the first one is waiting for.</summary>
    private int SkipOverlappingPass()
    {
        _logger.LogInformation("{Sweep}: the previous pass is still running; this tick is skipped", SweepName);

        return 0;
    }

    /// <summary>
    /// The pass, ended at <see cref="CredentialHealthDeadlines.Pass"/> wherever it is — a probe in flight is cut, and no
    /// further one starts. What the pass had recorded stays recorded and the rest is due again next tick, so running out of time
    /// costs nothing but the credentials not reached.
    /// </summary>
    private async Task<int> RunPassWithinBudgetAsync(int batchSize, CancellationToken cancellationToken)
    {
        var probed = 0;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_deadlines.Pass);

        async Task<bool> ProbeAndCountAsync(DueCredential candidate, CancellationToken token)
        {
            var answered = await ProbeOneAsync(candidate, token).ConfigureAwait(false);

            if (answered) probed++;

            return answered;
        }

        try
        {
            return await new SweepPass<DueCredential>(SweepName, _backoff, _logger).RunAsync(batchSize, LoadDueAsync, ProbeAndCountAsync, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("{Sweep}: the pass ran out of its {Budget}s budget after {Probed} credentials; the rest are probed by the next tick", SweepName, _deadlines.Pass.TotalSeconds, probed);

            return probed;
        }
    }

    /// <summary>
    /// The next page of due credentials in id order, behind a cursor (see <see cref="SweepPass{TCandidate}"/>). Due means in
    /// service with a token to present, of a type the providers can probe, and either Active and never validated or not
    /// since <see cref="ValidatedStaleAfter"/>, or Error and last confirmed over <see cref="ErrorRecheckAfter"/> ago — a
    /// verdict's date is when the row was last written, which a confirmation refreshes.
    /// </summary>
    private async Task<List<DueCredential>> LoadDueAsync(Guid? after, int take, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var validatedBefore = now - ValidatedStaleAfter;
        var confirmedBefore = now - ErrorRecheckAfter;

        var due = _db.Credential.AsNoTracking()
            .Where(c => c.DeletedDate == null && c.EncryptedPayload != string.Empty)
            .Where(c => c.AuthType == AuthType.Pat || c.AuthType == AuthType.ProjectAccessToken || c.AuthType == AuthType.GroupAccessToken || c.AuthType == AuthType.OAuth)
            .Where(c => (c.Status == CredentialStatus.Active && (c.LastValidatedDate == null || c.LastValidatedDate < validatedBefore)) || (c.Status == CredentialStatus.Error && c.LastModifiedDate < confirmedBefore));

        if (after is Guid cursor) due = due.Where(c => c.Id.CompareTo(cursor) > 0);

        var ids = await due.OrderBy(c => c.Id).Select(c => c.Id).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);

        return ids.Select(id => new DueCredential(id)).ToList();
    }

    /// <summary>Probe one credential and record what the provider said, each on connections of their own. False when it was no longer due: disconnected, retired or lifted meanwhile.</summary>
    private async Task<bool> ProbeOneAsync(DueCredential candidate, CancellationToken cancellationToken)
    {
        var credential = await _db.Credential.AsNoTracking().Include(c => c.ProviderInstance)
            .SingleOrDefaultAsync(c => c.Id == candidate.Id && c.DeletedDate == null && c.EncryptedPayload != string.Empty && (c.Status == CredentialStatus.Active || c.Status == CredentialStatus.Error), cancellationToken).ConfigureAwait(false);

        if (credential == null) return false;

        var result = await ProbeAsync(credential.ProviderInstance, credential, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return await RecordAsync(credential, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What the provider says about the credential now. A credential recorded as Error is probed as if it were not: the
    /// verdict in its row stops anything resolving its token, and the probe is how the verdict is lifted. The copy is read
    /// without tracking and never saved, so the pretence reaches no row.
    /// </summary>
    private async Task<CredentialProbeResult> ProbeAsync(ProviderInstance instance, Credential credential, CancellationToken cancellationToken)
    {
        var provider = instance.Provider;
        var probe = _registry.Require<ICredentialProbeCapability>(provider);
        var context = new ProviderContext(instance, credential);

        credential.Status = CredentialStatus.Active;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_deadlines.Probe);

        try
        {
            // The token reaches a provider that honours it, which then stops; one that does not — an SDK with no per-call
            // token, a call that blocks its thread — is walked away from at the deadline all the same, on a pool thread of its
            // own so that a blocked one does not hold this pass's.
            return await Task.Run(() => probe.ProbeCredentialAsync(context, deadline.Token), CancellationToken.None).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CredentialProbeResult { IsValid = false, Error = $"{provider} did not answer within {_deadlines.Probe.TotalSeconds:0.#}s" };
        }
    }

    /// <summary>
    /// Accepted: validated, and a static token's real expiry kept when the provider described the token. Refused (401): marked
    /// Error. Anything else is no answer — nothing is written, and the failure sets the credential aside after a few in a
    /// row. Written against the payload the probe ended with (a refresh it triggered may have replaced the one read), so a
    /// reconnect that replaced the row meanwhile is never overwritten by a verdict on the old one.
    /// </summary>
    private async Task<bool> RecordAsync(Credential credential, CredentialProbeResult result, CancellationToken cancellationToken)
    {
        var provider = credential.ProviderInstance.Provider;

        if (result.IsValid)
        {
            await _writer.MarkValidatedAsync(credential.Id, credential.EncryptedPayload, _clock.GetUtcNow(), ExpiryOf(credential, result), cancellationToken).ConfigureAwait(false);

            return true;
        }

        if (!result.IsRejected) throw new InvalidOperationException($"The {provider} probe of credential {credential.Id} got no answer ({result.Error}); nothing was recorded");

        var marked = await _writer.MarkRejectedAsync(credential.Id, credential.EncryptedPayload, $"token rejected by {provider}", cancellationToken).ConfigureAwait(false);

        _logger.LogWarning("Credential {CredentialId} ({CredentialName}) was refused by {Provider}: marked Error ({Marked}). It needs to be reconnected", credential.Id, credential.DisplayName, provider, marked);

        return true;
    }

    public async Task ValidateNewAsync(Credential credential, ProviderInstance instance, CancellationToken cancellationToken)
    {
        if (!CredentialHealthDerivation.IsStaticToken(credential.AuthType) || !_registry.TryGet<ICredentialProbeCapability>(instance.Provider, out _)) return;

        var result = await TryProbeAsync(instance, credential, cancellationToken).ConfigureAwait(false);

        if (result is not { IsValid: true }) return;

        credential.LastValidatedDate = _clock.GetUtcNow();

        if (ExpiryOf(credential, result) is { } expiry) credential.ExpiresDate = expiry.ExpiresAt;
    }

    /// <summary>The probe of a credential that is not saved yet, or null when it could not be asked: a failure here is the provider's, and adding the credential does not wait on it or fail with it. The caller's own cancellation still ends the add.</summary>
    private async Task<CredentialProbeResult?> TryProbeAsync(ProviderInstance instance, Credential credential, CancellationToken cancellationToken)
    {
        try
        {
            return await ProbeAsync(instance, credential, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "{Sweep}: the new credential {CredentialId} could not be validated as it was added; it is left for the sweep", SweepName, credential.Id);

            return null;
        }
    }

    /// <summary>A static token's expiry as its provider reports it — recorded even when it is "none" — but only when the provider described the token; an OAuth credential's expiry is its access token's and is not this sweep's to write.</summary>
    private static ExpiryRecord? ExpiryOf(Credential credential, CredentialProbeResult result) =>
        CredentialHealthDerivation.IsStaticToken(credential.AuthType) && result.Active.HasValue ? new ExpiryRecord(result.ExpiresAt) : null;

    private sealed record DueCredential(Guid Id) : ISweepCandidate;
}
