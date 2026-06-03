using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Chat.Interactions;

namespace CodeSpace.Core.Services.Chat.Interactions;

public sealed class InteractionComponentRegistry : IInteractionComponentRegistry, ISingletonDependency
{
    private readonly IReadOnlyDictionary<string, IInteractionComponentFactory> _byKind;

    public InteractionComponentRegistry(IEnumerable<IInteractionComponentFactory> factories) => _byKind = factories.ToDictionary(f => f.Kind);

    public InteractionComponent? Build(JsonElement componentConfig)
    {
        if (componentConfig.ValueKind != JsonValueKind.Object || !componentConfig.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) return null;

        return _byKind.TryGetValue(kind.GetString()!, out var factory) ? factory.Build(componentConfig) : null;
    }
}
