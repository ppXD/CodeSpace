namespace CodeSpace.Messages.Dtos.Repositories;

/// <summary>
/// The decrypted signing secret for one webhook — what an operator re-enters at the provider when
/// a hook has to be re-created by hand, and what every inbound delivery is authenticated against.
///
/// <para>Its own DTO on its own endpoint, rather than a field on
/// <see cref="RepositoryWebhookDetail"/>: whoever holds this value can forge a signed delivery for
/// the repository, so it must be something the operator asks for, not something that arrives
/// because a tab was opened.</para>
/// </summary>
public sealed record RepositoryWebhookSecret
{
    public required Guid WebhookId { get; init; }

    public required string Secret { get; init; }
}
