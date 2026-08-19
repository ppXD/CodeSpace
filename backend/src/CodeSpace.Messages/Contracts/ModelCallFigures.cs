namespace CodeSpace.Messages.Contracts;

/// <summary>
/// The figures a model-call attempt row may DECLARE it could not produce, named by their own column names.
///
/// <para><b>Why a row needs to say this at all.</b> Every one of these columns is nullable, and NULL alone cannot tell
/// "nobody could observe this" from "nobody has written it yet". For an in-process call the difference is academic —
/// the recording decorator sees the whole exchange — but for a call a harness CLI made inside itself the platform is
/// reading a frame after the fact, and several figures are simply not in it: a provider request id the CLI never
/// prints, a token class it does not report, a cost for a model this deployment has no price for. Writing zero for any
/// of those would read as measured, and a cost report that is quietly wrong is trusted. So the row names them.</para>
///
/// <para><b>What an empty set means, exactly.</b> That the row's producer DECLARES nothing unavailable — not that
/// every figure on it was measured. A producer that never populated a column and never declared it says nothing about
/// that column either way, which is what every row written before this vocabulary existed says.</para>
///
/// <para>The database enforces two things over the set: every member is from this vocabulary, and a figure named here
/// carries no value in its own column. Ordering and distinctness are this contract's, not the database's:
/// <see cref="Canonical"/> is what a producer here writes, so two rows describing the same absences compare equal.</para>
/// </summary>
public static class ModelCallFigures
{
    public const string ProviderRequestId = "provider_request_id";
    public const string CacheReadTokens = "cache_read_tokens";
    public const string CacheWriteTokens = "cache_write_tokens";
    public const string ReasoningTokens = "reasoning_tokens";
    public const string CostAmount = "cost_amount";
    public const string FirstTokenAt = "first_token_at";
    public const string CompletedAt = "completed_at";

    private static readonly IReadOnlySet<string> Registered = new HashSet<string>(StringComparer.Ordinal)
    {
        ProviderRequestId, CacheReadTokens, CacheWriteTokens, ReasoningTokens, CostAmount, FirstTokenAt, CompletedAt,
    };

    /// <summary>Every figure a row may declare unavailable. The same seven names <c>ck_workflow_run_model_call_attempt_unavailable_figures</c> admits — a member added here without the migration is refused at insert.</summary>
    public static IReadOnlySet<string> All => Registered;

    public static bool IsSupported(string? figure) => figure is not null && Registered.Contains(figure);

    /// <summary>The canonical spelling of a declared set: distinct and ordinal-sorted, so two producers naming the same absences write byte-identical arrays.</summary>
    public static IReadOnlyList<string> Canonical(IEnumerable<string> figures) =>
        figures.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
