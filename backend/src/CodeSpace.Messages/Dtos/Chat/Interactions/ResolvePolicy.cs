using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Chat.Interactions;

/// <summary>
/// How an interaction RESOLVES the workflow wait it parks — the generic resolve contract. Each terminal
/// action click is evaluated against this: <see cref="ResolvePolicyKind.First"/> resolves on the first
/// click (today's default, single-responder); <see cref="ResolvePolicyKind.Quorum"/> waits for
/// <see cref="Count"/> DISTINCT responders of the same action key. A button marked
/// <see cref="InteractionButton.Vetoes"/> always short-circuits (resolves immediately) regardless of the
/// policy — e.g. one "request changes" blocks even under a 2-approval quorum.
///
/// New policies (unanimous, weighted, deadline, …) plug in as another <c>IResolvePolicyStrategy</c> keyed
/// by <see cref="Kind"/> — no change to this record or the respond path. A pre-existing card with no
/// stored policy reads the default (<see cref="ResolvePolicyKind.First"/>), so behaviour is unchanged.
/// </summary>
public sealed record ResolvePolicy
{
    public ResolvePolicyKind Kind { get; init; } = ResolvePolicyKind.First;

    /// <summary>For <see cref="ResolvePolicyKind.Quorum"/>: distinct responders of one action key needed to resolve. Ignored for First.</summary>
    public int Count { get; init; } = 1;
}

/// <summary>Resolve-policy kind. Serialized as a string for a stable, readable wire value; extensible — a new kind is a new IResolvePolicyStrategy.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResolvePolicyKind
{
    First,
    Quorum,
}
