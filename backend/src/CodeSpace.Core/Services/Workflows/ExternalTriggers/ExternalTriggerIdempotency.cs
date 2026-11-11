namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

/// <summary>
/// No idempotency header and no dedupe query key means no deduplication: identical bodies can be retries or distinct
/// heartbeats and periodic alerts, so the caller must decide when they represent one event.
/// </summary>
public static class ExternalTriggerIdempotency
{
    public const int MaxLength = 200;
    public const string DedupeKeyIdempotencyConflictError = "dedupeKey and Idempotency-Key cannot be used together";
    public const string DuplicateDedupeKeyError = "dedupeKey must be specified at most once";

    public static string DedupeKeyFormatError =>
        $"dedupeKey must be at most {MaxLength} characters from [A-Za-z0-9._~-]";

    /// <summary>
    /// Mutual exclusion wins over the query-shape checks; duplicate and empty values win over the character-set error.
    /// </summary>
    public static string? GetDedupeKeyError(string? dedupeKey, string? idempotencyKey, bool duplicateDedupeKey = false)
    {
        if (dedupeKey is null) return null;
        if (idempotencyKey is not null) return DedupeKeyIdempotencyConflictError;
        if (duplicateDedupeKey) return DuplicateDedupeKeyError;
        return IsWellFormed(dedupeKey) ? null : DedupeKeyFormatError;
    }

    public static bool IsWellFormed(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxLength &&
        value.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '~' or '-');

    /// <summary>
    /// Scope follows the durable trigger row, not the activation row: ReplaceActivationsAsync recreates activations on
    /// every workflow save, so an activation-scoped key would stop deduplicating after an ordinary edit.
    /// </summary>
    public static string KeyFor(string value, Guid triggerId) => $"{ExternalTriggerSlug.TypeKey}:{value}:{triggerId:N}";

    /// <summary>
    /// Event-level merge keys are intentionally not comparable to caller idempotency keys: a dedupe replay returns
    /// the first run without inspecting the body. The prefix keeps both scopes in one durable unique-key index while
    /// preventing a caller-supplied idempotency value from ever occupying the dedupe namespace.
    /// </summary>
    public static string DedupeKeyFor(string value, Guid triggerId) => $"dedupe:{value}:{triggerId:N}";
}
