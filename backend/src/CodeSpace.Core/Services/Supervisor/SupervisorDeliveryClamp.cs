using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// DC-1 — clamp the model's PROPOSED delivery contract (authored on a fresh plan) against the OPERATOR's own
/// pre-declared one, PER FIELD: the operator's own declared value always wins when set, mirroring
/// <see cref="SupervisorRepoClamp"/>'s "model proposes, the operator's own bound value stands when the model is
/// silent" shape — a null check, never a throw, since an operator's delivery preference is a PREFERENCE, not an
/// access grant to violate. An operator-declared <c>false</c> must survive even against a model proposing
/// <c>true</c> — DC-1's whole point: a user's "don't open a PR" is never overridable by the model. Pure +
/// stateless, so a replay re-derives the identical clamp.
/// </summary>
public static class SupervisorDeliveryClamp
{
    /// <summary>The clamped contract, or null when NEITHER side named anything on ANY field — byte-identical to no contract at all, never an all-null placeholder object.</summary>
    public static DeliverySpec? Clamp(DeliverySpec? modelProposed, DeliverySpec? operatorDeclared)
    {
        var openPullRequest = operatorDeclared?.OpenPullRequest ?? modelProposed?.OpenPullRequest;
        var targetBranch = operatorDeclared?.TargetBranch ?? modelProposed?.TargetBranch;

        return openPullRequest is null && targetBranch is null
            ? null
            : new DeliverySpec { OpenPullRequest = openPullRequest, TargetBranch = targetBranch };
    }
}
