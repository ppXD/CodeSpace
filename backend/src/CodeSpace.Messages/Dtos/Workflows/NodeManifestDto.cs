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
}
