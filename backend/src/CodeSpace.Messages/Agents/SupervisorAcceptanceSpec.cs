using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// A model-authored OBJECTIVE acceptance check — the L3→L4 "definition of done" (a data noun, Rule 18.1). The
/// supervisor model authors this to declare HOW a result is verified, so the verdict becomes a SERVER-RUN check
/// instead of a self-reported "it passed" prose marker: the server executes <see cref="Command"/> against the
/// produced workspace and a non-zero exit fails acceptance. Carried on a terminal <c>stop</c> (the run's
/// definition of done) and on each plan phase. On a terminal stop the server runs this check against the run's final
/// reviewable head(s) — the single integrated branch (single-repo) or EVERY per-repo head (multi-repo, all-or-nothing,
/// L4 C2) — and a failed verdict WITHHOLDS that head set. It is an ADDITIONAL gate on top of the operator's own
/// acceptance floor (<c>SupervisorGoalConfig.AcceptanceChecks</c>), which the stop path ALSO enforces against the same
/// head(s) (L4 C1) so the floor gates even a clean no-conflict run. Both must pass; a model criterion only ever
/// TIGHTENS acceptance, never manufactures it (it cannot ship past the operator floor).
///
/// <para>Optional everywhere it appears, and — because it serializes through <c>AgentJson.Options</c> (Web
/// defaults, no global null-ignore) into the idempotency-key bytes — the property that holds it carries
/// <c>[JsonIgnore(WhenWritingNull)]</c> so an ABSENT acceptance is omitted entirely and the prior self-report
/// path stays byte-identical.</para>
/// </summary>
public sealed record SupervisorAcceptanceSpec
{
    /// <summary>The runnable acceptance check — an argv the server executes against the produced workspace (a non-zero exit fails acceptance). Authoring a runnable command is what makes the verdict OBJECTIVE rather than a model self-report.</summary>
    public required IReadOnlyList<string> Command { get; init; }

    /// <summary>Optional human-readable description of what the check proves — surfaced in the phase projection for legibility; never executed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }
}
