using System.Text.Json;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// API-exposed view of a node's manifest. The engine's internal <c>NodeManifest</c> record
/// uses JsonSchema documents that aren't directly serialisable; this DTO embeds them as
/// raw <see cref="JsonElement"/>s the frontend renders into config forms.
/// </summary>
public sealed record NodeManifestDto
{
    public required string TypeKey { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public required NodeKind Kind { get; init; }
    public string? Description { get; init; }
    public string? IconKey { get; init; }

    public required JsonElement ConfigSchema { get; init; }
    public required JsonElement InputSchema { get; init; }
    public required JsonElement OutputSchema { get; init; }

    /// <summary>
    /// True for an on-demand trigger (e.g. <c>trigger.manual</c>) that starts runs by hand/API
    /// rather than by subscribing to an event. The editor uses this to skip creating a
    /// <c>workflow_activation</c> row and to surface a "Run now" input form. Default false.
    /// </summary>
    public bool IsManual { get; init; }

    /// <summary>
    /// True when the node has external side effects (creates/modifies remote state — opens a PR, comments,
    /// merges, runs a command). The editor badges it "Writes" so an author sees at a glance which steps act.
    /// </summary>
    public bool IsSideEffecting { get; init; }

    /// <summary>
    /// True when the node can SUSPEND the run (an agent run, a human decision, a sleep, a sub-workflow). The
    /// editor badges it "Waits" so an author knows the step pauses rather than completing inline.
    /// </summary>
    public bool CanSuspend { get; init; }

    /// <summary>
    /// True when the node always parks on a human-approval gate before its effect. The editor badges it
    /// "Approval" so an author sees the step needs a person before it proceeds.
    /// </summary>
    public bool AlwaysRequiresApproval { get; init; }

    /// <summary>Author-facing starter templates the editor offers as "start from a template". Null/empty ⇒ none.</summary>
    public IReadOnlyList<NodePresetDto>? Presets { get; init; }
}

/// <summary>API view of one node starter template — a named (Config, Inputs) pair the editor applies on pick.</summary>
public sealed record NodePresetDto
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public required JsonElement Config { get; init; }
    public required JsonElement Inputs { get; init; }
}
