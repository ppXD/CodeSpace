using System.Text.Json;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// One row per configured activation source on a workflow (e.g. a webhook subscription,
/// a cron schedule, the "manual" enable flag). Surfaces the type key + config for the
/// editor's "When does this run?" sidebar.
/// </summary>
public sealed record WorkflowActivationSummary
{
    public required Guid Id { get; init; }
    public required string TypeKey { get; init; }
    public required bool Enabled { get; init; }
    public required JsonElement Config { get; init; }
}
