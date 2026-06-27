using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>
/// EF-backed <see cref="IModelCapabilityProbeService"/>. Runs the fixed <see cref="ModelCapabilityProbeBattery"/> against
/// each opaque pool model (the model's OWN provider + credential — the whole point is to exercise THAT model) and writes
/// a coarse, monotonic-upgrade tier. Scoped (DbContext + the client registry are per-request).
/// </summary>
public sealed class ModelCapabilityProbeService : IModelCapabilityProbeService, IScopedDependency
{
    /// <summary>Small — each row is a MULTI-call battery (not one batched call like tiering, nor one ping like availability), so a tick probes few rows and the back-off drains the rest.</summary>
    private const int MaxBatch = 10;

    /// <summary>Re-probe window. LONG (days): an opaque alias's backing model rarely changes (much rarer than the tier's own 24h or availability's 30min). Bounds the per-week live-call cost of the multi-call battery; the monotonic write means a re-probe can only ever raise the tier.</summary>
    private static readonly TimeSpan ProbeRetryWindow = TimeSpan.FromDays(7);

    /// <summary>Per-battery-call wall-clock. Generous enough for a real (small) completion — unlike the availability PING (which only needs reachability), this needs the model to actually answer — but capped so a hung gateway can't pin the worker.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private readonly ILLMClientRegistry _clients;
    private readonly IPayloadEncryptor _encryptor;
    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<ModelCapabilityProbeService> _logger;

    public ModelCapabilityProbeService(ILLMClientRegistry clients, IPayloadEncryptor encryptor, CodeSpaceDbContext db, ILogger<ModelCapabilityProbeService> logger)
    {
        _clients = clients;
        _encryptor = encryptor;
        _db = db;
        _logger = logger;
    }

    public async Task ProbeTeamAsync(Guid teamId, CancellationToken cancellationToken)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow - ProbeRetryWindow;

            // TRACKED (the verdict persists) + Include the credential (the battery runs on the row's OWN model + key).
            // Only OPAQUE rows (capability_tier == 'Unknown' — the brain's "I don't recognise this"); a never-tiered row
            // (capability_tier IS NULL) belongs to the tiering producer, not here (the two gates are disjoint).
            var rows = await _db.ModelCredentialModel
                .Include(m => m.Credential)
                .Where(m => m.Enabled && m.Credential.TeamId == teamId && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active
                    && m.CapabilityTier == ModelCapabilityTier.Unknown
                    && (m.LastProbedCapabilityAt == null || m.LastProbedCapabilityAt < cutoff))
                .OrderBy(m => m.ModelId).ThenBy(m => m.Id)
                .Take(MaxBatch)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            if (rows.Count == 0) return;

            var now = DateTimeOffset.UtcNow;

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProbeRowAsync(row, now, cancellationToken).ConfigureAwait(false);
            }

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The probe is an ADVISORY enrichment — any team-level miss leaves the rows' probed tier unchanged and the
            // auto pick byte-identical. Never crash the caller (the backfill tick).
            _logger.LogWarning(ex, "Capability probe for team {TeamId} failed; leaving its opaque models' probed tier unchanged", teamId);
        }
    }

    public async Task<int> ProbeAllPendingAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - ProbeRetryWindow;

        var teamIds = await _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.Enabled && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active
                && m.CapabilityTier == ModelCapabilityTier.Unknown
                && (m.LastProbedCapabilityAt == null || m.LastProbedCapabilityAt < cutoff))
            .Select(m => m.Credential.TeamId)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var teamId in teamIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProbeTeamAsync(teamId, cancellationToken).ConfigureAwait(false);
        }

        return teamIds.Count;
    }

    /// <summary>
    /// Run the battery on ONE row + record a coarse tier. Stamps <see cref="ModelCredentialModel.LastProbedCapabilityAt"/>
    /// FIRST (back-off even on a no-verdict). A capability FAIL (wrong answer) counts against the band; an INFRA fault
    /// (no response / timeout / API error) is INCONCLUSIVE — excluded from the tally, never a capability signal. If the
    /// model gave ZERO usable responses (wholesale unreachable) ⇒ no verdict (re-probe later). Else map the score and
    /// write it as a MONOTONIC UPGRADE only (a later flaky run never downgrades a good verdict).
    /// </summary>
    private async Task ProbeRowAsync(ModelCredentialModel row, DateTimeOffset now, CancellationToken jobToken)
    {
        row.LastProbedCapabilityAt = now;

        var client = _clients.All.FirstOrDefault(c => c.Provider.Equals(row.Credential.Provider, StringComparison.OrdinalIgnoreCase));

        if (client == null) return;   // no client serves this provider — a config error, not a verdict; leave unchanged

        var credential = ToCredential(row.Credential);

        var responded = 0;
        var easyPasses = 0;
        var hardPasses = 0;

        foreach (var task in ModelCapabilityProbeBattery.Tasks)
        {
            var passed = await RunTaskAsync(client, row.ModelId, credential, task, jobToken).ConfigureAwait(false);

            if (passed is null) continue;   // inconclusive (infra fault) — excluded from the band tally

            responded++;

            if (passed.Value)
            {
                if (task.Band == ProbeBand.Easy) easyPasses++;
                else hardPasses++;
            }
        }

        if (responded == 0) return;   // the model never gave a usable response — infra, not a capability verdict

        var probed = ModelCapabilityProbeBattery.MapToTier(easyPasses, hardPasses);

        if (probed is { } tier && (int)tier > (int)(row.ProbedCapabilityTier ?? ModelCapabilityTier.Unknown))
            row.ProbedCapabilityTier = tier;   // MONOTONIC upgrade only — never downgrade on a later flaky run
    }

    /// <summary>Run one battery task: <c>true</c>/<c>false</c> = a graded capability pass/fail (a real completion); <c>null</c> = INCONCLUSIVE (our timeout / a transport fault / any API error / an unexpected fault). A genuine job cancellation rethrows.</summary>
    private async Task<bool?> RunTaskAsync(ILLMClient client, string model, ResolvedModelCredential credential, ProbeTask task, CancellationToken jobToken)
    {
        var request = new LLMCompletionRequest
        {
            Model = model,
            SystemPrompt = "You are a precise assistant. Answer correctly and follow the requested output format exactly.",
            UserPrompt = task.Prompt,
            MaxOutputTokens = 256,
            Temperature = 0,
            Credential = credential,
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(jobToken);
        timeoutCts.CancelAfter(CallTimeout);

        try
        {
            var completion = await client.CompleteAsync(request, timeoutCts.Token).ConfigureAwait(false);
            return task.Passes(completion.Text ?? "");
        }
        catch (OperationCanceledException) when (jobToken.IsCancellationRequested)
        {
            throw;   // the JOB was cancelled (shutdown) — propagate
        }
        catch (OperationCanceledException)
        {
            return null;   // our per-call timeout — inconclusive (not a capability verdict)
        }
        catch (LlmApiException)
        {
            return null;   // any API error (transport OR a status like 400/429) — the model didn't DEMONSTRATE; inconclusive
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capability probe task for model {ModelId} hit an unexpected fault; treating as inconclusive", model);
            return null;
        }
    }

    private ResolvedModelCredential ToCredential(ModelCredential credential) => new()
    {
        Provider = credential.Provider,
        ApiKey = string.IsNullOrEmpty(credential.EncryptedApiKey) ? null : _encryptor.Decrypt(credential.EncryptedApiKey),
        BaseUrl = credential.BaseUrl,
    };
}
