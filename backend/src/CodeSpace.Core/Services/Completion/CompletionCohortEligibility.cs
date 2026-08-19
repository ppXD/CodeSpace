using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Completion;

public interface ICompletionCohortEligibility
{
    /// <summary>Whether an assessment row's recorded structural inputs clear the three gates the terminal authority applies BEFORE it composes. False for a pre-slice row (both columns null), a run whose mode or capability is unregistered, and a run whose mode does not hold Enforceable standing.</summary>
    bool IsCohortEligible(string? runMode, string? capabilityKey);
}

/// <summary>
/// The re-derivation that makes a recorded <c>would_be_terminal_decision</c> readable as more than an upper bound:
/// the shadow deliberately mirrors only the authority's two EVIDENCE-dependent refusals (integrity, stages), so a
/// recorded CleanSuccess is a claim about the evidence, not a prediction of a terminal. This applies the three
/// STRUCTURAL refusals the shadow leaves out, from the mode and capability the row itself carries — exactly the
/// registries the authority consults, in its order: capability registered, mode registered, mode Enforceable.
///
/// <para>Every gate resolves against the registries' CURRENT contents, mirroring the authority (which recomputes
/// all three on every arbitration, nothing baked in) — so this answers "would an Enforced cohort stamp this today",
/// which is the question an Enforced-default decision turns on. The row's own <c>readiness_at_compose</c> is the
/// historical companion to that: it says what standing the mode held when the row was written, so a re-graduation
/// or demotion since is visible as drift instead of silently rewriting the past.</para>
///
/// <para>A sibling of the two registries rather than a widening of either (Rule 7): neither owns the conjunction,
/// and a consumer needs the whole answer, not both tables.</para>
/// </summary>
public sealed class CompletionCohortEligibility : ICompletionCohortEligibility, ISingletonDependency
{
    private readonly ICompletionCapabilityRegistry _capabilities;
    private readonly IModeProfileRegistry _modes;

    public CompletionCohortEligibility(ICompletionCapabilityRegistry capabilities, IModeProfileRegistry modes)
    {
        _capabilities = capabilities;
        _modes = modes;
    }

    public bool IsCohortEligible(string? runMode, string? capabilityKey)
    {
        if (runMode is null || capabilityKey is null) return false;

        if (_capabilities.Resolve(capabilityKey) is null) return false;

        return _modes.Resolve(runMode) is { Readiness: ProtocolReadiness.Enforceable };
    }
}
