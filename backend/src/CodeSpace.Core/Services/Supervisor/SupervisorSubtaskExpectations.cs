using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// S2's SERVER FALLBACK for a planned subtask's <see cref="SupervisorPlannedSubtask.ExpectsChanges"/> when the model
/// didn't declare it: a subtask whose instruction OPENS with a read-only/investigative verb is presumed NOT to
/// expect a diff; every other subtask defaults to expecting one (the safe, backward-compatible default — byte-
/// identical to the pre-S2 world, where a missing branch always failed closed). A defensive heuristic ONLY —
/// an explicit model declaration always wins, never overridden by this. Pure + static so the inference is
/// unit-pinned directly.
/// </summary>
public static class SupervisorSubtaskExpectations
{
    /// <summary>Verbs whose subtasks are presumed read-only/analysis when the model leaves <c>ExpectsChanges</c> unset. Deliberately narrow — a false negative here just means a legitimate no-diff unit fails closed exactly as it did before this field existed; a false positive would wrongly excuse a coding unit from grading, which this list is written to avoid.</summary>
    private static readonly string[] ReadOnlyLeadingVerbs =
    {
        "investigate", "analyze", "analyse", "research", "review", "audit", "inspect", "report", "document", "summarize", "summarise",
    };

    /// <summary>Resolve whether <paramref name="subtask"/> is expected to produce a diff/branch — the model's own declaration when present, else the verb-based server inference.</summary>
    public static bool Resolve(SupervisorPlannedSubtask subtask) =>
        subtask.ExpectsChanges ?? !StartsWithReadOnlyVerb(subtask.Instruction);

    /// <summary>Whether <paramref name="instruction"/>'s first word matches a read-only verb, case-insensitive. Internal + static so the allowlist is unit-pinned directly.</summary>
    internal static bool StartsWithReadOnlyVerb(string? instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction)) return false;

        var trimmed = instruction.TrimStart();
        var firstWord = trimmed.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        return ReadOnlyLeadingVerbs.Any(verb => string.Equals(firstWord.TrimEnd('.', ',', ':', ';'), verb, StringComparison.OrdinalIgnoreCase));
    }
}
