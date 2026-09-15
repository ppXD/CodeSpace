namespace CodeSpace.Messages.Agents;

/// <summary>
/// WHICH rates priced one agent run — the price-version the agent-run plane never had. A pure data noun (Rule 18.1):
/// where the rates came from (<see cref="ModelPriceSources"/>), the two per-million rates themselves, and a stable
/// <see cref="Digest"/> over both. The digest is MINTED in Core (<c>AgentCostPricing.SnapshotOf</c>) — this record
/// carries it, never computes it, so Messages stays free of behaviour.
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
