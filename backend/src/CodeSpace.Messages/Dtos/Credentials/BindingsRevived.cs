namespace CodeSpace.Messages.Dtos.Credentials;

/// <summary>What a credential coming back to life brought back with it: the repositories its disconnect had turned Error, and the group hooks its failures had buried.</summary>
public sealed record BindingsRevived
{
    public static BindingsRevived None { get; } = new();

    public int RepositoryCount { get; init; }

    public int ConnectionWebhookCount { get; init; }

    public bool Any => RepositoryCount + ConnectionWebhookCount > 0;
}
