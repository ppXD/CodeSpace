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
    // Version 2: the reducer no longer reads a clean exit code as Solved for a run that staked no obligation at all.
    // A protocol revision by the rule stated above (new reducer semantics), so runs created under v1 stay stamped v1
    // and keep the semantics they were assessed under — this is not a refactor and must not be treated as one.
    public const int CurrentVersion = 2;

    /// <summary>The enforcement mode every NEWLY CREATED run is stamped with while the protocol is in its Shadow phase (Lock Clause 1: production terminal mutation stays with the legacy engine until P2b enables Enforced per qualified cohort).</summary>
    public const CompletionEnforcementMode CurrentMode = CompletionEnforcementMode.Shadow;

    /// <summary>A run's assessment basis from its stamped policy version: unstamped (pre-P2a) rows are LegacyUnknown — old tape is never re-derived into contract truth.</summary>
    public static CompletionBasis BasisFor(int? stampedPolicyVersion) =>
        stampedPolicyVersion is null ? CompletionBasis.LegacyUnknown : CompletionBasis.ContractDerived;

    /// <summary>Parse a stored mode column fail-CLOSED: null or unrecognized reads <see cref="CompletionEnforcementMode.Legacy"/> — the protocol never enforces (or even shadow-trusts) a run whose policy it cannot read.</summary>
    public static CompletionEnforcementMode ModeFor(string? storedMode) =>
        Enum.TryParse<CompletionEnforcementMode>(storedMode, ignoreCase: false, out var mode) ? mode : CompletionEnforcementMode.Legacy;

    /// <summary>
    /// P2b (Enforced cohort) + Q3 (cohort admission): the mode a NEW run is stamped with, from its definition's
    /// own <see cref="Messages.Dtos.Workflows.WorkflowDefinition.CompletionMode"/> opt-in — null inherits
    /// <see cref="CurrentMode"/>, 'shadow' maps without consulting the cohort, and 'enforced' is a COHORT
    /// PRIVILEGE: it stamps only when the run's operating mode holds <see cref="ProtocolReadiness.Enforceable"/>
    /// standing and REFUSES TO LAUNCH otherwise — admission is the registry's graduation decision (a reviewed
    /// edit arguing accumulated conformance evidence), never a per-launch bypass, and silently stamping a weaker
    /// mode than the author declared stays the one unacceptable direction. Unknown vocabulary THROWS as before:
    /// the validator rejects it at authoring time; this throw is the launch-time backstop for rows that predate
    /// (or evaded) it. Callers derive <paramref name="mode"/> with <c>RunModeClassifier</c> from the SAME
    /// (projection kind, definition json) pair the run row will carry, so the admission decision and the
    /// terminal authority's later mode reading can never disagree.
    /// </summary>
    public static CompletionEnforcementMode StampModeFor(string? definitionCompletionMode, string mode, ModeProfile? profile) => definitionCompletionMode switch
    {
        null => CurrentMode,
        Messages.Dtos.Workflows.WorkflowDefinition.CompletionModeShadow => CompletionEnforcementMode.Shadow,
        Messages.Dtos.Workflows.WorkflowDefinition.CompletionModeEnforced when profile?.Readiness == ProtocolReadiness.Enforceable => CompletionEnforcementMode.Enforced,
        Messages.Dtos.Workflows.WorkflowDefinition.CompletionModeEnforced => throw new InvalidOperationException($"Definition opts into Enforced but mode '{mode}' {(profile is null ? "has no registered conformance profile" : $"holds ProtocolReadiness.{profile.Readiness}")} — the Enforced cohort admits only Enforceable modes; graduation is a reviewed ModeProfileRegistry edit arguing accumulated conformance evidence, never a launch-time bypass."),
        _ => throw new InvalidOperationException($"Unknown definition completionMode '{definitionCompletionMode}' — expected '{Messages.Dtos.Workflows.WorkflowDefinition.CompletionModeShadow}' or '{Messages.Dtos.Workflows.WorkflowDefinition.CompletionModeEnforced}'; refusing to launch with an unreadable enforcement opt-in."),
    };
}
