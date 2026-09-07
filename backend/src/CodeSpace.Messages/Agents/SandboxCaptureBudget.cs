namespace CodeSpace.Messages.Agents;

/// <summary>Engineering bounds on in-memory command observation, independent of the task's execution/token budget. Overflow is reported as lost capture; the command continues and its exit status is preserved.</summary>
public sealed record SandboxCaptureBudget
{
    public const int DefaultBytes = 1_048_576;
    public const int MaximumBytes = 16 * 1_048_576;
    public int StdoutBytes { get; init; } = DefaultBytes;
    public int StderrBytes { get; init; } = DefaultBytes;
    /// <summary>Streaming callback lines larger than this raw UTF-8 bound (excluding LF, including a preceding CR) are omitted as whole records and reported through DeliveryComplete=false. Never present a partial protocol record as a full line.</summary>
    public int StdoutLineBytes { get; init; } = DefaultBytes;
}
