using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// WHICH rates priced one agent run — the price-version the agent-run plane never had. A pure data noun (Rule 18.1):
/// where the rates came from (<see cref="ModelPriceSources"/>), the two per-million rates themselves, and a stable
/// <see cref="Digest"/> over both.
///
/// <para>It exists because an agent run's cost is a DERIVED number, not a provider receipt: the CLI reports tokens,
/// CodeSpace multiplies them by an operator-editable rate. Without this stamp, editing
/// <c>model_credential_model.input/output_usd_per_million</c> (which has no effective-from column) silently re-values
/// every historical run — <c>result_json.CostUsd</c> could not be audited, because nothing recorded what it was
/// computed with. The physical-LLM plane already snapshots its rates (<c>BudgetLedger.PhysicalLlm</c>'s
/// <c>PricingSnapshotJson</c>); this is the same guarantee for the CLI plane, and the value the pre-launch budget
/// reservation stamps as its <c>price_version</c> so an admission can be told from one made under different rates.</para>
///
/// <para>Persisted through the existing <c>result_json</c> envelope — no migration, and a pre-snapshot row stays
/// byte-identical because the field is null-omitted.</para>
/// </summary>
public sealed record ModelPriceSnapshot(string Source, decimal InputUsdPerMillion, decimal OutputUsdPerMillion, string Digest)
{
    /// <summary>The <c>price_version</c> an admission stamps when NO table priced the run's model. A reservation still has to record SOMETHING (the column is NOT NULL), and "admitted without a price" is a materially different audit fact from "admitted under these rates" — collapsing the two would make an unpriceable launch indistinguishable from a priced one.</summary>
    public const string UnpricedVersion = "unpriced";

    /// <summary>How many hex characters of the SHA-256 the <see cref="Digest"/> keeps. 16 (64 bits) is far past collision risk for a price table an operator types by hand, and short enough to read in a <c>budget_reservation.price_version</c> cell.</summary>
    private const int DigestHexLength = 16;

    /// <summary>The snapshot for rates resolved from <paramref name="source"/> — the ONE place a digest is minted, so a value written to the ledger and a value stamped on a result can never be computed two ways.</summary>
    public static ModelPriceSnapshot Of(string source, decimal inputUsdPerMillion, decimal outputUsdPerMillion) =>
        new(source, inputUsdPerMillion, outputUsdPerMillion, DigestOf(source, inputUsdPerMillion, outputUsdPerMillion));

    /// <summary>The snapshot for an already-resolved <see cref="ModelPrice"/>.</summary>
    public static ModelPriceSnapshot Of(string source, ModelPrice price) => Of(source, price.InputPerMillionUsd, price.OutputPerMillionUsd);

    /// <summary>
    /// A stable hash over the source AND both rates: change any one of the three and the digest changes, so a
    /// reservation stamped under the old rates is distinguishable from one stamped under the new. Canonicalized with
    /// the INVARIANT culture and a round-trip decimal format, so the same rates hash identically on every host —
    /// a culture-dependent "2,5" vs "2.5" would otherwise mint two digests for one price.
    /// </summary>
    private static string DigestOf(string source, decimal inputUsdPerMillion, decimal outputUsdPerMillion)
    {
        var canonical = string.Create(CultureInfo.InvariantCulture, $"{source}|{inputUsdPerMillion:0.############################}|{outputUsdPerMillion:0.############################}");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..DigestHexLength].ToLowerInvariant();
    }
}

/// <summary>
/// WHERE a <see cref="ModelPriceSnapshot"/>'s rates came from — the three tables <c>AgentCostPricing</c> resolves in
/// order. Durable state (they land in <c>result_json</c> and in <c>budget_reservation.price_version</c>), so the
/// literals are pinned by test: renaming one would split the audit trail into two vocabularies.
/// </summary>
public static class ModelPriceSources
{
    /// <summary>The operator's per-model row next to the credential (<c>model_credential_model</c>) — the table that WINS.</summary>
    public const string CredentialRow = "credential-model-row";

    /// <summary>The <c>CODESPACE_AGENT_MODEL_PRICES</c> env override (Rule 8's correction path for drifted / absent prices).</summary>
    public const string EnvOverride = "env-override";

    /// <summary>The seeded in-code default table — the fallback when neither the operator's row nor the env names the model.</summary>
    public const string BuiltIn = "built-in-table";
}
