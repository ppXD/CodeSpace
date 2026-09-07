using System.Text.Json;

namespace CodeSpace.Core.Services.Tasks.SpecPreview;

/// <summary>Resolves a model-selected literal reference without guessing commands or rewriting tokens. Literal integrity and independent semantic support remain separate.</summary>
internal static class TaskSpecSourceArgv
{
    public static TaskSpecArgvResolution Resolve(TaskSpecCompilation compilation, TaskSpecEvidenceContext? context)
    {
        if (compilation.AcceptanceArgvSource is not { } reference) return new(compilation.AcceptanceChecks ?? [], null, null);
        const string error = "The argv source reference is missing, ambiguous, unsupported or malformed; no generated fallback can be adopted. If no executable check is being proposed, omit acceptanceArgvSource or use null and express non-executable requirements in acceptanceCriteria.";
        if (reference.Encoding != "json-argv" || string.IsNullOrWhiteSpace(reference.SourceId) || reference.SourceId.Length > 128 || string.IsNullOrWhiteSpace(reference.Quote) || reference.Quote.Length > 16_384 || context is null) return new([], null, error);
        var matches = context.Sources.Where(s => s.Id == reference.SourceId).Take(2).ToArray();
        if (matches.Length != 1 || matches[0].Kind is not ("user-goal" or "repository-file") || !matches[0].Content.Contains(reference.Quote, StringComparison.Ordinal)) return new([], null, error);
        try
        {
            var argv = JsonSerializer.Deserialize<string[]>(reference.Quote);
            if (argv is not { Length: > 0 and <= 256 } || string.IsNullOrWhiteSpace(argv[0]) || argv.Any(a => a is null || a.Contains('\0'))) return new([], null, error);
            return new(argv, matches[0], null);
        }
        catch (JsonException) { return new([], null, error); }
    }

    public static IReadOnlyList<string> ValidateResponse(JsonElement response, TaskSpecEvidenceContext context)
    {
        try
        {
            var compilation = response.Deserialize<TaskSpecCompilation>(TaskSpecCompilerSchema.Options);
            if (compilation is null) return ["The task specification must be an object."];
            return Resolve(compilation, context).Error is { } error ? [error] : [];
        }
        catch (JsonException ex) { return [$"Task specification contract: {ex.Message}"]; }
    }
}

internal sealed record TaskSpecArgvResolution(IReadOnlyList<string> Argv, TaskSpecSource? Source, string? Error);
