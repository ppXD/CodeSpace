using System.Text.Json;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The ONE place the shape of an acceptance argv is decided: which ones can be run at all
/// (<see cref="NormalizeOrNull"/>) and how the supervisor surfaces below show one (<see cref="AsLine"/>). Every
/// acceptance command — the operator's floor (<c>SupervisorGoalConfig.AcceptanceChecks</c>), a supervisor unit's or a
/// stop payload's check, a task's own oracle — is spawned with NO shell: the first element is the program and the rest
/// are its arguments. So every reader that decides whether such a command exists (the launch projections that bake it,
/// the rehydrate and the stop grade that run it, the in-loop hook and the local verifier that wrap it) must agree on
/// what an unrunnable one is, or one of them promises a command another refused to read.
/// </summary>
public static class AcceptanceArgv
{
    /// <summary>
    /// The argv as its own copy when it can be spawned, else null. Two things make one unrunnable, and only these: a
    /// blank FIRST element — there is no program, and the argv is never repaired by promoting a later element into that
    /// slot, which would run a command nobody wrote — and a null element or a NUL anywhere, which cannot be handed to
    /// exec. Every other element is kept verbatim, blank ones after the first included: <c>["sh", " ", "check.sh"]</c>
    /// is three arguments, and the element boundaries are the command.
    /// </summary>
    public static IReadOnlyList<string>? NormalizeOrNull(IReadOnlyList<string>? argv) => argv is { Count: > 0 } && !string.IsNullOrWhiteSpace(argv[0]) && argv.All(token => token is not null && !token.Contains('\0')) ? argv.ToArray() : null;

    /// <summary>
    /// A check's command as the three supervisor surfaces below show it — a model reading a prompt, a human reading an
    /// approval card: a JSON array of strings on ONE line, cut at <paramref name="maxChars"/> and marked with an
    /// ellipsis, never shell text. For a TestsPass check the array is an argv, and the grader spawns every acceptance argv
    /// with NO shell (<c>TestsPassGrader</c> hands the first element to the runner as the program
    /// and the rest as its arguments), so a shell-looking line misleads: <c>--filter Category=Unit|Category=Smoke</c>
    /// reads as a pipe, and a joined line cannot tell <c>["sh", " ", "check.sh"]</c> from two arguments, or
    /// <c>[" ", "dotnet", "test"]</c> from <c>dotnet test</c>. The array keeps every element boundary and every
    /// character — <c>WorkflowJson.InterpolatedText</c>, the repo's own way to write an array into prompt text, leaves
    /// the quote, pipe, ampersand, angle brackets and plus as themselves — and it escapes a newline, so the line is one
    /// line whatever the argv holds. For the other kinds (<c>ArtifactPresent</c>, <c>LlmJudge</c>,
    /// <c>ArtifactSchema</c>, <c>CitationsResolve</c>) the same list holds repo paths, not an argv, and the array keeps
    /// their boundaries just as well.
    ///
    /// <para>Exactly three call sites come through here: the supervisor prompt's operator-floor block, each per-subtask
    /// check line, and the amendment card's proposed check, so those three never write one command two ways. It is NOT
    /// the only way a command reaches a reader. Known exceptions: the grader's own <c>$ cmd</c> evidence line
    /// (<c>TestsPassGrader</c>), which is a fenced record of what ran and which a failed unit's evidence tail puts into
    /// the prompt beside the array line, and the two UI labels, <c>RoomNarrative</c>'s acceptance label and
    /// <c>SupervisorPhaseSource</c>'s phase description, which still join with spaces.</para>
    /// </summary>
    public static string AsLine(IReadOnlyList<string> argv, int maxChars)
    {
        var json = JsonSerializer.Serialize(argv, Workflows.WorkflowJson.InterpolatedText);

        return json.Length <= maxChars ? json : json[..maxChars] + "…";
    }
}
