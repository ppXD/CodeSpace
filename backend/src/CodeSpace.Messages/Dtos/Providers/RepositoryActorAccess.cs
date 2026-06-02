namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Whether the probed credential's user can make attributable contributions (review / comment) to a
/// repository, plus an end-user reason when they can't — surfaced to the chat responder so they learn
/// WHY their click was refused. Provider-defined threshold (GitHub: accessible; GitLab: Developer+).
/// </summary>
public sealed record RepositoryActorAccess
{
    public required bool CanContribute { get; init; }

    /// <summary>End-user reason when <see cref="CanContribute"/> is false; null when they can contribute.</summary>
    public string? Reason { get; init; }

    public static RepositoryActorAccess Allowed { get; } = new() { CanContribute = true };

    public static RepositoryActorAccess Denied(string reason) => new() { CanContribute = false, Reason = reason };
}
