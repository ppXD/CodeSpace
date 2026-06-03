using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Chat.Interactions;

namespace CodeSpace.Core.Services.Chat.Interactions;

/// <summary>
/// Builds a <see cref="FormComponent"/> from <c>{ "kind": "form", "fields": { …json schema… }, "submitLabel": "…" }</c>.
/// Returns null if <c>fields</c> is missing/not an object — so an incomplete form posts a plain message.
/// </summary>
public sealed class FormComponentFactory : IInteractionComponentFactory, ISingletonDependency
{
    public string Kind => "form";

    public InteractionComponent? Build(JsonElement config)
    {
        if (!config.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object) return null;

        var submitLabel = config.TryGetProperty("submitLabel", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "Submit";

        return new FormComponent { Fields = fields.Clone(), SubmitLabel = submitLabel };
    }
}
