using System.Text;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// Builds the DETERMINISTIC instruction for a resolver agent (resolver loop #379, S2) — a pure string builder, no
/// state, no model. Given the goal, the conflicted integration, and the FULL set of the prior agents' produced
/// branches, it produces the resolver <c>agent.code</c> run's goal text: reconcile those branches into one coherent
/// change, build, run the tests, and commit ONLY if they pass.
///
/// <para>This is the "deterministic synthesis" half of fork #2: the decider only CHOOSES to attempt resolution (the
/// <c>resolve</c> verb); the CONTENT of what the resolver does — which branches, which conflicted files, the
/// build/test-gate — is assembled here from durable data, never authored by the model. So the resolver can never be
/// pointed at the wrong branches by a model mistake, and the whole loop is model-free testable.</para>
///
/// <para>Branch-pair approach (the locked fork): the resolver clones the repo and RE-MERGES the agents' already-pushed
/// branches itself (rather than patching conflict markers), producing one reconciled branch a downstream PR-open
/// consumes. The build/test gate is INSTRUCTION-ENCODED (the resolver commits only on green + ends with
/// <see cref="TestsPassedMarker"/>); S3 reads that marker as the verification verdict.</para>
/// </summary>
public static class SupervisorResolverRecipe
{
    /// <summary>The exact token the resolver agent must end its summary with WHEN (and only when) the build + full test suite passed — the instruction-encoded verification verdict S3 reads. Load-bearing: pinned by a unit test so a rename is a visible decision.</summary>
    public const string TestsPassedMarker = "RESOLUTION_VERIFIED";

    /// <summary>
    /// The resolver agent's goal text. Names the goal, the conflicted files, and EVERY branch to reconcile (already
    /// pushed to origin), then the build/test-gated reconcile steps + the reconcile-don't-invent guardrail + the
    /// verified-only marker. Deterministic: same inputs → same string (the branches/files are emitted in the order
    /// given, so a replay re-derives identical bytes).
    /// </summary>
    public static string BuildInstruction(string goal, SupervisorIntegrationOutcome conflict, IReadOnlyList<string> branches)
    {
        var sb = new StringBuilder();

        sb.AppendLine("The parallel agents' work for this goal could not be automatically combined — there is an integration conflict to resolve.");
        sb.AppendLine($"Goal: {goal}");
        sb.AppendLine();

        sb.AppendLine("Reconcile these branches (already pushed to the 'origin' remote) into ONE coherent change:");
        foreach (var branch in branches) sb.AppendLine($"  - {branch}");
        sb.AppendLine();

        if (conflict.ConflictedFiles.Count > 0)
        {
            sb.AppendLine("The conflict was on these files — pay them special attention:");
            foreach (var file in conflict.ConflictedFiles) sb.AppendLine($"  - {file}");
            sb.AppendLine();
        }

        sb.AppendLine("Steps:");
        sb.AppendLine("  1. Fetch each branch from origin and merge them together in this working copy.");
        sb.AppendLine("  2. Resolve every conflict so the combined change is coherent and complete — reconcile the two sides, do NOT discard either agent's intent.");
        sb.AppendLine("  3. Build the project and run the FULL test suite.");
        sb.AppendLine("  4. Commit the reconciled result ONLY if the build succeeds AND all tests pass. If they do not pass, keep fixing until they do, or stop without committing.");
        sb.AppendLine();
        sb.AppendLine("Do not invent changes beyond reconciling the agents' work. Do not weaken or delete tests to make them pass.");
        sb.AppendLine($"When (and only when) the build and the full test suite pass on the reconciled result, end your final summary with the exact token: {TestsPassedMarker}");

        return sb.ToString();
    }
}
