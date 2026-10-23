namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

/// <summary>
/// No header means no idempotency key: deduplication belongs to the caller. Identical bodies can be retries or
/// distinct heartbeats and periodic alerts; any server-derived fallback key would silently swallow real events.
/// </summary>
public static class ExternalTriggerIdempotency
{
    public const int MaxLength = 200;

    public static bool IsWellFormed(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxLength &&
        value.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '~' or '-');

    /// <summary>
    /// Scope follows the durable trigger row, not the activation row: ReplaceActivationsAsync recreates activations on
    /// every workflow save, so an activation-scoped key would stop deduplicating after an ordinary edit.
    /// </summary>
    public static string KeyFor(string value, Guid triggerId) => $"{ExternalTriggerSlug.TypeKey}:{value}:{triggerId:N}";
}
