using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The supervisor lane's EFFECTIVE unit contract → <see cref="ContractHashing"/> (P1b). Hashes WHAT the unit owes
/// after dispatch-time overrides: the effective instruction (a dispatch goal-override or a retry's revised
/// instruction IS a different contract), the dependency set (sorted — deps are a set, not a sequence), the full
/// acceptance spec, the output expectation, and the effective repo scope. EXCLUDES identity and display: unit id /
/// plan row id / title / timestamps — the hash names contract CONTENT (two units owing the identical thing share
/// a hash; identity lives on <see cref="WorkUnitRef"/>'s other coordinates). A unit the plan doesn't know
/// (clamped elsewhere) or a null subtask has no contract to hash — callers stamp null, never a fabricated digest.
/// </summary>
public static class SupervisorUnitContract
{
    /// <summary>
    /// P3b-1: whether the unit's contract OWES a delivery (its change must ARRIVE) — the planned subtask's own
    /// declaration, defaulting to true when omitted (the same convention the fold's vacuous-pass reading uses:
    /// only an explicit <c>ExpectsChanges=false</c> declares a read-only unit with nothing to arrive).
    /// </summary>
    public static bool OwesDelivery(SupervisorPlannedSubtask planned) => planned.ExpectsChanges != false;

    public static string Hash(SupervisorPlannedSubtask planned, string? effectiveInstruction, Guid? repositoryOverride) =>
        ContractHashing.Hash(new
        {
            instruction = string.IsNullOrWhiteSpace(effectiveInstruction) ? planned.Instruction : effectiveInstruction,
            dependsOn = planned.DependsOn is { Count: > 0 } deps ? deps.OrderBy(d => d, StringComparer.Ordinal).ToArray() : null,
            acceptance = planned.Acceptance,
            expectsChanges = planned.ExpectsChanges,
            repositoryOverride,
        }, Agents.AgentJson.Options);
}
