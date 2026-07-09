using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// EF-backed <see cref="IModelCredentialResolver"/>. Loads the team-scoped <c>ModelCredential</c> and decrypts
/// it with the shared <see cref="IPayloadEncryptor"/> — the SAME primitive every credential uses. Decryption
/// happens here, just-in-time, so the key lives only in the returned transient value (and, downstream, the
/// in-memory sandbox env), never in any persisted row.
/// </summary>
public sealed class ModelCredentialResolver : IModelCredentialResolver, IScopedDependency
{
    /// <summary>
    /// Operator-global single-tenant OpenAI key — the parallel to <c>CODESPACE_ANTHROPIC_API_KEY</c> for the
    /// agent path. NOT tenant-isolated: a deliberate single-tenant / local-dev convenience, superseded by any
    /// team credential. Pinned by a test (Rule 8).
    /// </summary>
    public const string OpenAIOperatorKeyEnvVar = "CODESPACE_OPENAI_API_KEY";

    // Provider tag → operator-global worker env var (the single-tenant last-resort key). Anthropic shares the
    // SAME var the in-process llm.complete client reads, so one operator key serves both paths.
    private static readonly IReadOnlyDictionary<string, string> OperatorGlobalKeyEnvVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Anthropic"] = AnthropicClient.ApiKeyEnvVar,
        ["OpenAI"] = OpenAIOperatorKeyEnvVar,
    };

    private readonly CodeSpaceDbContext _db;
    private readonly IPayloadEncryptor _encryptor;

    public ModelCredentialResolver(CodeSpaceDbContext db, IPayloadEncryptor encryptor)
    {
        _db = db;
        _encryptor = encryptor;
    }

    public async Task<ResolvedModelCredential?> ResolveAsync(AgentTask task, Guid teamId, IModelCredentialProjector? projector, CancellationToken cancellationToken)
    {
        if (task.ModelCredentialId is { } id)
            return await ResolvePinnedAsync(id, teamId, projector, cancellationToken).ConfigureAwait(false);

        // No pin: a team default for one of the harness's providers, else the operator-global single-tenant key.
        if (projector is null) return null;

        return await ResolveTeamDefaultAsync(teamId, projector.SupportedProviders, cancellationToken).ConfigureAwait(false)
               ?? ResolveOperatorGlobal(projector.SupportedProviders);
    }

    /// <summary>
    /// A pinned credential MUST be an Active, non-deleted row in THIS team AND for a provider the harness can
    /// drive — else fail clean with a typed error (never fall through to a different credential: the author
    /// pinned A, so the run uses A or fails). The team is the run row's, never a value from the deserialized
    /// (forgeable) task envelope. Provider is checked BEFORE decrypt, and every message carries only the id.
    /// </summary>
    private async Task<ResolvedModelCredential> ResolvePinnedAsync(Guid id, Guid teamId, IModelCredentialProjector? projector, CancellationToken cancellationToken)
    {
        var credential = await _db.ModelCredential.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == id && c.TeamId == teamId && c.DeletedDate == null && c.Status == CredentialStatus.Active, cancellationToken).ConfigureAwait(false)
            ?? throw new ModelCredentialResolutionException($"Model credential {id} is not an active credential for this team.");

        if (projector is not null && !projector.SupportedProviders.Contains(credential.Provider, StringComparer.OrdinalIgnoreCase))
            throw new ModelCredentialResolutionException($"Model credential {id} is for a provider this harness cannot drive.");

        return Decrypt(credential) with { DefaultModel = await PickDefaultModelAsync(credential.Id, cancellationToken).ConfigureAwait(false) };
    }

    /// <summary>
    /// No-pin "auto" resolve. Picks a (model, credential) pair from the FULL team pool — the operator-marked default model
    /// if one is set, else the strongest available model NOT counting Frontier (P3.4 — <see cref="AgentPlaneModelRanking"/>;
    /// ordinary execution is verified downstream by the task's own acceptance checks, so it doesn't reach for the priciest
    /// tier automatically) across ALL the team's Active credentials whose provider the harness can drive — so an "auto" run
    /// sees EVERY applicable model (null pool ⇒ all apply, the same contract <c>IModelPoolSelector</c> honours), never just
    /// one credential's rows. The model id and the decrypted key come from the SAME row, so they are always consistent.
    /// When the team has eligible credentials but NO registered models (an official vendor that hosts everything), fall
    /// back to the most-recent one with no default model — the CLI's own default stands, correct there. Null when the
    /// team has no usable credential at all (→ the operator-global key). Provider matching is case-insensitive in memory
    /// (a team has few credentials). It is NOT routed through <c>IModelPoolSelector.SelectAsync</c> because that is
    /// single-provider, whereas an agent harness drives SEVERAL providers and the pool here spans them — the null=all
    /// semantic, not the method, is the shared contract.
    /// </summary>
    private async Task<ResolvedModelCredential?> ResolveTeamDefaultAsync(Guid teamId, IReadOnlyList<string> supportedProviders, CancellationToken cancellationToken)
    {
        var candidates = await _db.ModelCredential.AsNoTracking()
            .Where(c => c.TeamId == teamId && c.DeletedDate == null && c.Status == CredentialStatus.Active)
            .OrderByDescending(c => c.CreatedDate)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var eligible = candidates.Where(c => supportedProviders.Contains(c.Provider, StringComparer.OrdinalIgnoreCase)).ToList();

        if (eligible.Count == 0) return null;

        var eligibleIds = eligible.Select(c => c.Id).ToHashSet();

        // Ordered IN-MEMORY (capability_tier is stored as TEXT — a DB ORDER BY would sort it alphabetically, not by
        // rank), mirroring ModelPoolSelector's own established pattern. IsDefault first, then AgentPlaneModelRanking
        // (tier-aware, Frontier soft-avoided), then row id — a stable total order, never credential-recency-weighted.
        var rows = await _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.Enabled && eligibleIds.Contains(m.ModelCredentialId))
            .Select(m => new { m.ModelId, m.ModelCredentialId, m.IsDefault, m.CapabilityTier, m.ProbedCapabilityTier, m.Id })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var pick = AgentPlaneModelRanking.Rank(rows, m => m.IsDefault, m => m.ProbedCapabilityTier, m => m.CapabilityTier)
            .ThenBy(m => m.Id)
            .Select(m => new { m.ModelId, m.ModelCredentialId })
            .FirstOrDefault();

        // A model in the pool → pair it with ITS OWN credential (key + model from one row). No models → the most-recent
        // eligible credential, no default model (an official vendor hosts the CLI default).
        return pick is not null
            ? Decrypt(eligible.First(c => c.Id == pick.ModelCredentialId)) with { DefaultModel = pick.ModelId }
            : Decrypt(eligible[0]);
    }

    /// <summary>
    /// A PINNED credential's default model: its operator-marked default, else the strongest available model not
    /// counting Frontier (P3.4 — <see cref="AgentPlaneModelRanking"/>, the SAME "avoid Frontier by default" policy as
    /// the full-pool pick). Credential-scoped on purpose — the operator pinned THIS credential, so an "auto" model
    /// must come from ITS rows, never the wider pool. Null when the pinned credential has no registered models (→ the
    /// CLI default). The no-pin path uses the full-pool pick above instead.
    /// </summary>
    private async Task<string?> PickDefaultModelAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var rows = await _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.ModelCredentialId == credentialId && m.Enabled)
            .Select(m => new { m.ModelId, m.IsDefault, m.CapabilityTier, m.ProbedCapabilityTier, m.Id })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return AgentPlaneModelRanking.Rank(rows, m => m.IsDefault, m => m.ProbedCapabilityTier, m => m.CapabilityTier)
            .ThenBy(m => m.Id)
            .Select(m => m.ModelId)
            .FirstOrDefault();
    }

    /// <summary>
    /// Last resort: the operator-global single-tenant key from the worker env, for the first supported provider
    /// that has one set. NOT tenant-isolated — a strict operator simply sets no <c>CODESPACE_*_API_KEY</c>, and
    /// then teams must each configure a credential. Reads the env directly (no secret persisted anywhere).
    /// </summary>
    private static ResolvedModelCredential? ResolveOperatorGlobal(IReadOnlyList<string> supportedProviders)
    {
        foreach (var provider in supportedProviders)
            if (OperatorGlobalKeyEnvVars.TryGetValue(provider, out var envVar) && Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } key)
                return new ResolvedModelCredential { Provider = provider, ApiKey = key };

        return null;
    }

    private ResolvedModelCredential Decrypt(ModelCredential credential) => new()
    {
        Provider = credential.Provider,
        ApiKey = string.IsNullOrEmpty(credential.EncryptedApiKey) ? null : _encryptor.Decrypt(credential.EncryptedApiKey),
        BaseUrl = credential.BaseUrl,
    };
}
