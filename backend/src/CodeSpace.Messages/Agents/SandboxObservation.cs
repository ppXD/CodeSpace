namespace CodeSpace.Messages.Agents;

/// <summary>
/// Capture facts independent of the command's exit outcome. Returned text is held in memory; these facts are not
/// a durable-storage receipt. An omitted stream retains the older runner's full-return contract, not evidence that
/// a new capture protocol completed. A consumer must preserve explicit incompleteness through any later offload.
/// </summary>
public sealed record SandboxObservation
{
    public SandboxStreamObservation? Stdout { get; init; }
    public SandboxStreamObservation? Stderr { get; init; }
}

/// <summary>Byte counts describe the source before text decoding; a stored UTF-8 excerpt may have a different length.</summary>
public sealed record SandboxStreamObservation
{
    /// <summary>Source bytes actually observed. A lower bound on total production until EOF is reached.</summary>
    public required long ObservedBytes { get; init; }

    /// <summary>True only after the reader observed EOF; process exit, a deadline, or closing our pipe is insufficient.</summary>
    public required bool ReachedEndOfStream { get; init; }

    /// <summary>
    /// True only when the corresponding SandboxResult text retains all observed source content. A budget cut,
    /// decode loss, or stdout delivered only to a streaming callback sets false. Complete output requires EOF too.
    /// </summary>
    public required bool CaptureComplete { get; init; }

    /// <summary>Streaming callbacks only: whether all observed records were delivered without truncation or decode loss. Null for batch capture. Full source delivery additionally requires EOF.</summary>
    public bool? DeliveryComplete { get; init; }
}
