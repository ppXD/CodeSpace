using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>
/// Which pooled row an authored model pin runs on inside an operator's allowed model pool — the ONE choice the launch
/// (a single agent's pinned model, clamped before its config is frozen) and the dispatch (every bounded agent run, via
/// <c>HarnessModelReconciler</c>) both make, so a launch disposition and the run it predicts cannot disagree. Pure: it
/// decides over the rows <see cref="IModelPoolSelector.ListPoolRowsAsync"/> described; no I/O.
///
/// <para>A model NAME is unique only per credential, so the choice walks from the most specific thing authored to the
/// least: the pinned ROW, when it is pooled and usable; the pooled row carrying the pinned model on the pinned
/// credential; a pooled row carrying the model name; the pinned credential's best pooled row (a persona may pin only a
/// credential); the pool's own default row. Ranking follows the agent-plane default (<see cref="AgentPlaneModelRanking"/>),
/// row id breaking ties. Whenever the chosen row differs from what was authored — another model, or the same model on
/// another credential — the choice carries a note naming what moved and why: never in the pool, or in the pool but no
/// longer usable.</para>
/// </summary>
public static class AllowedModelPool
{
    internal const string NothingRunsNote = "None of this run's allowed models is enabled under an active credential, so nothing inside the pool can run.";

    /// <summary>The rows to describe for one choice: the pool, plus a pinned row the pool does not list — so a note can name the model and credential it moved away from.</summary>
    public static IReadOnlyList<Guid> RowsToRead(IReadOnlyList<Guid> pool, ModelPin pin) =>
        pin.RowId is { } row && !pool.Contains(row) ? [.. pool, row] : pool;

    public static PooledModelChoice Choose(IReadOnlyList<Guid> pool, IReadOnlyList<PooledModelRow> rows, ModelPin pin)
    {
        var usable = rows.Where(r => r.Usable && pool.Contains(r.Id)).ToList();
        var authored = Authored(rows, pin);

        var row = usable.FirstOrDefault(r => r.Id == pin.RowId)
            ?? OnCredential(usable, authored.CredentialId, authored.Model)
            ?? Best(usable.Where(r => Named(r, authored.Model)))
            ?? OnCredential(usable, authored.CredentialId, model: null)
            ?? Best(usable);

        if (row is null) return new PooledModelChoice(null, NothingRunsNote);

        return new PooledModelChoice(row, Moved(authored, row) ? Describe(pool, rows, authored, row) : null);
    }

    /// <summary>What the pin authored, in model + credential terms: a pinned row the rows describe speaks for itself; otherwise the pin's own name and credential.</summary>
    private static AuthoredPin Authored(IReadOnlyList<PooledModelRow> rows, ModelPin pin)
    {
        var pinned = rows.FirstOrDefault(r => r.Id == pin.RowId);
        var credentialId = pinned?.ModelCredentialId ?? pin.CredentialId;

        return new AuthoredPin(pin.RowId, pinned?.ModelId ?? NullIfBlank(pin.Model), credentialId, rows.FirstOrDefault(r => r.ModelCredentialId == credentialId)?.CredentialName);
    }

    /// <summary>The best pooled row on <paramref name="credentialId"/> — carrying <paramref name="model"/> when one is named. Null when no credential was authored.</summary>
    private static PooledModelRow? OnCredential(IReadOnlyList<PooledModelRow> usable, Guid? credentialId, string? model) =>
        credentialId is { } credential ? Best(usable.Where(r => r.ModelCredentialId == credential && (model is null || Named(r, model)))) : null;

    private static PooledModelRow? Best(IEnumerable<PooledModelRow> rows) =>
        AgentPlaneModelRanking.Rank(rows, r => r.IsDefault, r => r.ProbedCapabilityTier, r => r.CapabilityTier).ThenBy(r => r.Id).FirstOrDefault();

    /// <summary>Model ids match case-insensitively — parity with every other pool lookup.</summary>
    private static bool Named(PooledModelRow row, string? model) => model is not null && string.Equals(row.ModelId, model, StringComparison.OrdinalIgnoreCase);

    private static bool Moved(AuthoredPin authored, PooledModelRow row) =>
        (authored.RowId is { } id && id != row.Id) || (authored.Model is { } model && !Named(row, model)) || (authored.CredentialId is { } credential && credential != row.ModelCredentialId);

    /// <summary>The note for a moved pin, worded by cause: the same model on another credential, a model or credential the pool does not hold, or one it holds that can no longer run.</summary>
    private static string Describe(IReadOnlyList<Guid> pool, IReadOnlyList<PooledModelRow> rows, AuthoredPin authored, PooledModelRow row)
    {
        var instead = $"running '{row.ModelId}' on '{row.CredentialName}' instead.";

        if (authored.Model is { } model && Named(row, model))
            return $"Model '{model}' has no usable row on {(authored.CredentialName is { } name ? $"credential '{name}'" : "the credential it was pinned to")} in this run's allowed model pool; {instead}";

        return $"{Subject(authored)} {Cause(pool, rows, authored)}; {instead}";
    }

    private static string Subject(AuthoredPin authored) => authored switch
    {
        { Model: { } model, CredentialName: { } name } => $"Model '{model}' on '{name}'",
        { Model: { } model } => $"Model '{model}'",
        { CredentialId: not null, CredentialName: { } credential } => $"Credential '{credential}'",
        { CredentialId: not null } => "The pinned credential",
        _ => "The pinned model",
    };

    private static string Cause(IReadOnlyList<Guid> pool, IReadOnlyList<PooledModelRow> rows, AuthoredPin authored)
    {
        if (authored.Model is null && authored.CredentialId is not null) return "carries no usable model in this run's allowed model pool";

        return rows.Any(r => pool.Contains(r.Id) && !r.Usable && Matches(r, authored))
            ? "is in this run's allowed model pool but disabled, or its credential is no longer active"
            : "is not in this run's allowed model pool";
    }

    /// <summary>Whether a row is the authored thing — the pinned row itself, or the authored model on the authored credential (any credential when none was pinned).</summary>
    private static bool Matches(PooledModelRow row, AuthoredPin authored) =>
        row.Id == authored.RowId || (Named(row, authored.Model) && (authored.CredentialId is null || row.ModelCredentialId == authored.CredentialId));

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record AuthoredPin(Guid? RowId, string? Model, Guid? CredentialId, string? CredentialName);
}
