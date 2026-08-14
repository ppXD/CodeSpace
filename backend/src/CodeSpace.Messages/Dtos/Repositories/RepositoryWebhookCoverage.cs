using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Repositories;

/// <summary>
/// What is delivering this repository's events, when it is not one of its own hooks.
///
/// <para>Under connection-wide scope the repository has NO <c>repository_webhook</c> row — the group
/// hook covers it — and a Webhook tab reading only that table shows an empty page for a repository
/// that is working perfectly. That blankness is the exact invisibility the tab exists to end, so the
/// covering hook is named here instead.</para>
///
/// <para>The hook is projected into <see cref="RepositoryWebhookDetail"/> rather than a shape of its
/// own. It is the same lifecycle, the same attempt timeline, and the same questions an operator asks
/// of it, so it gets the same vocabulary and the page can put it through the same reader — a
/// parallel set of words for "Registering" and "DeadLettered" would be two things to learn and two
/// things to get subtly different.</para>
/// </summary>
public sealed record RepositoryWebhookCoverage
{
    /// <summary>The mode the repository's connection is in. <c>Repository</c> means the list of its own hooks is the whole answer and nothing here adds to it.</summary>
    public required ProviderWebhookScope Scope { get; init; }

    /// <summary>The group / organization the covering hook sits on. Null under per-repository scope, and null when connection-wide scope has no hook covering this repository yet.</summary>
    public string? OwnerPath { get; init; }

    /// <summary>
    /// The covering hook. Null when the connection is connection-wide but nothing covers this
    /// repository — which is a real and reportable state (registration never ran, or it was retired),
    /// not an empty answer to skip over.
    /// </summary>
    public RepositoryWebhookDetail? Hook { get; init; }
}
