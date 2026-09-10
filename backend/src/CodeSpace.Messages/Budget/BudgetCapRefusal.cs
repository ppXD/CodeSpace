namespace CodeSpace.Messages.Budget;

/// <summary>
/// The one formatter for every budget refusal reason, so all three grains read alike and each NAMES the cap that
/// refused. The reason string reaches an operator through the run's failure and the Room's budget block, and
/// "refused by the cap" with no grain was the whole ambiguity <see cref="BudgetCapGrain"/> exists to remove.
/// </summary>
public static class BudgetCapRefusal
{
    /// <summary>The grain's word as it appears in a refusal reason. An unmapped grain throws rather than printing a bare enum name into operator-facing copy.</summary>
    public static string Word(BudgetCapGrain grain) => grain switch
    {
        BudgetCapGrain.Run => "run",
        BudgetCapGrain.Team => "team",
        BudgetCapGrain.Deployment => "deployment",
        _ => throw new ArgumentOutOfRangeException(nameof(grain), grain, "unknown budget cap grain — add its word here"),
    };

    /// <summary>The refusal reason: what the admission WOULD have committed, the cap it would have passed, which cap that is, and (for a windowed cap) over what window.</summary>
    public static string Reason(BudgetCapGrain grain, decimal wouldCommitUsd, decimal capUsd, string? window = null) =>
        $"admission would commit {wouldCommitUsd:F4} past the {capUsd:F4} {Word(grain)} cap{(string.IsNullOrWhiteSpace(window) ? string.Empty : $" ({window})")}";
}
