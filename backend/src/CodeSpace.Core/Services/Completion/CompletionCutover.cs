namespace CodeSpace.Core.Services.Completion;

/// <summary>
/// THE contract-regime boundary (F0b / v4.1). A run's <c>CreatedAt</c> against this constant decides its
/// <see cref="CodeSpace.Messages.Contracts.CompletionBasis"/>: before → <c>LegacyUnknown</c> (contract dimensions
/// are never re-derived from an old tape), at-or-after → <c>ContractDerived</c> (the reducer's five dimensions are
/// the run's truth, and a missing assessment reads <c>Unknown</c>, never Success). The value is the reducer
/// regime's deployment day — every fact source the reducer consumes (fold verdicts, publish manifests, the
/// stop-publish and delivery gates, forced-stop reasons) has been durably on tape since before it. A moved cutover
/// silently REWRITES which runs have contract truth, so the value is pinned by test — changing it is an explicit,
/// reviewed decision, never a refactor side-effect.
/// </summary>
public static class CompletionCutover
{
    public static readonly DateTimeOffset Value = new(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Whether a run created at <paramref name="runCreatedAt"/> falls under the contract regime.</summary>
    public static bool IsContractEra(DateTimeOffset runCreatedAt) => runCreatedAt >= Value;
}
