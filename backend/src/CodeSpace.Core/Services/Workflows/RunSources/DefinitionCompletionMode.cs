using CodeSpace.Messages.Dtos.Workflows;
using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.RunSources;

/// <summary>
/// P2b (Enforced cohort): reads a frozen definition JSON's <see cref="WorkflowDefinition.CompletionMode"/> opt-in —
/// the one input <see cref="Completion.CompletionPolicy.StampModeFor"/> needs at run staging. Both starters resolve
/// through here so the stored-workflow lane and the snapshot/replay lane can never diverge on how the opt-in is
/// read. Unreadable JSON returns null (the platform default): such a definition cannot execute at all, so no
/// enforcement claim is being silently weakened — while a READABLE definition carrying an unknown mode value still
/// reaches the policy's fail-closed throw.
/// </summary>
public static class DefinitionCompletionMode
{
    public static string? Read(string? definitionJson)
    {
        if (string.IsNullOrWhiteSpace(definitionJson)) return null;

        try
        {
            return JsonSerializer.Deserialize<WorkflowDefinition>(definitionJson, WorkflowJson.Options)?.CompletionMode;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
