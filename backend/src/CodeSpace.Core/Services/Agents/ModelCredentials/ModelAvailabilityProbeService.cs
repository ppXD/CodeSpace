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
/// EF-backed <see cref="IModelAvailabilityProbeService"/>. Pings each ENABLED Custom-gateway pool model with a minimal
/// completion and records reachability. Scoped (DbContext + the client registry are per-request).
/// </summary>
public sealed class ModelAvailabilityProbeService : IModelAvailabilityProbeService, IScopedDependency
{
    /// <summary>Each ping is a SEPARATE live call (unlike the tiering service's one batched structured call), so the per-tick batch is smaller — a large dead-gateway pool drains across ticks via the <see cref="LastPingedAt"/> back-off.</summary>
    private const int MaxBatch = 25;

    private const string CustomProvider = "custom";

    /// <summary>Re-probe window. SHORTER than tiering's 24h: availability is volatile (a gateway dies and recovers within minutes), so a long window would avoid a recovered model for nearly a day with only the operator PIN as escape. 30 min bounds the live-call cost while letting a recovered gateway re-probe soon — and unlike the write-once tier, every row re-evaluates each window.</summary>
    private static readonly TimeSpan AvailabilityRetryWindow = TimeSpan.FromMinutes(30);

    /// <summary>Per-ping wall-clock. A TIGHT linked cap so a half-open gateway (accepts the socket then hangs) can't pin the worker for the LLM clients' 600s generation budget × retries — the probe only needs reachability, not a useful completion.</summary>
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(10);

    private readonly ILLMClientRegistry _clients;
    private readonly IPayloadEncryptor _encryptor;
    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<ModelAvailabilityProbeService> _logger;

    public ModelAvailabilityProbeService(ILLMClientRegistry clients, IPayloadEncryptor encryptor, CodeSpaceDbContext db, ILogger<ModelAvailabilityProbeService> logger)
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
            var cutoff = DateTimeOffset.UtcNow - AvailabilityRetryWindow;

            // TRACKED (the verdict persists) + Include the credential (the ping needs its key + base url + provider).
            var rows = await _db.ModelCredentialModel
                .Include(m => m.Credential)
                .Where(m => m.Enabled && m.Credential.TeamId == teamId && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active
                    && m.Credential.Provider.ToLower() == CustomProvider && m.Credential.BaseUrl != null
                    && (m.LastPingedAt == null || m.LastPingedAt < cutoff))
                .OrderBy(m => m.ModelId).ThenBy(m => m.Id)   // deterministic batch — reproducible which rows a tick probes
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
            // Availability is an ADVISORY hint — any team-level miss (a query / SaveChanges fault) leaves the rows'
            // availability unchanged and the auto pick byte-identical. Never crash the caller (the backfill tick).
            _logger.LogWarning(ex, "Availability probe for team {TeamId} failed; leaving its models' availability unchanged", teamId);
        }
    }

    public async Task<int> ProbeAllPendingAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - AvailabilityRetryWindow;

        var teamIds = await _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.Enabled && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active
                && m.Credential.Provider.ToLower() == CustomProvider && m.Credential.BaseUrl != null
                && (m.LastPingedAt == null || m.LastPingedAt < cutoff))
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
    /// Ping ONE row + record its reachability. Stamps <see cref="LastPingedAt"/> FIRST (so the back-off applies even on a
    /// fail-soft skip), then maps the outcome: a 2xx completion ⇒ available; a response on ANY status (auth / rate-limit /
    /// shape) ⇒ available (reachable — those are orthogonal axes); only a no-response transport failure (StatusCode==null)
    /// or our own ping timeout ⇒ unavailable. An unroutable provider or an unexpected fault leaves availability UNCHANGED.
    /// </summary>
    private async Task ProbeRowAsync(ModelCredentialModel row, DateTimeOffset now, CancellationToken jobToken)
    {
        row.LastPingedAt = now;

        var client = _clients.All.FirstOrDefault(c => c.Provider.Equals(row.Credential.Provider, StringComparison.OrdinalIgnoreCase));

        if (client == null) return;   // no client serves this provider — a config error, not a dead endpoint; leave Available unchanged

        var request = new LLMCompletionRequest
        {
            Model = row.ModelId,
            SystemPrompt = "",
            UserPrompt = "ping",
            MaxOutputTokens = 16,   // a safe floor — some gateways reject max_tokens=1; the goal is REACHABILITY, not a useful reply
            Temperature = 0,
            Credential = ToCredential(row.Credential),
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(jobToken);
        timeoutCts.CancelAfter(PingTimeout);

        try
        {
            await client.CompleteAsync(request, timeoutCts.Token).ConfigureAwait(false);
            row.Available = true;   // a 2xx completion — reachable
        }
        catch (OperationCanceledException) when (jobToken.IsCancellationRequested)
        {
            throw;   // the JOB was cancelled (shutdown) — propagate; do NOT record a verdict
        }
        catch (OperationCanceledException)
        {
            row.Available = false;   // our PingTimeout fired — the endpoint hung without responding → unreachable
        }
        catch (LlmApiException ex)
        {
            // The endpoint RESPONDED (any HTTP status — even 401 AuthFailed / 429 RateLimited / 400 BadRequest) ⇒
            // reachable. Only a transport failure with NO response (StatusCode == null: connection refused / reset /
            // DNS / HttpClient timeout) ⇒ unreachable. So availability never conflates auth / quota / request-shape,
            // which are separate axes the operator fixes elsewhere.
            row.Available = ex.StatusCode is not null;
        }
        catch (Exception ex)
        {
            // An UNEXPECTED (non-typed) fault is a bug, not a verdict about the endpoint — leave Available unchanged.
            // The attempt is still stamped (LastPingedAt above), so the back-off applies and we don't hammer it.
            _logger.LogWarning(ex, "Availability probe for model {ModelId} hit an unexpected fault; leaving availability unchanged", row.ModelId);
        }
    }

    private ResolvedModelCredential ToCredential(ModelCredential credential) => new()
    {
        Provider = credential.Provider,
        ApiKey = string.IsNullOrEmpty(credential.EncryptedApiKey) ? null : _encryptor.Decrypt(credential.EncryptedApiKey),
        BaseUrl = credential.BaseUrl,
    };
}
