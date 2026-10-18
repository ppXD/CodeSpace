namespace CodeSpace.Messages.Dtos.Credentials;

/// <summary>What a move of one credential's bindings onto another carried: the live repositories re-pointed, and the group hooks re-pointed with them.</summary>
public sealed record BindingsMoved
{
    public static BindingsMoved None { get; } = new();

    public int RepositoryCount { get; init; }

    public int ConnectionWebhookCount { get; init; }

    public bool Any => RepositoryCount + ConnectionWebhookCount > 0;
}
