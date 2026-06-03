using System.Text.Json;
using CodeSpace.Messages.Dtos.Chat.Interactions;

namespace CodeSpace.Core.Services.Chat.Interactions;

/// <summary>
/// Builds one interaction COMPONENT kind from its authored config (the <c>component</c> object on a
/// chat.post_message, discriminated by <c>kind</c>). Adding a kind (poll, checklist, rating, …) is a new
/// class implementing this — chat.post_message and the registry don't change; it stays a thin shell.
/// Mirrors the IProviderAuthStrategy / IResolvePolicyStrategy registries.
/// </summary>
public interface IInteractionComponentFactory
{
    /// <summary>The <c>kind</c> discriminator this factory builds (e.g. "action_buttons", "form").</summary>
    string Kind { get; }

    /// <summary>Build the component from its config object; null if the config is empty/malformed (→ no interaction, a plain message).</summary>
    InteractionComponent? Build(JsonElement config);
}

/// <summary>
/// Resolves the right <see cref="IInteractionComponentFactory"/> for a component config's <c>kind</c> and
/// builds it. Null for a missing/unknown kind or malformed config — so a card the node can't build degrades
/// to a plain message rather than throwing.
/// </summary>
public interface IInteractionComponentRegistry
{
    InteractionComponent? Build(JsonElement componentConfig);
}
