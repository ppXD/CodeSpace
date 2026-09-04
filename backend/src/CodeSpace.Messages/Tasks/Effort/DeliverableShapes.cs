namespace CodeSpace.Messages.Tasks.Effort;

/// <summary>
/// The OPEN-STRING vocabulary for WHAT a task is asked to produce — the deliverable SHAPE axis, orthogonal to the
/// effort tier (Rule 18.1 — a pure vocabulary noun in Messages, no enum, no task taxonomy). Effort answers "how much
/// work is this?"; shape answers "what does done look like?" — a chat answer, a written document, a code change, or
/// read-only findings. Two tasks can share a tier and still need a different agent MODE and a different objective
/// ORACLE, which is exactly what a tier alone could never express.
///
/// <para>Deliberately OPEN strings (like <see cref="TaskEffortModes"/> / <c>TaskRecipeKinds</c>): a classifier may
/// emit a value nobody has heard of, and <see cref="Normalize"/> folds anything outside the known set back to
/// <see cref="Code"/> — today's behaviour — so an unknown shape degrades to the byte-identical status quo rather
/// than crashing a launch or silently disarming an oracle.</para>
/// </summary>
public static class DeliverableShapes
{
    /// <summary>The user wants an explanation / answer in chat — no files, no code change.</summary>
    public const string Answer = "answer";

    /// <summary>A written deliverable FILE — a report, a design doc, an RFC, a plan.</summary>
    public const string Document = "document";

    /// <summary>A code change — the historical default, and what every task was assumed to be before this axis existed.</summary>
    public const string Code = "code";

    /// <summary>Investigate / read-only findings — analysis whose product is what was learned, not a diff.</summary>
    public const string Research = "research";

    /// <summary>The known shapes, in the order a UI / prompt should list them.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Answer, Document, Code, Research };

    /// <summary>The shape a value denotes — trimmed + case-insensitive; anything blank or outside <see cref="All"/> folds to <see cref="Code"/> (the conservative status quo, never a silently disarmed oracle).</summary>
    public static string Normalize(string? shape)
    {
        var normalized = shape?.Trim().ToLowerInvariant();

        return normalized is not null && All.Contains(normalized) ? normalized : Code;
    }

    /// <summary>
    /// The <c>agent.run</c> MODE a shape projects onto — the node's own two-value vocabulary (<c>research</c> /
    /// <c>code</c>). <see cref="Answer"/> / <see cref="Research"/> ⇒ <c>research</c> (network off, nothing published
    /// by default); <see cref="Document"/> ⇒ <c>code</c> (a written file IS a workspace write, and its branch is the
    /// deliverable); <see cref="Code"/> ⇒ null ⇒ the key is omitted ⇒ today's tier-derived behaviour, byte-identical.
    /// </summary>
    public static string? AgentModeFor(string? shape) => Normalize(shape) switch
    {
        Answer or Research => "research",
        Document => "code",
        _ => null,
    };
}
