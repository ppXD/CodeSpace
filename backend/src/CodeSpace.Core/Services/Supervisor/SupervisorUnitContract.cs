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

    /// <summary>
    /// P2b-2: the staked obligation set for one authorization wave — every contracted unit stakes its acceptance,
    /// delivery, and output rows, spec-hash-bound to the same effective contract. A change-expecting unit stakes
    /// delivery/output REQUIRED; a declared read-only unit stakes them ServerPolicy-AUTHORIZED-NotApplicable
    /// (Lock Clause 4: the stage is explicitly authorized off, never silently absent — the model DECLARED the
    /// read-only fact, the SERVER's policy authorizes the exemption, so the rows carry ServerPolicy authority).
    /// Pure so the whole table pins without a database.
    /// </summary>
    public static List<Messages.Contracts.RequirementEnvelope> BuildStakedRequirements(IEnumerable<(string SubtaskId, string ContractHash, bool OwesDelivery)> units)
    {
        var requirements = new List<Messages.Contracts.RequirementEnvelope>();

        foreach (var (subtaskId, contractHash, owesDelivery) in units)
        {
            requirements.Add(Stake($"acceptance:{subtaskId}", Messages.Contracts.ContractKinds.Acceptance, contractHash, required: true));
            requirements.Add(Stake($"delivery:{subtaskId}", Messages.Contracts.ContractKinds.Delivery, contractHash, owesDelivery));
            requirements.Add(Stake($"output:{subtaskId}", Messages.Contracts.ContractKinds.Output, contractHash, owesDelivery));
        }

        return requirements;
    }

    private static Messages.Contracts.RequirementEnvelope Stake(string requirementRef, string kind, string contractHash, bool required) => new()
    {
        RequirementRef = requirementRef,
        Kind = kind,
        Requiredness = required ? Messages.Contracts.Requiredness.Required : Messages.Contracts.Requiredness.ServerPolicyAuthorizedNotApplicable,
        Authority = required ? Messages.Contracts.ContractAuthority.ModelProposal : Messages.Contracts.ContractAuthority.ServerPolicy,
        SpecHash = contractHash,
        ContractSchemaVersion = "1",
    };

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
