using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Events.Pipeline;

public sealed class PipelineCompletedEvent : NormalizedEvent
{
    public required string ExternalPipelineId { get; init; }
    public required string Ref { get; init; }
    public required string Sha { get; init; }
    public required PipelineStatus Status { get; init; }
    public TimeSpan? Duration { get; init; }
    public required string WebUrl { get; init; }
}
