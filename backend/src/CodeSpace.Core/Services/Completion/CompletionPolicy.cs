using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Completion;

/// <summary>
/// THE completion-protocol policy authority (P2a) — supersedes the date-based CompletionCutover: era is decided by
/// the run's OWN immutably-stamped <c>CompletionPolicyVersion</c> column, never inferred from a global clock (a
/// hardcoded date cannot express per-run policy, replay semantics, or cohort rollout). Version 1 = the first
/// contract-era policy. The value is test-pinned: bumping it is an explicit protocol revision (new admission
/// rules, new reducer semantics), never a refactor side-effect.
/// </summary>
public static class CompletionPolicy
{
    public const int CurrentVersion = 1;

    /// <summary>The enforcement mode every NEWLY CREATED run is stamped with while the protocol is in its Shadow phase (Lock Clause 1: production terminal mutation stays with the legacy engine until P2b enables Enforced per qualified cohort).</summary>
    public const CompletionEnforcementMode CurrentMode = CompletionEnforcementMode.Shadow;

    /// <summary>A run's assessment basis from its stamped policy version: unstamped (pre-P2a) rows are LegacyUnknown — old tape is never re-derived into contract truth.</summary>
    public static CompletionBasis BasisFor(int? stampedPolicyVersion) =>
        stampedPolicyVersion is null ? CompletionBasis.LegacyUnknown : CompletionBasis.ContractDerived;

    /// <summary>Parse a stored mode column fail-CLOSED: null or unrecognized reads <see cref="CompletionEnforcementMode.Legacy"/> — the protocol never enforces (or even shadow-trusts) a run whose policy it cannot read.</summary>
    public static CompletionEnforcementMode ModeFor(string? storedMode) =>
        Enum.TryParse<CompletionEnforcementMode>(storedMode, ignoreCase: false, out var mode) ? mode : CompletionEnforcementMode.Legacy;
}
