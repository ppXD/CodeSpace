using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// What an agent's tool call will do, resolved server-side from its arguments before the call is parked for a human's
/// approval: each argument as the tool will read it, the repository it names by its path, and for a pull request its
/// title, head and base. The approval card renders it, the ledger row keeps it (redacted and bounded), and the run's
/// tool-call audit shows it, so whoever approves sees what they approve and the record shows what they saw.
/// </summary>
public sealed record ToolCallPreview
{
    /// <summary>
    /// The arguments a reviewer's rejection sticks to: the call's target, normalised as the tool reads it and as the card
    /// shows it — for a merge its repository and pull request at the head and base the card pinned, whatever method or
    /// commit text it names. A rejected target is not asked again in the same run, and while one call on a target awaits a
    /// reviewer no other is put to one. Server-side only: hashed onto the ledger row, never persisted or shown as is.
    /// </summary>
    [JsonIgnore]
    public JsonElement Target { get; init; }

    /// <summary>The summary lines, in the order the card shows them.</summary>
    public IReadOnlyList<ToolCallPreviewLine> Lines { get; init; } = [];

    /// <summary>
    /// Inputs the approved call runs with, fixed to what the reviewer saw (input key → value): a merge's
    /// <c>expectedHeadSha</c> and <c>expectedBaseBranch</c> are the head and base the card showed, so a head that moves or
    /// a base retargeted after approval fails instead of merging commits nobody reviewed, or into a branch nobody approved.
    /// Applied over the call's own arguments when it executes.
    /// </summary>
    public IReadOnlyDictionary<string, string> Pins { get; init; } = new Dictionary<string, string>();
}

/// <summary>One line of a <see cref="ToolCallPreview"/>: what it names, the value, and whether it reaches outside the run.</summary>
public sealed record ToolCallPreviewLine
{
    public required string Label { get; init; }

    public required string Value { get; init; }

    /// <summary>True when the value names something outside the repositories the run is bound to — a fork's head, say — so the reviewer sees it flagged.</summary>
    public bool OutsideRun { get; init; }

    /// <summary>
    /// True when the value is one of the call's own arguments — what it sends or runs — so it is shown whole: never cut and
    /// never dropped, since a reviewer approves exactly it. A value the platform read to explain the call (a pull request's
    /// title) is bounded instead. Shapes the preview before it is stored; not stored itself.
    /// </summary>
    [JsonIgnore]
    public bool Whole { get; init; }
}
