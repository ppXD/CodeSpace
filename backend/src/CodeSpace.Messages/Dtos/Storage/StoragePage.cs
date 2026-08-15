namespace CodeSpace.Messages.Dtos.Storage;

public static class StoragePageLimits
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;
}

/// <summary>One bounded keyset page from a stable-name ordered storage Settings collection.</summary>
public sealed record StoragePage<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public string? NextCursor { get; init; }
}
