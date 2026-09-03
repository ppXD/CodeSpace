using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Cost;

/// <summary>
/// The ONE query that turns a team's operator-typed model prices (<c>model_credential_model.input/output_usd_per_million</c>)
/// into the plain dictionary <see cref="AgentCostPricing"/> takes. It exists so the pricer itself stays PURE (Rule 18.1
/// — no DB inside a noun-shaped calculator): a caller that already holds a DB context loads the map ONCE per operation
/// and hands it down through the pure pricing/admission calls. Team-scoped fail-closed through the credential FK.
///
/// <para>A DISABLED row still prices: the price answers "what did this model cost", and a run that already spent on a
/// model the operator has since hidden must still bill honestly. A REVOKED/soft-deleted credential's rows are excluded
/// — revoking drops the key, so nothing can spend on them any more.</para>
///
/// <para>Two credentials of the same team may price the SAME model id differently (two keys, two contracts). This
/// takes the MAX of each side: under a cost cap the only safe direction is to over-, never under-, estimate.</para>
/// </summary>
public static class ModelPriceResolver
{
    /// <summary>The shared empty map for the common all-unpriced team — keeps that path allocation-free.</summary>
    public static readonly IReadOnlyDictionary<string, ModelPrice> Empty = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every model the team has PRICED (BOTH per-million columns set — a half-filled row is not a price), keyed by wire model id, case-insensitively. Unpriced rows are absent, so the pricer falls through to the env override and the built-in table for them.</summary>
    public static async Task<IReadOnlyDictionary<string, ModelPrice>> LoadAsync(CodeSpaceDbContext db, Guid teamId, CancellationToken cancellationToken)
    {
        if (teamId == Guid.Empty) return Empty;

        var rows = await db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.InputUsdPerMillion != null && m.OutputUsdPerMillion != null
                        && m.Credential.TeamId == teamId && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active)
            .Select(m => new { m.ModelId, Input = m.InputUsdPerMillion!.Value, Output = m.OutputUsdPerMillion!.Value })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0) return Empty;

        return rows
            .GroupBy(r => r.ModelId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new ModelPrice { InputPerMillionUsd = g.Max(r => r.Input), OutputPerMillionUsd = g.Max(r => r.Output) }, StringComparer.OrdinalIgnoreCase);
    }
}
